using System.Runtime.InteropServices;
using PGTray;

namespace PGTray.IconChecks;

internal static class BrandChecks
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static void Run(string output, Action<bool, string> check)
    {
        using var brand = new ApplicationIcons();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PGTray.sln"))) directory = directory.Parent;
        var repo = directory?.FullName ?? throw new DirectoryNotFoundException("Run brand checks from the repository build output");
        var assetPath = Path.Combine(repo, "src/PGTray/Assets/Brand");
        var applicationPixels = new Dictionary<int, int[]>();
        check(brand.HasApplicationAsset && brand.HasTrayAsset, "Production application and tray assets are embedded");
        foreach (var kind in new[] { "Application", "Tray" })
        {
            using var resource = typeof(MainForm).Assembly.GetManifestResourceStream($"PgHatch.Brand.{kind}.ico")!;
            using var buffer = new MemoryStream(); resource.CopyTo(buffer); var bytes = buffer.ToArray();
            var fileName = kind == "Application" ? "PgHatch.ico" : "PgHatch.Tray.ico";
            check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(assetPath, fileName))), $"{kind}: embedded bytes exactly match production ICO");
            ushort U16(int p) => BitConverter.ToUInt16(bytes, p);
            uint U32(int p) => BitConverter.ToUInt32(bytes, p);
            check(U16(0) == 0 && U16(2) == 1 && U16(4) == Sizes.Length, $"{kind}: ICO header has exactly nine frames");
            var expectedOffset = 6 + Sizes.Length * 16;
            for (var frame = 0; frame < Sizes.Length; frame++)
            {
                var size = Sizes[frame]; var p = 6 + frame * 16;
                var width = bytes[p] == 0 ? 256 : bytes[p];
                var height = bytes[p + 1] == 0 ? 256 : bytes[p + 1];
                var length = checked((int)U32(p + 8)); var offset = checked((int)U32(p + 12));
                check(width == size && height == size && U16(p + 4) == 1 && U16(p + 6) == 32 && offset == expectedOffset && offset + length <= bytes.Length,
                    $"{kind}/{size}: exact dimensions, 32-bit RGBA, valid non-overlapping frame range");
                expectedOffset += length;
                if (size == 256)
                    check(bytes.AsSpan(offset, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes[offset + 24] == 8 && bytes[offset + 25] == 6,
                        $"{kind}/{size}: PNG frame is true 8-bit RGBA, not indexed color");
                else
                    check(U32(offset) == 40 && U32(offset + 4) == size && U32(offset + 8) == size * 2 && U16(offset + 14) == 32,
                        $"{kind}/{size}: native 32-bit DIB with alpha and AND mask");

                // Decode each frame independently. This catches a missing ICO entry
                // being silently replaced by System.Drawing's nearest-size fallback.
                var single = new byte[22 + length]; single[2] = 1; single[4] = 1;
                bytes.AsSpan(p, 16).CopyTo(single.AsSpan(6)); BitConverter.GetBytes(22u).CopyTo(single, 18);
                bytes.AsSpan(offset, length).CopyTo(single.AsSpan(22));
                using var stream = new MemoryStream(single);
                using var icon = new Icon(stream, size, size); using var bitmap = icon.ToBitmap();
                var transparent = 0; var partial = 0; var white = 0; var blue = 0; var clipped = false; var fringe = false;
                for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (c.A == 0) transparent++;
                    // Near-opaque interior shell pixels can round to 251–254
                    // during Lanczos resampling. Inspect the actual alpha edge.
                    if (c.A is > 0 and < 255) { partial++; if (c.A < 240 && c.R > 220 && c.G > 220 && c.B > 220) fringe = true; }
                    if (c.A == 255 && c.R > 230 && c.G > 230 && c.B > 230) white++;
                    if (c.A > 220 && c.B > c.R + 40) blue++;
                    // Approved small frames use a subpixel transparent margin.
                    // Outer antialias coverage is allowed; opaque edge clipping
                    // remains forbidden. Large frames retain their clear border.
                    if ((x == 0 || y == 0 || x == size - 1 || y == size - 1) && c.A >= (size <= 32 ? 128 : 1)) clipped = true;
                }
                clipped |= bitmap.GetPixel(0, 0).A != 0 || bitmap.GetPixel(size - 1, 0).A != 0 || bitmap.GetPixel(0, size - 1).A != 0 || bitmap.GetPixel(size - 1, size - 1).A != 0;
                bitmap.Save(Path.Combine(output, $"decoded-{kind}-{size}.png"));
                if (transparent == 0 || partial == 0 || white == 0 || blue == 0 || clipped || fringe)
                    Console.WriteLine($"Alpha diagnostic {kind}/{size}: transparent={transparent}, partial={partial}, white={white}, blue={blue}, clipped={clipped}, fringe={fringe}");
                check(bitmap.Width == size && transparent > 0 && partial > 0 && white > 0 && blue > 0 && !clipped && !fringe,
                    $"{kind}/{size}: safe alpha boundary and clear corners, opaque white shell and blue database, no white matte in alpha fringe");
                var pixels = Pixels(bitmap);
                using var png = new Bitmap(Path.Combine(assetPath, "Frames", $"{(kind == "Application" ? "app" : "tray")}-{size}.png"));
                check(pixels.AsSpan().SequenceEqual(Pixels(png)), $"{kind}/{size}: ICO pixels exactly match the production PNG");
                if (size <= 32)
                {
                    using var approved = new Bitmap(Path.Combine(repo, "tests/PGTray.IconChecks/Fixtures/Brand", $"Adjusted-{size}.png"));
                    check(pixels.AsSpan().SequenceEqual(Pixels(approved)), $"{kind}/{size}: exact approved proportional enlargement, no redesign");
                }
                if (kind == "Application") applicationPixels.Add(size, pixels);
            }
            check(expectedOffset == bytes.Length, $"{kind}: all file bytes belong to declared ICO frames");
        }
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            check(brand.Window(dpi).Width == (int)Math.Round(32 * dpi / 96d) && brand.Tray(dpi).Width == (int)Math.Round(16 * dpi / 96d),
                $"Production frame selection is exact at {dpi / 96d:P0} (window and tray)");
            check(ReferenceEquals(brand.Window(dpi), brand.Window(dpi)), $"Production window HICON is cached at {dpi / 96d:P0}");
        }
        // Extract from the built apphost PE, not the managed embedded resources.
        var exe = Path.Combine(Path.GetDirectoryName(typeof(MainForm).Assembly.Location)!, "PgHatch.exe");
        foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
        {
            var handles = new IntPtr[1];
            var count = PrivateExtractIcons(exe, 0, size, size, handles, null, 1, 0);
            try
            {
                check(count == 1 && handles[0] != IntPtr.Zero, $"Built EXE contains a native shell icon extractable at {size}px");
                using var extracted = (Icon)Icon.FromHandle(handles[0]).Clone();
                check(extracted.Width == size && extracted.Height == size, $"Native PE icon extraction returns exact {size}px");
                using var bitmap = extracted.ToBitmap();
                check(Pixels(bitmap).AsSpan().SequenceEqual(applicationPixels[size]), $"Built EXE/{size}: native icon pixels exactly match the production application ICO");
            }
            finally { if (handles[0] != IntPtr.Zero) DestroyIcon(handles[0]); }
        }
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        using var form = new MainForm(signal); // Not shown: no config load or PostgreSQL work.
        using var windowBitmap = form.Icon!.ToBitmap();
        using var expectedWindow = brand.Window(form.DeviceDpi).ToBitmap();
        check(Pixels(windowBitmap).AsSpan().SequenceEqual(Pixels(expectedWindow)) && form.ShowInTaskbar,
            "Main window/taskbar uses the production application icon");
        var tray = (NotifyIcon)typeof(MainForm).GetField("tray", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
        using var trayBitmap = tray.Icon!.ToBitmap();
        using var expectedTray = brand.Tray(form.DeviceDpi).ToBitmap();
        check(Pixels(trayBitmap).AsSpan().SequenceEqual(Pixels(expectedTray)), "NotifyIcon uses the production tray asset");
    }

    private static int[] Pixels(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++) pixels[y * bitmap.Width + x] = bitmap.GetPixel(x, y).ToArgb();
        return pixels;
    }

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height, [Out] IntPtr[] handles, [Out] uint[]? ids, uint count, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
