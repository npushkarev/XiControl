using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XiControl.Wmi;

namespace XiControl.SystemIntegration;

public sealed record PowerModeProfile(
    [property: JsonRequired] PerfMode Mode,
    [property: JsonRequired] bool OnAc,
    [property: JsonRequired] int Pl1Watts,
    [property: JsonRequired] int Pl2Watts);

public interface IPowerModeProfiles
{
    IReadOnlyList<PowerModeProfile> Load();
    void Save(IReadOnlyList<PowerModeProfile> entries);
}

/// <summary>Только пресеты: реальные capabilities и baseline проверяются отдельно перед записью мощности.</summary>
public static class PowerModeProfiles
{
    internal const int MaximumJsonLength = 16384;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static void Validate(IReadOnlyList<PowerModeProfile> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > 12) throw new InvalidDataException("Too many power-mode profiles");
        var keys = new HashSet<(PerfMode, bool)>();
        foreach (var entry in entries)
        {
            if (entry is null) throw new InvalidDataException("Null power-mode profile");
            ValidateMode(entry.Mode);
            if (entry.Pl1Watts is < 10 or > 250 || entry.Pl2Watts is < 10 or > 250 || entry.Pl1Watts > entry.Pl2Watts)
                throw new InvalidDataException("Invalid power-mode profile limits");
            if (!keys.Add((entry.Mode, entry.OnAc))) throw new InvalidDataException("Duplicate power-mode profile");
        }
    }

    public static IReadOnlyList<PowerModeProfile> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumJsonLength) throw new InvalidDataException("Invalid power-mode profiles size");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Power-mode profiles must be an array");
        if (document.RootElement.GetArrayLength() > 12) throw new InvalidDataException("Too many power-mode profiles");
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Power-mode profile must be an object");
            // System.Text.Json иначе принимает последнее значение повторённого поля.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate power-mode profile property");
                if (property.Name == nameof(PowerModeProfile.Mode)
                    && (property.Value.ValueKind != JsonValueKind.String
                        || !Enum.GetNames<PerfMode>().Contains(property.Value.GetString(), StringComparer.OrdinalIgnoreCase)))
                    throw new JsonException("Power-mode profile requires a named performance mode");
            }
        }
        var entries = document.RootElement.Deserialize<List<PowerModeProfile>>(JsonOptions)
            ?? throw new JsonException("Null power-mode profiles");
        Validate(entries);
        return entries.AsReadOnly();
    }

    public static PowerModeProfile? Find(IReadOnlyList<PowerModeProfile> entries, PerfMode mode, bool onAc)
    {
        Validate(entries);
        ValidateMode(mode);
        return entries.FirstOrDefault(entry => entry.Mode == mode && entry.OnAc == onAc);
    }

    public static IReadOnlyList<PowerModeProfile> Upsert(IReadOnlyList<PowerModeProfile> entries, PowerModeProfile profile)
    {
        Validate(entries);
        ArgumentNullException.ThrowIfNull(profile);
        Validate([profile]);
        var result = entries.ToList();
        int index = result.FindIndex(entry => entry.Mode == profile.Mode && entry.OnAc == profile.OnAc);
        if (index < 0) result.Add(profile);
        else result[index] = profile;
        Validate(result);
        return result.AsReadOnly();
    }

    public static IReadOnlyList<PowerModeProfile> Remove(IReadOnlyList<PowerModeProfile> entries, PerfMode mode, bool onAc)
    {
        Validate(entries);
        ValidateMode(mode);
        return Array.AsReadOnly(entries.Where(entry => entry.Mode != mode || entry.OnAc != onAc).ToArray());
    }

    internal static string Serialize(IReadOnlyList<PowerModeProfile> entries)
    {
        Validate(entries);
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    private static void ValidateMode(PerfMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new InvalidDataException("Unknown performance mode in power-mode profile");
    }
}

/// <summary>Персональные пресеты под административным ACL, не в пользовательском config.json.</summary>
public sealed class PowerModeProfilesStore : IPowerModeProfiles
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XiControl");
    private string DirectoryPath => Path.Combine(_root, "IntelPower");

    private string FilePath
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new UnauthorizedAccessException("Power-mode profiles require a user SID");
            return Path.Combine(DirectoryPath, $"profiles-{sid}.json");
        }
    }

    public IReadOnlyList<PowerModeProfile> Load()
    {
        if (!Exists(_root)) return Array.Empty<PowerModeProfile>();
        CheckDirectory(_root);
        if (!Exists(DirectoryPath)) return Array.Empty<PowerModeProfile>();
        CheckDirectory(DirectoryPath);
        string path = FilePath;
        if (!Exists(path)) return Array.Empty<PowerModeProfile>();
        CheckFile(path);
        if (new FileInfo(path).Length > PowerModeProfiles.MaximumJsonLength)
            throw new InvalidDataException("Invalid power-mode profiles size");
        return PowerModeProfiles.Parse(File.ReadAllText(path));
    }

    public void Save(IReadOnlyList<PowerModeProfile> entries)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Power-mode profiles require administrator rights");
        string json = PowerModeProfiles.Serialize(entries);
        // Защищаем и родителя: право DeleteChild на нём позволило бы подменить весь IntelPower.
        ProtectDirectory(_root);
        ProtectDirectory(DirectoryPath);
        string path = FilePath;
        if (Exists(path)) CheckFile(path);
        string temporary = path + $".{Guid.NewGuid():N}.new";
        bool created = false;
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(Admins);
            security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.None, security))
            {
                created = true;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            CheckFile(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (created) File.Delete(temporary); }
    }

    private static void ProtectDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(Admins);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (Exists(path))
        {
            RejectLink(path);
            CheckOwner(directory.GetAccessControl());
        }
        else directory.Create(security);
        directory.SetAccessControl(security);
        CheckDirectory(path);
    }

    private static void CheckDirectory(string path)
    {
        RejectLink(path);
        CheckSecurity(new DirectoryInfo(path).GetAccessControl());
    }

    private static void CheckFile(string path)
    {
        RejectLink(path);
        CheckSecurity(new FileInfo(path).GetAccessControl());
    }

    private static void CheckSecurity(FileSystemSecurity security)
    {
        CheckOwner(security);
        if (!security.AreAccessRulesProtected) throw new UnauthorizedAccessException("Power-mode profiles ACL must be protected");
        const FileSystemRights readOnly = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & ~readOnly) != 0
                && !Admins.Equals(rule.IdentityReference) && !SystemSid.Equals(rule.IdentityReference))
                throw new UnauthorizedAccessException("Power-mode profiles allow non-administrator writes");
    }

    private static void CheckOwner(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!Admins.Equals(owner) && !SystemSid.Equals(owner))
            throw new UnauthorizedAccessException("Power-mode profiles must be owned by Administrators or SYSTEM");
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Power-mode profiles must not use links");
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
