using TavernDesk.App.Presentation;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class CampaignWorldbookBindingItem : ViewModelBase
{
    private bool _isBound;

    public CampaignWorldbookBindingItem(Worldbook worldbook, bool isBound)
    {
        Worldbook = worldbook;
        _isBound = isBound;
    }

    public Worldbook Worldbook { get; }
    public string Name => Worldbook.Name;
    public string Description => string.IsNullOrWhiteSpace(Worldbook.Description)
        ? $"{Worldbook.EntryCount} 个条目 · {Worldbook.SourceKindText}"
        : Worldbook.Description;

    public bool IsBound
    {
        get => _isBound;
        set => SetProperty(ref _isBound, value);
    }
}

public sealed class WorldbookCampaignBindingItem : ViewModelBase
{
    private bool _isBound;

    public WorldbookCampaignBindingItem(CampaignScenario scenario, bool isBound)
    {
        Scenario = scenario;
        _isBound = isBound;
    }

    public CampaignScenario Scenario { get; }
    public string Title => Scenario.Title;
    public string Summary => TruncateSummary(Scenario.Summary);

    private static string TruncateSummary(string summary)
    {
        var trimmed = summary.Trim();
        return trimmed.Length <= 20
            ? trimmed
            : $"{trimmed[..20]}…";
    }

    public bool IsBound
    {
        get => _isBound;
        set => SetProperty(ref _isBound, value);
    }
}
