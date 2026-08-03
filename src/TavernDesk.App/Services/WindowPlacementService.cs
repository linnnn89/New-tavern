using System.Globalization;
using System.Windows;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.App.Services;

public sealed class WindowPlacementService
{
    private readonly IAppSettingsRepository _settings;

    public WindowPlacementService(IAppSettingsRepository settings)
    {
        _settings = settings;
    }

    public async Task RestoreAsync(
        Window window,
        string key,
        double fallbackWidth,
        double fallbackHeight)
    {
        window.Width = await ReadDimensionAsync(
            $"{key}.width",
            fallbackWidth,
            window.MinWidth,
            SystemParameters.WorkArea.Width);
        window.Height = await ReadDimensionAsync(
            $"{key}.height",
            fallbackHeight,
            window.MinHeight,
            SystemParameters.WorkArea.Height);
    }

    public async Task SaveAsync(Window window, string key)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        var width = ClampDimension(
            bounds.Width,
            window.MinWidth,
            SystemParameters.WorkArea.Width);
        var height = ClampDimension(
            bounds.Height,
            window.MinHeight,
            SystemParameters.WorkArea.Height);

        await _settings.SetAsync(
            $"{key}.width",
            width.ToString(CultureInfo.InvariantCulture));
        await _settings.SetAsync(
            $"{key}.height",
            height.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<double> ReadDimensionAsync(
        string key,
        double fallback,
        double minimum,
        double maximum)
    {
        var raw = await _settings.GetAsync(key);
        var value = double.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
        return ClampDimension(value, minimum, maximum);
    }

    private static double ClampDimension(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            value = minimum;
        }

        return Math.Clamp(value, minimum, Math.Max(minimum, maximum));
    }
}
