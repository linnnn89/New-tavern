using TavernDesk.App.ViewModels;
using TavernDesk.Core.Models;

namespace TavernDesk.Tests;

public sealed class CampaignUiStateTests
{
    [Fact]
    public void ObserverCampaignHidesUserInputAndExplainsManualAiGm()
    {
        var campaign = CreateCampaign(
            CampaignFlowPreset.CollaborativeTable,
            CampaignGmKind.Ai);
        var first = CreateAi(campaign.Id, "a", 0, "雪乃");
        var second = CreateAi(campaign.Id, "b", 1, "加藤惠");
        var aggregate = new CampaignAggregate(
            campaign,
            [first, second],
            []);

        var state = CampaignGameUiState.Create(aggregate);

        Assert.False(state.HasUserSeat);
        Assert.False(state.ShowUserActionSection);
        Assert.True(state.CanScheduleUserJoin);
        Assert.Contains("AI GM 主持", state.GmModeText, StringComparison.Ordinal);
        Assert.Contains("观看 AI 演出", state.ParticipationModeText, StringComparison.Ordinal);
        Assert.DoesNotContain("提交你的行动", state.CurrentStepDescription);
        Assert.True(CampaignSeatActionState.Create(aggregate, first).CanAct);
        Assert.True(CampaignSeatActionState.Create(aggregate, second).CanAct);
    }

    [Fact]
    public void StrictInitiativeShowsOnlyTheCurrentSeatAction()
    {
        var campaign = CreateCampaign(
            CampaignFlowPreset.StrictInitiative,
            CampaignGmKind.User);
        campaign.UserAlsoPlayer = true;
        campaign.CurrentTurnIndex = 1;
        var user = new CampaignParticipant
        {
            Id = "user",
            CampaignId = campaign.Id,
            Kind = CampaignParticipantKind.User,
            SortIndex = 0,
            DisplayName = "旅人"
        };
        var ai = CreateAi(campaign.Id, "ai", 1, "雪乃");
        var aggregate = new CampaignAggregate(campaign, [user, ai], []);

        var aiTurn = CampaignGameUiState.Create(aggregate);

        Assert.False(aiTurn.ShowUserActionSection);
        Assert.True(CampaignSeatActionState.Create(aggregate, ai).CanAct);

        campaign.CurrentTurnIndex = 0;
        var userTurn = CampaignGameUiState.Create(aggregate);
        var aiState = CampaignSeatActionState.Create(aggregate, ai);

        Assert.True(userTurn.ShowUserActionSection);
        Assert.True(userTurn.UserSeatCanAct);
        Assert.False(aiState.CanAct);
        Assert.Contains("旅人", aiState.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void BlindSubmissionUsesOneBatchControlInsteadOfSeatButtons()
    {
        var campaign = CreateCampaign(
            CampaignFlowPreset.BlindSubmission,
            CampaignGmKind.Ai);
        var first = CreateAi(campaign.Id, "a", 0, "雪乃");
        var second = CreateAi(campaign.Id, "b", 1, "加藤惠");
        var aggregate = new CampaignAggregate(
            campaign,
            [first, second],
            []);

        var state = CampaignGameUiState.Create(aggregate);
        var seatState = CampaignSeatActionState.Create(aggregate, first);

        Assert.True(state.ShowBlindAiAction);
        Assert.True(state.CanGenerateBlindAiActions);
        Assert.False(seatState.ShowButton);
        Assert.False(seatState.CanAct);
        Assert.Contains("一次并发", seatState.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedAiAttemptBecomesMandatoryRetryStep()
    {
        var campaign = CreateCampaign(
            CampaignFlowPreset.CollaborativeTable,
            CampaignGmKind.Ai);
        var ai = CreateAi(campaign.Id, "ai", 0, "雪乃");
        var failed = new CampaignEvent
        {
            CampaignId = campaign.Id,
            SequenceNo = 1,
            RoundNo = 1,
            Kind = CampaignEventKind.PlayerIntent,
            ActorId = ai.Id,
            GenerationStatus = CampaignGenerationStatus.Failed,
            EndReason = CampaignEndReason.ProviderError
        };
        var aggregate = new CampaignAggregate(campaign, [ai], [failed]);

        var state = CampaignGameUiState.Create(aggregate);
        var seatState = CampaignSeatActionState.Create(aggregate, ai);

        Assert.Equal("先处理生成失败", state.CurrentStepTitle);
        Assert.Contains(
            "必须先在跑团记录中重试",
            state.CurrentStepDescription,
            StringComparison.Ordinal);
        Assert.False(seatState.CanAct);
        Assert.Contains("重试原行动", seatState.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedAiGmResolutionStaysOnExplicitRetryStep()
    {
        var campaign = CreateCampaign(
            CampaignFlowPreset.CollaborativeTable,
            CampaignGmKind.Ai);
        campaign.Phase = CampaignPhase.ReadyForResolution;
        var ai = CreateAi(campaign.Id, "ai", 0, "雪乃");
        var action = new CampaignEvent
        {
            CampaignId = campaign.Id,
            SequenceNo = 1,
            RoundNo = 1,
            Kind = CampaignEventKind.PlayerIntent,
            ActorId = ai.Id,
            GenerationStatus = CampaignGenerationStatus.Completed,
            EndReason = CampaignEndReason.Normal,
            IsLocked = true
        };
        var failedResolution = new CampaignEvent
        {
            CampaignId = campaign.Id,
            SequenceNo = 2,
            RoundNo = 1,
            Kind = CampaignEventKind.GmResolution,
            ActorId = "gm:ai",
            GenerationStatus = CampaignGenerationStatus.Failed,
            EndReason = CampaignEndReason.ProviderError
        };
        var aggregate = new CampaignAggregate(
            campaign,
            [ai],
            [action, failedResolution]);

        var state = CampaignGameUiState.Create(aggregate);

        Assert.True(state.ShowResolveSection);
        Assert.Equal("重试 AI GM 裁定", state.CurrentStepTitle);
        Assert.Contains("不能跳过", state.CurrentStepDescription, StringComparison.Ordinal);
        Assert.Contains("失败记录不会被删除", state.ResolveHelpText, StringComparison.Ordinal);
    }

    private static Campaign CreateCampaign(
        CampaignFlowPreset flow,
        CampaignGmKind gmKind) =>
        new()
        {
            Title = "测试跑团",
            WorldSetting = "测试世界",
            Rules = "测试规则",
            OpeningPrompt = "测试开场",
            Status = CampaignStatus.Active,
            Phase = CampaignPhase.AwaitingActions,
            CurrentRound = 1,
            FlowPreset = flow,
            GmKind = gmKind,
            UserAlsoPlayer = false
        };

    private static CampaignParticipant CreateAi(
        string campaignId,
        string id,
        int sortIndex,
        string name) =>
        new()
        {
            Id = id,
            CampaignId = campaignId,
            Kind = CampaignParticipantKind.Ai,
            SortIndex = sortIndex,
            DisplayName = name,
            ProviderId = "provider",
            ModelId = "model"
        };
}
