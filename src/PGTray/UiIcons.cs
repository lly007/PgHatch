using System.Drawing.Drawing2D;
using System.Reflection;
using PGTray.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PGTray.IconChecks")]

namespace PGTray;

internal enum UiGlyph { Plus, Database, Refresh, Ellipsis, Play, Square, Rotate, FileText, Settings, FolderOpen }

/// <summary>Owns shared images for one window. Controls borrow them; dispose after menus/controls close.</summary>
internal sealed class UiIcons : IDisposable
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 48, 64];
    private readonly Dictionary<(UiGlyph Glyph, bool Light, int Pixels), Bitmap> images = [];
    private readonly Dictionary<Button, (UiGlyph Glyph, bool Light)> buttons = [];
    private readonly Dictionary<ToolStripItem, UiGlyph> items = [];
    private bool disposed;

    internal static int Pixels(int dpi) => Math.Max(1, (int)Math.Round(16 * dpi / 96d));
    internal static string Name(UiGlyph glyph) => glyph switch {
        UiGlyph.Refresh => "refresh-cw", UiGlyph.Rotate => "rotate-cw", UiGlyph.FileText => "file-text",
        UiGlyph.FolderOpen => "folder-open", _ => glyph.ToString().ToLowerInvariant()
    };
    internal Bitmap Get(UiGlyph glyph, int dpi, bool light = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var pixels = Pixels(dpi); var key = (glyph, light, pixels);
        if (images.TryGetValue(key, out var cached)) return cached;
        var sourceSize = Sizes.FirstOrDefault(n => n >= pixels, Sizes[^1]);
        var resource = $"PgHatch.Ui.{Name(glyph)}-{(light ? "light" : "ink")}-{sourceSize}.png";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(L10n.Get("EmbeddedIconMissing") + resource);
        using var source = new Bitmap(stream);
        Bitmap image;
        if (sourceSize == pixels) image = (Bitmap)source.Clone();
        else
        {
            image = new Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(image);
            graphics.PageUnit = GraphicsUnit.Pixel;
            // Explicit source/destination pixels prevent GDI+ interpreting SVG-exported 72dpi metadata.
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, pixels, pixels), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        image.SetResolution(dpi, dpi);
        images.Add(key, image); return image;
    }
    public void Bind(Button button, UiGlyph glyph, bool light = false)
    {
        buttons.Add(button, (glyph, light));
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.ImageAlign = ContentAlignment.MiddleLeft;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.Image = Get(glyph, button.DeviceDpi, light && button.Enabled);
        button.HandleCreated += (_, _) => UpdateButton(button);
        button.DpiChangedAfterParent += (_, _) => UpdateButton(button);
        button.EnabledChanged += (_, _) => UpdateButton(button);
        // Button and ToolStrip render their disabled image with the same native policy as their text.
    }
    public void Bind(ToolStripItem item, UiGlyph glyph)
    {
        items.Add(item, glyph); item.ImageScaling = ToolStripItemImageScaling.None;
        item.Image = Get(glyph, 96);
    }
    private void UpdateButton(Button button)
    {
        if (disposed || button.IsDisposed) return;
        var icon = buttons[button]; button.Image = Get(icon.Glyph, button.DeviceDpi, icon.Light && button.Enabled);
    }
    public void UpdateMenu(ContextMenuStrip menu, int dpi)
    {
        menu.ImageScalingSize = new Size(Pixels(dpi), Pixels(dpi));
        foreach (ToolStripItem item in menu.Items)
            if (items.TryGetValue(item, out var glyph)) item.Image = Get(glyph, dpi);
    }
    public void UpdateDpi(int dpi)
    {
        foreach (var (button, icon) in buttons)
            if (!button.IsDisposed) button.Image = Get(icon.Glyph, dpi, icon.Light && button.Enabled);
        foreach (var (item, glyph) in items)
            if (!item.IsDisposed) item.Image = Get(glyph, dpi);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var button in buttons.Keys) if (!button.IsDisposed) button.Image = null;
        foreach (var item in items.Keys) if (!item.IsDisposed) item.Image = null;
        foreach (var image in images.Values) image.Dispose();
        images.Clear(); buttons.Clear(); items.Clear();
    }
}
