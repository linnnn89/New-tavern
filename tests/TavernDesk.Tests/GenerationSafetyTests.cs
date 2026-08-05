using TavernDesk.Infrastructure.Providers;

namespace TavernDesk.Tests;

public sealed class GenerationSafetyTests
{
    [Fact]
    public void RepetitionGuardStopsAnExactLongRunningLoopAcrossChunks()
    {
        var guard = new ProviderOutputHealthGuard();
        const string repeated =
            "她再次确认门窗、补给和地图，然后把相同的决定写回行动记录。";

        guard.Observe(repeated + repeated);
        guard.Observe(repeated);
        guard.Observe(repeated);

        Assert.Throws<ProviderOutputLoopException>(() => guard.Observe(repeated));
    }

    [Fact]
    public void RepetitionGuardAllowsRelatedButNonIdenticalNarrativeParagraphs()
    {
        var guard = new ProviderOutputHealthGuard();
        var paragraphs = new[]
        {
            "她检查门窗和补给，然后把新的路线画在地图北侧。",
            "她再次检查门窗，却决定把撤离路线改到河道方向。",
            "第三次检查时，她发现窗沿留下了此前不存在的湿泥。",
            "众人复核补给，确认药品足够，但食物只能维持两天。",
            "最后一次检查结束后，队伍按新的先后顺序离开据点。"
        };

        foreach (var paragraph in paragraphs)
        {
            guard.Observe(paragraph);
        }
    }
}
