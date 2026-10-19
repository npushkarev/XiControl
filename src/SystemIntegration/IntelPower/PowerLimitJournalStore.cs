using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using XiControl.Wmi;

namespace XiControl.SystemIntegration;

/// <summary>Журнал отката в ProgramData, а не пользовательском config: фальшивый backup не должен поднять мощность.</summary>
public sealed class PowerLimitJournalStore : IPowerLimitJournal
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XiControl", "IntelPower");
    private string FilePath => Path.Combine(_directory,
        $"restore-{WindowsIdentity.GetCurrent().User?.Value ?? "unknown"}.json");

    public PowerLimitJournal? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var owner = new FileInfo(FilePath).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (!Admins.Equals(owner) && !SystemSid.Equals(owner))
            throw new UnauthorizedAccessException("Power-limit recovery journal is not owned by Administrators or SYSTEM");
        if (new FileInfo(FilePath).Length > 4096) throw new InvalidDataException("Invalid power recovery journal size");
        var journal = JsonSerializer.Deserialize<PowerLimitJournal>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("Empty power recovery journal");
        if (journal.OriginalPl1 is < 1000 or > 250000 || journal.OriginalPl2 is < 1000 or > 250000
            || journal.TargetPl1 is < 1000 or > 250000 || journal.TargetPl2 is < 1000 or > 250000
            || journal.OriginalPl1 > journal.OriginalPl2
            || journal.TargetPl1 > journal.OriginalPl1 || journal.TargetPl2 > journal.OriginalPl2)
            throw new InvalidDataException("Invalid power recovery journal limits");
        return journal;
    }

    public void Save(PowerLimitJournal journal)
    {
        if (journal.Mode is PerfMode mode && !Enum.IsDefined(mode))
            throw new InvalidDataException("Invalid power recovery journal mode");
        if (!NativePowerLimitBackend.IsElevated) throw new UnauthorizedAccessException("Power recovery journal requires administrator rights");
        Directory.CreateDirectory(_directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(Admins);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(security);
        string temporary = FilePath + ".new";
        if (File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Recovery journal must not be a link");
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal));
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(true, false);
        fileSecurity.SetOwner(Admins);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        fileSecurity.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(temporary).SetAccessControl(fileSecurity);
        File.Move(temporary, FilePath, overwrite: true); // atomic rename в защищённом каталоге
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
