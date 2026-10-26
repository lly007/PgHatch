using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PGTray;

internal static class WindowsSessionShutdown
{
    internal const int QueryEndSession = 0x0011, EndSession = 0x0016;
    internal const long Logoff = 0x80000000L;
    internal static bool IsEnding { get; set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);

    internal static void SetPriority()
    {
        // Finish before ordinary applications/console processes (default level 0x280).
        if (!SetProcessShutdownParameters(0x300, 0)) Debug.WriteLine("Could not set PgHatch session-end priority: " + Marshal.GetLastWin32Error());
    }

    internal static void RegisterReason(IntPtr window, string reason) => ShutdownBlockReasonCreate(window, reason);
    internal static void ClearReason(IntPtr window) => ShutdownBlockReasonDestroy(window);

    internal static bool CompleteWithin(Task operation, TimeSpan timeout)
    {
        // WM_ENDSESSION is synchronous: returning before cleanup lets Windows terminate us.
        // Only this bounded session-end path pumps messages, so existing UI-context operations
        // (including initdb/configuration commits) can finish instead of deadlocking on a wait.
        var clock = Stopwatch.StartNew();
        while (!operation.IsCompleted && clock.Elapsed < timeout)
        {
            Application.DoEvents();
            if (operation.IsCompleted) break;
            var remaining = (uint)Math.Max(1, Math.Min(50, (timeout - clock.Elapsed).TotalMilliseconds));
            MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, remaining, 0x04FF, 0x0004);
        }
        return operation.IsCompleted;
    }
}
