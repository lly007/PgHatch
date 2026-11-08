using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PGTray.Core;

namespace PGTray.IconChecks;

internal static class WindowIconChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private static void Pump()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 100) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static bool SamePixels(Icon actual, Icon expected)
    {
        using var a = actual.ToBitmap(); using var b = expected.ToBitmap();
        return a.Size == b.Size && BrandChecks.Pixels(a).AsSpan().SequenceEqual(BrandChecks.Pixels(b));
    }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Window icon fixtures prohibit network access"));
        }
    }
    private static void CheckFrames(Action<bool, string> check)
    {
        foreach (var name in new[] { "settings", "database", "file-text" })
        {
            using var resource = typeof(MainForm).Assembly.GetManifestResourceStream($"PgHatch.Window.{name}.ico")!;
            using var buffer = new MemoryStream(); resource.CopyTo(buffer); var bytes = buffer.ToArray();
            check(BitConverter.ToUInt16(bytes, 0) == 0 && BitConverter.ToUInt16(bytes, 2) == 1 && BitConverter.ToUInt16(bytes, 4) == Sizes.Length,
                $"{name}: embedded Windows ICO contains all nine frames");
            for (var index = 0; index < Sizes.Length; index++)
            {
                var size = Sizes[index]; var entry = 6 + 16 * index;
                var length = checked((int)BitConverter.ToUInt32(bytes, entry + 8));
                var offset = checked((int)BitConverter.ToUInt32(bytes, entry + 12));
                check((bytes[entry] == 0 ? 256 : bytes[entry]) == size && (bytes[entry + 1] == 0 ? 256 : bytes[entry + 1]) == size &&
                    BitConverter.ToUInt16(bytes, entry + 6) == 32 && offset + length <= bytes.Length, $"{name}/{size}: exact native 32-bit frame entry");
                // Decode this frame alone so nearest-size substitution cannot conceal a missing frame.
                var single = new byte[22 + length]; single[2] = 1; single[4] = 1;
                bytes.AsSpan(entry, 16).CopyTo(single.AsSpan(6)); BitConverter.GetBytes(22u).CopyTo(single, 18);
                bytes.AsSpan(offset, length).CopyTo(single.AsSpan(22));
                using var stream = new MemoryStream(single); using var icon = new Icon(stream, size, size); using var bitmap = icon.ToBitmap();
                var ink = 0; var clipped = false;
                for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    if (color.A > 128) ink++;
                    if ((x == 0 || y == 0 || x == size - 1 || y == size - 1) && color.A != 0) clipped = true;
                }
                check(bitmap.Size == new Size(size, size) && ink > size && !clipped, $"{name}/{size}: decoded ICO is visible, transparent and uncropped");
            }
        }
    }
    internal static void Run(string output, Action<bool, string> check)
    {
        CheckFrames(check);
        var root = Path.Combine(Path.GetFullPath(output), "window-icons"); Directory.CreateDirectory(root);
        var store = new ConfigStore(root); var tools = new PostgresTools(new ProcessRunner()); var manager = new InstanceManager(store, tools);
        var binary = new BinaryInstallation(Guid.NewGuid(), @"X:\icon-fixture\bin", "18.6", "18");
        var instance = new InstanceConfig(Guid.NewGuid(), "Icon fixture", binary.Id, Path.Combine(root, "missing-data"), 55499, Path.Combine(root, "fixture.log"));
        manager.Config.Binaries.Add(binary);
        var bundled = Path.Combine(AppContext.BaseDirectory, "postgresql-catalog.xml");
        var catalog = PostgresCatalog.Parse(File.ReadAllBytes(bundled));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog.UpdateUrl.AbsoluteUri))).ToLowerInvariant();
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        File.WriteAllBytes(Path.Combine(root, "cache", "postgresql-catalog-" + key + ".xml.checked"), []);
        var requests = new List<OfflineHandler>();
        Form CatalogWindow(bool download)
        {
            Form form = download ? new DownloadForm(manager, store) : new CatalogForm(store);
            var field = form.GetType().GetField(download ? "catalog" : "service", Private)!;
            ((PostgresCatalogService)field.GetValue(form)!).Dispose();
            var offline = new OfflineHandler(); requests.Add(offline);
            field.SetValue(form, new PostgresCatalogService(bundled, store, offline));
            return form;
        }
        (Func<Form> Create, WindowIcon Kind)[] fixtures = [
            (() => new AboutForm(), WindowIcon.Brand),
            (() => new BinaryForm(manager), WindowIcon.Settings),
            (() => new InstanceSettingsForm(instance, manager), WindowIcon.Settings),
            (() => new RenameForm("Icon fixture"), WindowIcon.Settings),
            (() => new EmergencyToolForm([binary]), WindowIcon.Settings),
            (() => new InstanceForm([binary], tools, true), WindowIcon.Database),
            (() => new InstanceForm([binary], tools, false), WindowIcon.Database),
            (() => new DiscoveryForm(manager, tools), WindowIcon.Database),
            (() => new LogForm(instance, "Icon fixture"), WindowIcon.FileText),
            (() => CatalogWindow(false), WindowIcon.Settings),
            (() => CatalogWindow(true), WindowIcon.Settings)
        ];
        var covered = new HashSet<Type> { typeof(MainForm) };
        using var expected = new ApplicationIcons();
        using var owner = new Form { Text = "Isolated window icon owner", ShowInTaskbar = false };
        owner.Show(); Pump();
        foreach (var cycle in Enumerable.Range(0, 3))
            foreach (var fixture in fixtures)
            {
                using var form = fixture.Create(); covered.Add(form.GetType());
                form.Show(owner); Pump();
                var name = form.GetType().Name;
                var initial = form.Icon!;
                var initialHandle = initial.Handle;
                check(form.ShowIcon && !form.ShowInTaskbar && form.Owner == owner && SamePixels(form.Icon!, expected.Window(form.DeviceDpi, fixture.Kind)),
                    $"{name}/{cycle}: owned window displays its assigned application ICO");
                foreach (var small in new[] { true, false })
                {
                    var handle = SendMessage(form.Handle, 0x007F, new IntPtr(small ? 0 : 1), IntPtr.Zero); // WM_GETICON
                    check(handle != IntPtr.Zero, $"{name}/{cycle}: native {(small ? "small" : "large")} title-bar HICON is installed");
                    using var native = (Icon)Icon.FromHandle(handle).Clone();
                    var logical = small ? 16 : 32;
                    check(native.Size == new Size(DpiLayout.Pixels(logical, form.DeviceDpi), DpiLayout.Pixels(logical, form.DeviceDpi)),
                        $"{name}/{cycle}: native title-bar HICON uses the current desktop DPI frame");
                    using var frame = new Icon(expected.Window(form.DeviceDpi, fixture.Kind), native.Size);
                    check(SamePixels(native, frame), $"{name}/{cycle}: native title-bar pixels match the assigned ICO frame");
                }
                // Exercise the production DPI binding with a synthetic event, preserving native window geometry.
                var onDpi = typeof(Form).GetMethod("OnDpiChanged", Private)!;
                var rectangle = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.Copy(new[] { form.Left, form.Top, form.Right, form.Bottom }, 0, rectangle, 4);
                    var ctor = typeof(DpiChangedEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(int), typeof(Message)], null)!;
                    var previousDpi = form.DeviceDpi;
                    foreach (var dpi in new[] { 192, 120, form.DeviceDpi })
                    {
                        var message = Message.Create(form.Handle, 0x02E0, new IntPtr(dpi | dpi << 16), rectangle);
                        var args = (DpiChangedEventArgs)ctor.Invoke([previousDpi, message]);
                        onDpi.Invoke(form, [args]); Pump();
                        previousDpi = dpi;
                        check(SamePixels(form.Icon!, expected.Window(dpi, fixture.Kind)), $"{name}/{cycle}: DPI event selects the assigned multi-frame icon at {dpi}");
                    }
                }
                finally { Marshal.FreeHGlobal(rectangle); }
                check(ReferenceEquals(form.Icon, initial), $"{name}/{cycle}: returning to the initial DPI reuses a live owned icon");
                if (cycle == 0 && fixture.Kind != WindowIcon.Brand)
                {
                    using var preview = new Bitmap(form.Width, form.Height); form.DrawToBitmap(preview, new(Point.Empty, preview.Size));
                    preview.Save(Path.Combine(output, name + "-titlebar.png"));
                }
                form.Close(); Pump(); form.Dispose();
                check(form.IsDisposed, $"{name}/{cycle}: repeated window close completes icon disposal");
                check(!IsIconAlive(initialHandle), $"{name}/{cycle}: window-owned HICON is destroyed after disposal");
                using var independent = expected.Window(owner.DeviceDpi, fixture.Kind).ToBitmap();
                check(independent.Width > 0, $"{name}/{cycle}: closing one window leaves another icon owner valid");
            }
        var concreteForms = typeof(MainForm).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(Form)) && type != typeof(LayoutForm)).ToHashSet();
        check(covered.SetEquals(concreteForms), "Every concrete application Form is covered by the window icon assignment checks");
        foreach (var fixture in fixtures)
        {
            using var form = fixture.Create();
            using var close = new System.Windows.Forms.Timer { Interval = 20 };
            var ownedModal = false;
            close.Tick += (_, _) => {
                close.Stop();
                ownedModal = form.Modal && GetWindow(form.Handle, 4) == owner.Handle && !form.ShowInTaskbar && !IsWindowEnabled(owner.Handle);
                form.DialogResult = DialogResult.Cancel; form.Close();
            };
            close.Start(); form.ShowDialog(owner); close.Stop(); Pump();
            check(ownedModal && IsWindowEnabled(owner.Handle), $"{form.GetType().Name}: modal ownership and owner reactivation survive title-bar icon binding");
            form.Dispose();
        }
        check(requests.All(handler => handler.Requests == 0), "Window icon open/close fixtures never access the network");
        owner.Close(); manager.ShutdownAsync().GetAwaiter().GetResult();
    }
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public int IsIcon, HotspotX, HotspotY; public IntPtr Mask, Color; }
    private static bool IsIconAlive(IntPtr icon)
    {
        if (!GetIconInfo(icon, out var info)) return false;
        if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
        if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
        return true;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
}
