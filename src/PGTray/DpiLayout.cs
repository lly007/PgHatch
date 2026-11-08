namespace PGTray;

// Only for geometry outside WinForms autoscaling: work-area bounds and measured row metrics.
// Fixed control sizes, margins and padding stay in the 96-DPI design tree.
internal static class DpiLayout
{
    internal static float Logical(int pixels, int dpi) => pixels * 96f / dpi;
    internal static int Pixels(float logical, int dpi) => (int)Math.Round(logical * dpi / 96f);
    internal static Size Pixels(SizeF logical, int dpi) => new(Pixels(logical.Width, dpi), Pixels(logical.Height, dpi));
}
