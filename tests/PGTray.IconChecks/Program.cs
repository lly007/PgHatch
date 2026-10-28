using System.Reflection;
using PGTray;

namespace PGTray.IconChecks;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--pipe-burst"]) { Console.Out.Write(new string('o', 2 * 1024 * 1024)); Console.Error.Write(new string('e', 2 * 1024 * 1024)); return 7; }
        if (args is ["--delay-helper"]) { Thread.Sleep(10000); return 0; }
        if (args is ["--about-checks"])
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var passed = 0;
            AboutChecks.Run((valid, text) => { if (!valid) throw new Exception("FAIL: " + text); passed++; Console.WriteLine("PASS: " + text); });
            Console.WriteLine(passed + " PASS; About native dialog, simulated DPI and substituted shell/error checks."); return 0;
        }
        if (args is ["--creation-check", var binaries, var testParent])
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            return CreationChecks.Run(binaries, testParent);
        }
        if (args.Length != 1 && args is not ["--main-layout-check", _]) { Console.Error.WriteLine("Usage: <owned output directory> | --main-layout-check <owned output directory>"); return 2; }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args is ["--main-layout-check", var layoutOutput])
        {
            Directory.CreateDirectory(layoutOutput);
            MainWindowLayoutChecks.Run(layoutOutput, (valid, text) => {
                Console.WriteLine($"{(valid ? "PASS" : "FAIL")}: {text}");
                if (!valid) throw new Exception(text);
            });
            return 0;
        }
        Directory.CreateDirectory(args[0]);
        var results = new List<string>();
        void Check(bool valid, string text) { if (!valid) throw new Exception("FAIL: " + text); results.Add("PASS: " + text); Console.WriteLine(results[^1]); }
        try
        {
            using var icons = new UiIcons();
            int[] dpis = [96, 120, 144, 192];
            using var sheet = new Bitmap(1550, 860);
            using var graphics = Graphics.FromImage(sheet);
            graphics.Clear(Color.FromArgb(244, 246, 249));
            using var labelFont = new Font("Segoe UI", 10);
            var row = 0;
            foreach (var glyph in Enum.GetValues<UiGlyph>())
            {
                graphics.DrawString(glyph.ToString(), labelFont, Brushes.Black, 4, 45 + row * 78);
                for (var col = 0; col < dpis.Length; col++)
                {
                    var dpi = dpis[col]; var size = (int)(16 * dpi / 96d);
                    foreach (var light in new[] { false, true })
                    {
                        var icon = icons.Get(glyph, dpi, light);
                        Check(icon.Width == size && icon.Height == size, $"{glyph} {(light ? "light" : "ink")} at {dpi / 96d:P0}: exact {size}px embedded raster");
                        var opaque = 0; var edge = 0;
                        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
                        {
                            var pixel = icon.GetPixel(x, y);
                            if (pixel.A > 128) opaque++;
                            if (x == 0 || y == 0 || x == size - 1 || y == size - 1) edge = Math.Max(edge, pixel.A);
                        }
                        if (opaque == 0 || edge >= 128) { icon.Save(Path.Combine(args[0], "failed-raster.png")); throw new Exception($"Empty or clipped raster: {glyph}/{size}; opaque={opaque}, edge={edge}"); }
                    }
                    var left = 135 + col * 350; var top = 38 + row * 78;
                    if (row == 0) graphics.DrawString($"{dpi / 96d:P0} / {size}px · raster simulation", labelFont, Brushes.Black, left, 6);
                    // Offscreen native buttons, explicitly sized in pixels. This is not an OS DPI switch.
                    for (var state = 0; state < 3; state++)
                    {
                        var primary = state > 0;
                        using var button = new Button {
                            Text = "Run", Image = icons.Get(glyph, dpi, primary && state != 2), Enabled = state != 2,
                            TextImageRelation = TextImageRelation.ImageBeforeText, ImageAlign = ContentAlignment.MiddleLeft,
                            BackColor = primary ? Color.FromArgb(35, 92, 180) : Color.White,
                            ForeColor = primary ? Color.White : Color.FromArgb(30, 41, 59), FlatStyle = FlatStyle.Flat,
                            Size = new(106, 62), Padding = new(3), Font = labelFont
                        };
                        button.CreateControl();
                        using var tile = new Bitmap(button.Width, button.Height);
                        button.DrawToBitmap(tile, new Rectangle(Point.Empty, tile.Size));
                        graphics.DrawImageUnscaled(tile, left + state * 112, top);
                    }
                }
                row++;
            }
            sheet.Save(Path.Combine(args[0], "native-buttons-four-raster-sizes.png"));
            using (var button = new Button())
            {
                icons.Bind(button, UiGlyph.Play, light: true);
                var bright = button.Image;
                button.Enabled = false;
                Check(!ReferenceEquals(bright, button.Image), "Disabled primary button changes to the dark source before native gray rendering");
                button.Enabled = true;
                Check(ReferenceEquals(bright, button.Image), "Re-enabled primary button restores its cached light image");
            }
            using (var menu = new ContextMenuStrip())
            {
                var item = new ToolStripMenuItem("Settings"); menu.Items.Add(item); icons.Bind(item, UiGlyph.Settings);
                foreach (var dpi in dpis)
                {
                    icons.UpdateMenu(menu, dpi);
                    Check(item.Image!.Width == UiIcons.Pixels(dpi) && item.ImageScaling == ToolStripItemImageScaling.None,
                        $"Menu raster uses exact pixels without a second stretch at {dpi / 96d:P0}");
                }
            }
            using var licenseStream = typeof(MainForm).Assembly.GetManifestResourceStream("PgHatch.Ui.LICENSE")!;
            using var reader = new StreamReader(licenseStream); var license = reader.ReadToEnd();
            Check(license.Contains("ISC License") && license.Contains("The MIT License") && license.Contains("Cole Bemis"), "Complete Lucide/Feather license embedded");
            using (var brand = new ApplicationIcons(null, null))
            {
                Check(brand.Window(96).Handle != SystemIcons.Application.Handle, "Neutral fallback is owned; SystemIcons is never disposed");
                Check(ReferenceEquals(brand.Tray(144), brand.Tray(144)), "Tray handle reused for repeated selection at same DPI");
            }
            using var buffer = new MemoryStream(); SystemIcons.Application.Save(buffer);
            using (var fixture = new ApplicationIcons(buffer.ToArray(), buffer.ToArray()))
                foreach (var dpi in dpis)
                    Check(fixture.Window(dpi).Handle != IntPtr.Zero && fixture.Tray(dpi).Handle != IntPtr.Zero,
                        $"In-memory ICO remains valid after source stream closes at {dpi / 96d:P0}");
            using (var system = SystemIcons.Application.ToBitmap()) Check(system.Width > 0, "Disposing owned icons leaves the system icon valid");
            var disposed = new UiIcons(); disposed.Get(UiGlyph.Plus, 96); disposed.Dispose(); disposed.Dispose();
            try { disposed.Get(UiGlyph.Plus, 96); throw new Exception("Disposed cache accepted access"); }
            catch (ObjectDisposedException) { Check(true, "Image cache releases ownership and rejects access after disposal"); }
            BrandChecks.Run(args[0], Check);
            LocalizationChecks.Run(args[0], Check);
            AboutChecks.Run(Check);
            EmergencyBindingChecks.Run(Check);
            WindowChecks.Run(args[0], Check);
            MainWindowLayoutChecks.Run(args[0], Check);
            SessionShutdownChecks.Run(args[0], Check);
            Console.WriteLine($"{results.Count} PASS. Offscreen DPI raster and native PE resource checks; real desktop scaling remains a separate GUI check.");
            return 0;
        }
        finally { File.WriteAllLines(Path.Combine(args[0], "icon-check-results.txt"), results); }
    }
}
