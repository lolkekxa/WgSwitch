using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WgSwitch.Services;

/// <summary>Немного Win32: тёмная рамка DWM, опрос служб через SCM, сброс рабочего набора.</summary>
internal static class Native
{
    // ---------- окно ----------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Win11: скруглённые углы, тёмный режим и рамка цвета line (#1f1f1f). На Win10 тихо игнорируется.</summary>
    internal static void StyleWindow(IntPtr hwnd)
    {
        int dark = 1, round = 2 /* DWMWCP_ROUND */, border = 0x001F1F1F /* COLORREF 0x00BBGGRR */;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
    }

    // ---------- память ----------

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Отдать ОС страницы, которые не нужны в трее (окно уже уничтожено).</summary>
    internal static void TrimWorkingSet()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    // ---------- службы ----------

    internal enum ServiceState { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4, ContinuePending = 5, PausePending = 6, Paused = 7 }

    internal readonly record struct ServiceInfo(ServiceState State, int ProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
            dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out SERVICE_STATUS_PROCESS status, int size, out int needed);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private const int SC_MANAGER_CONNECT = 0x1, SERVICE_QUERY_STATUS = 0x4, ERROR_SERVICE_DOES_NOT_EXIST = 1060;

    /// <summary>Состояние службы или null, если её нет. Дёшево: без WMI и без ServiceController.</summary>
    internal static ServiceInfo? QueryService(string name)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_SERVICE_DOES_NOT_EXIST) return null;
                throw new Win32Exception(err);
            }
            try
            {
                if (!QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, out var s, Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
                    throw new Win32Exception();
                return new ServiceInfo((ServiceState)s.dwCurrentState, s.dwProcessId);
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
