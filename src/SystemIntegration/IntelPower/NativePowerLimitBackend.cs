using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace XiControl.SystemIntegration;

/// <summary>Узкий C ABI к штатному Intel IPF R5. Никаких IOCTL/MSR, произвольных путей или команд.</summary>
public sealed class NativePowerLimitBackend : IPowerLimitBackend, IDisposable
{
    private IntPtr _library, _session;
    private OpenFn? _open;
    private ReadFn? _read;
    private SetFn? _set;
    private CloseFn? _close;
    private ErrorFn? _error;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public PowerLimitSnapshot Read()
    {
        EnsureOpen();
        uint needed = 0;
        int rc = _read!(_session, null, 0, out needed);
        if (rc != 2 || needed is 0 or > 65536) throw Failure("IPF read size", rc);
        // JSON содержит живую телеметрию: между sizing и GET число может стать длиннее.
        // Запас и ограниченный повтор, а не ложная ошибка записи при BUFFER_REQUIRED.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint capacity = Math.Min(65536u, Math.Max(4096u, needed + 1024u));
            var bytes = new byte[capacity];
            rc = _read(_session, bytes, capacity, out uint actual);
            if (rc == 2 && actual is > 0 and <= 65536) { needed = actual; continue; }
            if (rc != 0 || actual is 0 || actual > capacity) throw Failure("IPF read", rc);
            return PowerLimitSnapshot.Parse(Encoding.UTF8.GetString(bytes, 0, checked((int)actual - 1)));
        }
        throw new InvalidOperationException("IPF response kept growing beyond the bounded read buffer");
    }

    public void SetLimit(int index, int milliwatts)
    {
        if (!IsElevated) throw new UnauthorizedAccessException("Intel power control requires an elevated administrator token");
        EnsureOpen();
        int rc = _set!(_session, index, milliwatts);
        if (rc != 0) throw Failure("IPF set", rc);
    }

    private void EnsureOpen()
    {
        if (_session != IntPtr.Zero) return;
        try
        {
            // Sidecar лежит только рядом с приложением; CWD, PATH и %TEMP% не участвуют.
            string bridge = Path.Combine(AppContext.BaseDirectory, "XiIntelPower.dll");
            if (!File.Exists(bridge)) throw new FileNotFoundException("XiIntelPower.dll is missing; extract the complete fork package", bridge);
            _library = LoadLibraryEx(bridge, IntPtr.Zero, 0x100 | 0x800); // DLL_LOAD_DIR | SYSTEM32
            if (_library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _open = Export<OpenFn>("XiPowerOpen");
            _read = Export<ReadFn>("XiPowerRead");
            _set = Export<SetFn>("XiPowerSetLimit");
            _close = Export<CloseFn>("XiPowerClose");
            _error = Export<ErrorFn>("XiPowerLastError");
            int rc = _open(InstalledProxyPath(), out _session);
            if (rc != 0 || _session == IntPtr.Zero) throw Failure("IPF open", rc);
        }
        catch { Dispose(); throw; }
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private InvalidOperationException Failure(string operation, int rc) =>
        new($"{operation} ({rc}): {(_error is null ? null : Marshal.PtrToStringUTF8(_error()))}");

    private static string InstalledProxyPath()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ipfsvc");
        string image = service?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? throw new NotSupportedException("Intel IPF service is not installed");
        image = Environment.ExpandEnvironmentVariables(image.Trim());
        if (image.StartsWith('"')) image = image[1..image.IndexOf('"', 1)];
        else if (image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is int end && end >= 0) image = image[..(end + 4)];
        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), image[12..]);
        string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(image)!, "ClientApiProxy.dll"));
        string driverStore = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository") + Path.DirectorySeparatorChar;
        if (!file.StartsWith(driverStore, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            throw new NotSupportedException("Registered Intel IPF proxy is not in the system DriverStore");
        return file; // подпись и revision проверит native loader ДО factory
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero) { _close?.Invoke(_session); _session = IntPtr.Zero; }
        if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
        _open = null; _read = null; _set = null; _close = null; _error = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int OpenFn([MarshalAs(UnmanagedType.LPWStr)] string path, out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadFn(IntPtr session, [Out] byte[]? buffer, uint capacity, out uint needed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetFn(IntPtr session, int index, long milliwatts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseFn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ErrorFn();
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
}
