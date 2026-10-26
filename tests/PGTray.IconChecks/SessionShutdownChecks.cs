using System.Reflection;
using PGTray;

namespace PGTray.IconChecks;

internal static class SessionShutdownChecks
{
    public static void Run(string output, Action<bool, string> check)
    {
        using var form = new Form(); _ = form.Handle;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = new System.Windows.Forms.Timer { Interval = 25 };
        timer.Tick += (_, _) => { timer.Stop(); completion.TrySetResult(); };
        timer.Start();
        check(WindowsSessionShutdown.CompleteWithin(completion.Task, TimeSpan.FromSeconds(2)),
            "Session-end cleanup pumps UI messages so existing asynchronous continuations can finish");
        var pending = new TaskCompletionSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        check(!WindowsSessionShutdown.CompleteWithin(pending.Task, TimeSpan.FromMilliseconds(50)) && clock.Elapsed < TimeSpan.FromSeconds(1),
            "Session-end cleanup has a bounded deadline instead of hanging Windows shutdown");
        var failed = Task.FromException(new IOException("fixture failure"));
        check(WindowsSessionShutdown.CompleteWithin(failed, TimeSpan.FromSeconds(1)) && failed.Exception is not null,
            "Session-end cleanup preserves faults for diagnostics");

        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var main = new MainForm(wake, new UiPreferences(Path.Combine(output, "session-preferences")));
        _ = main.Handle;
        var wndProc = typeof(MainForm).GetMethod("WndProc", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var query = new object[] { Message.Create(main.Handle, WindowsSessionShutdown.QueryEndSession, IntPtr.Zero, IntPtr.Zero) };
        wndProc.Invoke(main, query);
        check(((Message)query[0]).Result == new IntPtr(1) && !main.IsDisposed,
            "A shutdown query is accepted without disposing the window or stopping databases");
        wndProc.Invoke(main, [Message.Create(main.Handle, WindowsSessionShutdown.EndSession, IntPtr.Zero, IntPtr.Zero)]);
        check(!(bool)typeof(MainForm).GetField("sessionEnding", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)! && !main.IsDisposed,
            "A cancelled Windows shutdown leaves PgHatch operational");

        var manager = new PGTray.Core.InstanceManager(new PGTray.Core.ConfigStore(Path.Combine(output, "session-empty")), new PGTray.Core.PostgresTools(new PGTray.Core.ProcessRunner()));
        using var download = new DownloadForm(manager, new PGTray.Core.ConfigStore(Path.Combine(output, "session-download")));
        typeof(DownloadForm).GetField("busy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(download, true);
        var closing = typeof(Form).GetMethod("OnFormClosing", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var args = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
        closing.Invoke(download, [args]);
        check(!args.Cancel, "A busy download dialog does not veto the Windows session-end query");
        args = new(CloseReason.UserClosing, false); closing.Invoke(download, [args]);
        check(args.Cancel, "User-closing a busy download retains its cancellation-and-wait behavior");
        download.Dispose(); form.Dispose();
        // Deliver only to this isolated form. This does not request an actual Windows shutdown.
        var sessionRoot = Path.GetFullPath(Path.Combine(output, "confirmed-session"));
        typeof(MainForm).GetField("store", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, new PGTray.Core.ConfigStore(sessionRoot));
        try
        {
            wndProc.Invoke(main, [Message.Create(main.Handle, WindowsSessionShutdown.EndSession, new IntPtr(1), IntPtr.Zero)]);
            check(main.IsDisposed && File.ReadAllText(Path.Combine(sessionRoot, "logs", "pghatch-session-end.log")).Contains("Session cleanup completed."),
                "A simulated confirmed session end records cleanup and closes PgHatch instead of hiding to the tray");
        }
        finally { WindowsSessionShutdown.IsEnding = false; }
    }
}
