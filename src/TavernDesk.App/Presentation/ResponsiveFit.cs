namespace TavernDesk.App.Presentation;

/// <summary>Page-local fit decisions in layout DIPs, after WPF has applied LayoutTransform.</summary>
public static class ResponsiveFit
{
    public static bool Fits(double width, double height, double referenceWidth, double referenceHeight,
        bool wasFitting, double recoveryBuffer = 24)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return wasFitting;

        var buffer = wasFitting ? 0 : recoveryBuffer;
        return width >= referenceWidth + buffer && height >= referenceHeight + buffer;
    }

    public static double FontFactor(double fontSize) => Math.Max(1, fontSize / 14);
}
