using System.Reflection;

namespace PGTray;

internal enum WindowIcon { Brand, Settings, Database, FileText }

/// <summary>Owns cached Icon handles. Embedded resource bytes are shared across windows.</summary>
internal sealed class ApplicationIcons : IDisposable
{
    private static readonly byte[]? ApplicationAsset = Read("PgHatch.Brand.Application.ico"), TrayAsset = Read("PgHatch.Brand.Tray.ico");
    private static readonly Dictionary<WindowIcon, byte[]?> WindowAssets = new() {
        [WindowIcon.Settings] = Read("PgHatch.Window.settings.ico"),
        [WindowIcon.Database] = Read("PgHatch.Window.database.ico"),
        [WindowIcon.FileText] = Read("PgHatch.Window.file-text.ico")
    };
    private readonly byte[]? application, tray;
    private readonly Dictionary<(bool Tray, WindowIcon Kind, int Pixels), Icon> icons = [];
    private bool disposed;

    public ApplicationIcons() : this(ApplicationAsset, TrayAsset) { }
    internal ApplicationIcons(byte[]? application, byte[]? tray) { this.application = application; this.tray = tray; }

    internal bool HasApplicationAsset => application is not null;
    internal bool HasTrayAsset => tray is not null;
    private static byte[]? Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    internal static void BindWindow(Form form, WindowIcon kind)
    {
        var owned = new ApplicationIcons();
        form.ShowIcon = true; form.ShowInTaskbar = false;
        form.Icon = owned.Window(form.DeviceDpi, kind);
        form.HandleCreated += (_, _) => form.Icon = owned.Window(form.DeviceDpi, kind);
        form.DpiChanged += (_, e) => form.Icon = owned.Window(e.DeviceDpiNew, kind);
        // Disposed runs after the form's native handle and child controls have been destroyed.
        form.Disposed += (_, _) => owned.Dispose();
    }
    public Icon Window(int dpi, WindowIcon kind = WindowIcon.Brand) => Get(false, kind, Math.Max(1, (int)Math.Round(32 * dpi / 96d)));
    public Icon Tray(int dpi) => Get(true, WindowIcon.Brand, UiIcons.Pixels(dpi));
    private Icon Get(bool forTray, WindowIcon kind, int pixels)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (icons.TryGetValue((forTray, kind, pixels), out var cached)) return cached;
        var data = application;
        if (forTray) data = tray ?? application;
        else if (kind != WindowIcon.Brand) data = WindowAssets[kind] ?? application;
        Icon icon;
        if (data is null) icon = (Icon)SystemIcons.Application.Clone(); // Retain the existing neutral fallback.
        else
        {
            using var stream = new MemoryStream(data, writable: false);
            using var source = new Icon(stream, new Size(pixels, pixels));
            icon = (Icon)source.Clone(); // The stream and temporary icon never escape their lifetime.
        }
        icons.Add((forTray, kind, pixels), icon); return icon;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var icon in icons.Values) icon.Dispose(); icons.Clear();
    }
}
