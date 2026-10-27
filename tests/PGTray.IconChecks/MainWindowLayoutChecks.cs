using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class MainWindowLayoutChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Private)!.GetValue(form)!;
    private static void Set(MainForm form, string name, object value) => typeof(MainForm).GetField(name, Private)!.SetValue(form, value);
    private static void Pump()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 120) { Application.DoEvents(); Thread.Sleep(5); }
    }

    public static void Run(string output, Action<bool, string> check)
    {
        var culture = CultureInfo.CurrentUICulture;
        var measurements = new List<string>();
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                var root = Path.GetFullPath(Path.Combine(output, "main-layout", language));
                var store = new ConfigStore(root);
                var manager = new InstanceManager(store, new PostgresTools(new ProcessRunner()));
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
                using var form = new MainForm(signal, new UiPreferences(root));
                // Replace both objects before Shown: never load or poll real user instances.
                Set(form, "store", store); Set(form, "manager", manager);
                form.Show();
                var timeout = Stopwatch.StartNew();
                while (!Field<bool>(form, "loaded") && timeout.Elapsed < TimeSpan.FromSeconds(5)) Pump();
                check(Field<bool>(form, "loaded"), $"{language}: isolated main window loaded");
                Field<System.Windows.Forms.Timer>(form, "timer").Stop();
                var instance = new InstanceConfig(Guid.NewGuid(), "Layout fixture", Guid.NewGuid(), Path.Combine(root, "missing-data"), 55499, Path.Combine(root, "log"));
                manager.Config.Instances.Add(instance);
                typeof(MainForm).GetMethod("Render", Private)!.Invoke(form, null);
                Pump();
                var layout = form.Controls.OfType<TableLayoutPanel>().Single();
                var headerHost = (TableLayoutPanel)layout.GetControlFromPosition(0, 0)!;
                var header = (TableLayoutPanel)headerHost.GetControlFromPosition(0, 0)!;
                var toolbar = header.Controls.OfType<FlowLayoutPanel>().Single();
                var content = (TableLayoutPanel)layout.GetControlFromPosition(0, 2)!;
                var list = content.GetControlFromPosition(0, 0)!;
                var detail = Field<Panel>(form, "detailHost");
                string Snapshot(string stage)
                {
                    var text = $"{language}/{stage}: nativeDpi={form.DeviceDpi}, window={form.WindowState}, client={form.ClientSize}, headerRow={layout.RowStyles[0].Height}, host={headerHost.Bounds}, header={header.Bounds}, preferred={header.GetPreferredSize(new(header.Width, 0))}, toolbar={toolbar.Bounds}, content={content.Bounds}, left={list.Bounds}, right={detail.Bounds}, scroll={detail.VerticalScroll.Visible}";
                    measurements.Add(text); Console.WriteLine(text); return text;
                }
                Snapshot("initial");
                var initial = (content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible);
                for (var cycle = 1; cycle <= 5; cycle++)
                {
                    var rowHeight = layout.RowStyles[0].Height;
                    var logoWidth = headerHost.ColumnStyles[1].Width;
                    form.WindowState = FormWindowState.Minimized; Pump(); Snapshot($"minimized-{cycle}");
                    check(layout.RowStyles[0].Height == rowHeight && headerHost.ColumnStyles[1].Width == logoWidth,
                        $"{language}: minimized zero client area does not change header measurements");
                    form.WindowState = FormWindowState.Normal; Pump(); Snapshot($"restored-{cycle}");
                    check((content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible) == initial,
                        $"{language}: native minimize/restore cycle {cycle} preserves content and both panels exactly");
                }
                form.Close(); Pump();
                check(!form.Visible && !form.IsDisposed && Field<NotifyIcon>(form, "tray").Visible, $"{language}: close hides to tray");
                typeof(NotifyIcon).GetMethod("OnDoubleClick", Private)!.Invoke(Field<NotifyIcon>(form, "tray"), [EventArgs.Empty]); Pump(); Snapshot("tray-restored");
                check((content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible) == initial, $"{language}: tray restore preserves original layout after five cycles");
                form.WindowState = FormWindowState.Minimized; Pump(); form.Close(); Pump();
                typeof(NotifyIcon).GetMethod("OnDoubleClick", Private)!.Invoke(Field<NotifyIcon>(form, "tray"), [EventArgs.Empty]); Pump();
                check(form.Visible && form.WindowState == FormWindowState.Normal && (content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible) == initial,
                    $"{language}: tray double-click handler restores a hidden minimized window");
                form.WindowState = FormWindowState.Minimized; Pump(); signal.Set(); Pump();
                check(form.Visible && form.WindowState == FormWindowState.Normal && (content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible) == initial,
                    $"{language}: second-instance wake event preserves normal layout");
                var size = form.ClientSize;
                foreach (var width in new[] { form.MinimumSize.Width, size.Width + 100, size.Width })
                {
                    form.ClientSize = new(width, size.Height); Pump();
                    var before = (content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible);
                    form.WindowState = FormWindowState.Minimized; Pump();
                    form.WindowState = FormWindowState.Normal; Pump(); Snapshot($"resized-{width}");
                    check((content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible) == before, $"{language}: restore preserves layout at width {width}");
                }
                form.WindowState = FormWindowState.Maximized; Pump();
                var maximized = (content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible);
                form.WindowState = FormWindowState.Minimized; Pump();
                form.WindowState = FormWindowState.Maximized; Pump();
                check((content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible) == maximized, $"{language}: maximized restore retains its layout");
                form.WindowState = FormWindowState.Normal; Pump();
                check((content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible) == initial, $"{language}: returning from maximized retains original normal layout");

                // Exercise scaled control geometry without changing the desktop's DPI settings.
                // These are simulations, not a real monitor DPI transition.
                form.AutoScaleMode = AutoScaleMode.None;
                var previousDpi = form.DeviceDpi;
                foreach (var dpi in new[] { 96, 120, 144, 192 })
                {
                    var factor = dpi / (float)previousDpi;
                    form.Scale(new SizeF(factor, factor)); previousDpi = dpi; Pump();
                    var before = (content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible);
                    form.WindowState = FormWindowState.Minimized; Pump();
                    form.WindowState = FormWindowState.Normal; Pump(); Snapshot($"scaled-controls-{dpi}");
                    check((content.Bounds, list.Bounds, detail.Bounds, detail.VerticalScroll.Visible) == before, $"{language}/{dpi}: simulated scaled controls retain layout after native restore");
                }
                var card = Field<TableLayoutPanel>(form, "detailCard");
                var title = Field<Label>(form, "heading");
                title.Text = string.Join(Environment.NewLine, Enumerable.Repeat("Scrollable fixture", 40)); Pump();
                check(detail.VerticalScroll.Visible && detail.AutoScroll, $"{language}: genuinely overflowing details still scroll");
                detail.ScrollControlIntoView(card.Controls[card.Controls.Count - 1]); Pump();
                check(detail.AutoScrollPosition.Y < 0, $"{language}: detail scrolling remains functional");
                var quit = new KeyEventArgs(Keys.Control | Keys.Q);
                typeof(MainForm).GetMethod("OnKeyDown", Private)!.Invoke(form, [quit]); Pump();
                check(form.IsDisposed && !Field<NotifyIcon>(form, "tray").Visible, $"{language}: final close disposes window and tray");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
            File.WriteAllLines(Path.Combine(output, "main-layout-measurements.txt"), measurements);
        }
    }
}
