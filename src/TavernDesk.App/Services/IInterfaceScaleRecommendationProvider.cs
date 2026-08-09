namespace TavernDesk.App.Services;

public sealed record InterfaceScaleRecommendation(
    int Percent,
    string Reason);

public interface IInterfaceScaleRecommendationProvider
{
    InterfaceScaleRecommendation? GetRecommendation();
}
