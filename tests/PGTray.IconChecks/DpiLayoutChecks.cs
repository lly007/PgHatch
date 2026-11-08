using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class DpiLayoutChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Pump()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 150) { Application.DoEvents(); Thread.Sleep(5); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left, Top, Right, Bottom; }

    internal static void Run(string output, Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetFullPath(output), "dpi-invariants"); Directory.CreateDirectory(root);
        var store = new ConfigStore(root);
        var manager = new InstanceManager(store, new PostgresTools(new ProcessRunner()));
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var form = new MainForm(wake, new UiPreferences(root));
        typeof(MainForm).GetField("store", Private)!.SetValue(form, store);
        typeof(MainForm).GetField("manager", Private)!.SetValue(form, manager);
        form.Show();
        var load = Stopwatch.StartNew();
        while (!(bool)typeof(MainForm).GetField("loaded", Private)!.GetValue(form)! && load.Elapsed < TimeSpan.FromSeconds(5)) Pump();
        ((System.Windows.Forms.Timer)typeof(MainForm).GetField("timer", Private)!.GetValue(form)!).Stop();
        manager.Config.Instances.Add(new(Guid.NewGuid(), "DPI fixture", Guid.NewGuid(), Path.Combine(root, "missing-data"), 55499, Path.Combine(root, "fixture.log")));
        typeof(MainForm).GetMethod("Render", Private)!.Invoke(form, null); Pump();
        var viewport = form.Controls.OfType<Panel>().Single();
        var layout = viewport.Controls.OfType<TableLayoutPanel>().Single();
        var logo = (PictureBox)typeof(MainForm).GetField("brandLogo", Private)!.GetValue(form)!;
        var nativeDpi = form.DeviceDpi;
        var grid = (DataGridView)typeof(MainForm).GetField("grid", Private)!.GetValue(form)!;
        Console.WriteLine($"Native cell padding={grid.DefaultCellStyle.Padding}, header padding={grid.ColumnHeadersDefaultCellStyle.Padding}, DPI={nativeDpi}");
        var expectedRowHeight = Math.Max(DpiLayout.Pixels(28, nativeDpi), TextRenderer.MeasureText("Ag", form.Font).Height + DpiLayout.Pixels(16, nativeDpi));
        check(grid.RowTemplate.Height == expectedRowHeight, $"Cold-start measured row height is mapped once (actual={grid.RowTemplate.Height}, expected={expectedRowHeight})");
        check(grid.DefaultCellStyle.Padding.Left == DpiLayout.Pixels(4, nativeDpi), "Non-control cell style padding uses the common logical mapping");
        check(layout.Padding.Left == DpiLayout.Pixels(20, nativeDpi) && logo.Width == DpiLayout.Pixels(100, nativeDpi), "Cold start scales the complete 96-DPI design tree exactly once");
        check(form.AutoScaleMode == AutoScaleMode.Dpi && form.AutoScaleDimensions == new SizeF(nativeDpi, nativeDpi), "Cold start completes the DPI autoscale baseline");
        var largeWork = new Rectangle(-200, -100, 4000, 2600);
        form.ApplyWindowConstraints(largeWork, nativeDpi); Pump();
        check(form.ClientSize == DpiLayout.Pixels(new SizeF(1080, 700), nativeDpi), "Unconstrained cold start uses the logical design client size");
        var minimum = form.MinimumSize;
        var normal = form.Size;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var small = new Rectangle(50, 60, 880, 620);
            form.ApplyWindowConstraints(small, nativeDpi); Pump();
            Console.WriteLine($"Small work={small}, bounds={form.Bounds}, viewport={viewport.ClientSize}, virtual={viewport.DisplayRectangle}, scroll={viewport.HorizontalScroll.Visible}/{viewport.VerticalScroll.Visible}, layout={layout.Bounds}, min={layout.MinimumSize}");
            check(small.Contains(form.Bounds) && viewport.AutoScroll && viewport.HorizontalScroll.Visible && viewport.VerticalScroll.Visible,
                "Small work area bounds the window and scrolls the usable layout");
            check(!logo.Visible, "Small visible work area hides the secondary logo");
            var start = (Button)typeof(MainForm).GetField("start", Private)!.GetValue(form)!;
            viewport.ScrollControlIntoView(layout); Pump();
            check(layout.Width >= DpiLayout.Pixels(800, nativeDpi) && layout.Height >= DpiLayout.Pixels(540, nativeDpi) && start.Width > 0,
                "Scrolling preserves the minimum usable content geometry");
            form.ApplyWindowConstraints(largeWork, nativeDpi); Pump();
            check(form.Size == normal && form.MinimumSize == minimum, "Returning to a sufficient work area restores preferred size and minimum without shrink accumulation");
        }
        var userClient = DpiLayout.Pixels(new SizeF(960, 640), nativeDpi);
        form.ClientSize = userClient;
        typeof(MainForm).GetField("userSizing", Private)!.SetValue(form, true);
        typeof(MainForm).GetMethod("OnResizeEnd", Private)!.Invoke(form, [EventArgs.Empty]); Pump();
        for (var dpi = 96; dpi <= 216; dpi += 12)
        {
            form.ApplyWindowConstraints(largeWork, dpi);
            check(form.ClientSize == DpiLayout.Pixels(new SizeF(960, 640), dpi), $"User logical size survives work-area mapping at {dpi} DPI");
            form.ApplyWindowConstraints(new(20, 30, 650, 420), dpi);
            form.ApplyWindowConstraints(largeWork, dpi);
            check(form.ClientSize == DpiLayout.Pixels(new SizeF(960, 640), dpi), $"Temporary cap restores user size without cumulative rounding at {dpi} DPI");
        }
        // Synthetic WM_DPICHANGED exercises WinForms' actual scaling/event path, but does not
        // change Windows DPI, monitor configuration or the physical display's rendering scale.
        var wndProc = typeof(MainForm).GetMethod("WndProc", Private)!;
        var rectangle = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRectangle>());
        var measurements = new List<string>();
        try
        {
            foreach (var dpi in new[] { 96, 108, 120, 144, 168, 192, 168, 144, 120, 108, 96, nativeDpi, 192, nativeDpi })
            {
                var desired = DpiLayout.Pixels(new SizeF(960, 640), dpi) + form.Size - form.ClientSize;
                Marshal.StructureToPtr(new NativeRectangle { Left = 10, Top = 10, Right = 10 + desired.Width, Bottom = 10 + desired.Height }, rectangle, false);
                var message = Message.Create(form.Handle, 0x02E0, new IntPtr(dpi | dpi << 16), rectangle);
                wndProc.Invoke(form, [message]); Pump();
                measurements.Add($"synthetic DPI={dpi}, DeviceDpi={form.DeviceDpi}, client={form.ClientSize}, padding={layout.Padding}, logo={logo.Size}, font={form.Font.SizeInPoints}, minimum={form.MinimumSize}");
                // The OS nonclient frame remains at the physical monitor's DPI in a synthetic message.
                // Check the requested outer bounds here. Actual client geometry was checked above
                // without falsifying the OS DPI and must also be checked on real display transitions.
                var frame = (Size)typeof(Form).GetMethod("SizeFromClientSize", Private)!.Invoke(form, [Size.Empty])!;
                check(form.DeviceDpi == dpi && form.Size == DpiLayout.Pixels(new SizeF(960, 640), dpi) + frame, $"Synthetic DPI transition retains the user's requested logical size at {dpi}");
                check(Math.Abs(layout.Padding.Left - DpiLayout.Pixels(20, dpi)) <= 1 && Math.Abs(logo.Width - DpiLayout.Pixels(100, dpi)) <= 2,
                    $"Synthetic DPI transition preserves logical control geometry at {dpi}");
                var content = (TableLayoutPanel)layout.GetControlFromPosition(0, 2)!;
                Console.WriteLine($"Cell padding at synthetic {dpi}={grid.DefaultCellStyle.Padding}, header={grid.ColumnHeadersDefaultCellStyle.Padding}");
                check(grid.DefaultCellStyle.Padding == new Padding(DpiLayout.Pixels(4, dpi)) && grid.ColumnHeadersDefaultCellStyle.Padding == grid.DefaultCellStyle.Padding,
                    $"Non-control cell style geometry is mapped once at {dpi}");
                Console.WriteLine($"DPI geometry {dpi}: viewport={viewport.ClientSize}/{viewport.DisplayRectangle}, root={layout.Bounds}/{layout.MinimumSize}, content={content.Bounds}, columns={string.Join(",", content.GetColumnWidths())}, widths={content.ColumnStyles[0].Width}/{content.ColumnStyles[1].Width}, list={content.GetControlFromPosition(0, 0)!.Bounds}/{content.GetControlFromPosition(0, 0)!.MinimumSize}, logo={logo.Visible}");
                check(Math.Abs(content.GetColumnWidths()[0] / (double)content.Width - .42) < .02, $"Primary columns preserve their logical proportions at {dpi}");
                check(layout.ClientRectangle.Contains(content.Bounds) && content.Controls.Cast<Control>().All(control => content.ClientRectangle.Contains(control.Bounds)),
                    $"Content and both panels remain within their assigned layout bounds at {dpi}");
                check(content.GetControlFromPosition(0, 0)!.Width >= DpiLayout.Pixels(300, dpi) && content.GetControlFromPosition(1, 0)!.Width >= DpiLayout.Pixels(360, dpi),
                    $"List and details retain their declared usable widths at {dpi}");
                var headerHost = (TableLayoutPanel)layout.GetControlFromPosition(0, 0)!;
                var header = (TableLayoutPanel)headerHost.GetControlFromPosition(0, 0)!;
                var toolbar = header.Controls.OfType<FlowLayoutPanel>().Single();
                check(toolbar.Controls.Cast<Control>().All(button => toolbar.ClientRectangle.Contains(button.Bounds)), $"All toolbar actions fit their wrapping host at {dpi}");
                var occupied = header.Controls.Cast<Control>().Max(control => control.Bottom + control.Margin.Bottom);
                check(header.Height == occupied && headerHost.Height == Math.Max(occupied, logo.Visible ? logo.Height : 0), $"Header has no retained empty toolbar row at {dpi}");
                var start = (Button)typeof(MainForm).GetField("start", Private)!.GetValue(form)!;
                check(start.Image!.Width == UiIcons.Pixels(dpi), $"Button raster is selected once for the current DPI at {dpi}");
            }
        }
        finally { Marshal.FreeHGlobal(rectangle); File.WriteAllLines(Path.Combine(output, "dpi-invariant-measurements.txt"), measurements); }
        using var preview = new Bitmap(form.Width, form.Height); form.DrawToBitmap(preview, new(Point.Empty, preview.Size)); preview.Save(Path.Combine(output, "main-window-native-dpi.png"));
        form.Dispose();
        manager.ShutdownAsync().GetAwaiter().GetResult();
    }
}
