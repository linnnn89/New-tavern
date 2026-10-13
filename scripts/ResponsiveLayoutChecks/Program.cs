using TavernDesk.App.Presentation;

var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
Check(!ResponsiveFit.Fits(799, 600, 800, 0, true), "Collapse below reference width");
Check(ResponsiveFit.Fits(800, 600, 800, 0, true), "Keep exact fit");
Check(!ResponsiveFit.Fits(823, 600, 800, 0, false), "Recovery buffer");
Check(ResponsiveFit.Fits(824, 600, 800, 0, false), "Recover at buffer boundary");
Check(!ResponsiveFit.Fits(1000, 599, 800, 600, true), "Height participates");
foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
{
    Check(ResponsiveFit.Fits(invalid, 600, 800, 0, true), "Ignore unavailable width");
    Check(!ResponsiveFit.Fits(1000, invalid, 800, 0, false), "Ignore unavailable height");
}
var fits = true;
foreach (var width in new[] { 799d, 801, 800, 823, 810, 799, 820 })
{
    fits = ResponsiveFit.Fits(width, 600, 800, 0, fits);
    Check(!fits, "No breakpoint jitter");
}
foreach (var dpi in new[] { 1d, 1.25, 1.5, 2 })
foreach (var scale in new[] { .8, 1, 1.25, 1.5 })
foreach (var font in new[] { 10d, 14, 20, 30 })
{
    // The caller receives this constraint from WPF; the fit policy never sees physical pixels.
    var pixels = 1200 * dpi;
    var layoutWidth = pixels / dpi / scale;
    var expected = 1200 / scale >= 800 * Math.Max(1, font / 14);
    Check(ResponsiveFit.Fits(layoutWidth, 1000, 800 * ResponsiveFit.FontFactor(font), 0, true) == expected,
        "DPI/application scale/font coordinate contract");
}
Console.WriteLine($"Responsive sizing: {checks} checks passed.");
