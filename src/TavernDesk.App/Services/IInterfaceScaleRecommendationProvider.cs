namespace TavernDesk.App.Services;

public sealed record DisplayMetrics(
    int Dpi,
    double WorkAreaWidth,
    double WorkAreaHeight);

public sealed record InterfaceScaleRecommendation(
    int Percent,
    string Reason);

public interface IInterfaceScaleRecommendationProvider
{
    InterfaceScaleRecommendation? GetRecommendation();
}
