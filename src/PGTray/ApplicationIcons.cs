using System.Reflection;

namespace PGTray;

/// <summary>Optional embedded brand assets. No file-system lookup and no ownership of SystemIcons.</summary>
internal sealed class ApplicationIcons : IDisposable
{
    private readonly byte[]? application, tray;
    private readonly Dictionary<(bool Tray, int Pixels), Icon> icons = [];
    private bool disposed;

    public ApplicationIcons() : this(Read("PgHatch.Brand.Application.ico"), Read("PgHatch.Brand.Tray.ico")) { }
    internal ApplicationIcons(byte[]? application, byte[]? tray) { this.application = application; this.tray = tray; }

    internal bool HasApplicationAsset => application is not null;
    internal bool HasTrayAsset => tray is not null;
    private static byte[]? Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
    public Icon Window(int dpi) => Get(false, Math.Max(1, (int)Math.Round(32 * dpi / 96d)));
    public Icon Tray(int dpi) => Get(true, UiIcons.Pixels(dpi));
    private Icon Get(bool forTray, int pixels)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (icons.TryGetValue((forTray, pixels), out var cached)) return cached;
        var data = forTray ? tray ?? application : application;
        Icon icon;
        if (data is null) icon = (Icon)SystemIcons.Application.Clone(); // Retain the existing neutral fallback.
        else
        {
            using var stream = new MemoryStream(data, writable: false);
            using var source = new Icon(stream, new Size(pixels, pixels));
            icon = (Icon)source.Clone(); // The stream and temporary icon never escape their lifetime.
        }
        icons.Add((forTray, pixels), icon); return icon;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var icon in icons.Values) icon.Dispose(); icons.Clear();
    }
}
