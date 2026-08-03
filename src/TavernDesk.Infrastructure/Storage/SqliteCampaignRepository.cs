using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteCampaignRepository : ICampaignRepository
{
    private readonly SqliteDatabase _database;

    public SqliteCampaignRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<CampaignSummary>> ListAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var result = new List<CampaignSummary>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.story_id, c.parent_campaign_id, c.title,
                   c.status, c.phase, c.flow_preset, c.current_round,
                   COUNT(p.id), c.updated_at
            FROM campaigns c
            LEFT JOIN campaign_participants p
              ON p.campaign_id = c.id AND p.is_enabled = 1
            WHERE $includeArchived = 1 OR c.status <> $archived
            GROUP BY c.id
            ORDER BY c.updated_at DESC, c.id;
            """;
        command.Parameters.AddWithValue("$includeArchived", includeArchived);
        command.Parameters.AddWithValue("$archived", (int)CampaignStatus.Archived);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CampaignSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                (CampaignStatus)reader.GetInt32(4),
                (CampaignPhase)reader.GetInt32(5),
                (CampaignFlowPreset)reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                DateTimeOffset.Parse(reader.GetString(9))));
        }

        return result;
    }

    public async Task<CampaignAggregate?> GetAsync(
        string campaignId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var campaign = await ReadCampaignAsync(
            connection,
            transaction: null,
            campaignId,
            cancellationToken);
        if (campaign is null)
        {
            return null;
        }

        var participants = await ReadParticipantsAsync(
            connection,
            transaction: null,
            campaignId,
            cancellationToken);
        var events = await ReadEventsAsync(
            connection,
            transaction: null,
            campaignId,
            cancellationToken);
        return new CampaignAggregate(campaign, participants, events);
    }

    public async Task SaveDraftAsync(
        Campaign campaign,
        IReadOnlyList<CampaignParticipant> participants,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(participants);
        if (campaign.Status != CampaignStatus.Draft
            || campaign.Phase != CampaignPhase.Draft)
        {
            throw new InvalidOperationException("只有起始大厅中的草稿可以整体保存。");
        }

        ValidateParticipantsBelongToCampaign(campaign.Id, participants);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var stored = await ReadCampaignAsync(
                connection,
                (SqliteTransaction)transaction,
                campaign.Id,
                cancellationToken);
            if (stored is not null && stored.Status != CampaignStatus.Draft)
            {
                throw new InvalidOperationException(
                    "游戏已经开始，剧本、角色快照和流程设置已冻结。");
            }

            campaign.StoryId = string.IsNullOrWhiteSpace(campaign.StoryId)
                ? Guid.NewGuid().ToString("N")
                : campaign.StoryId.Trim();
            campaign.Status = CampaignStatus.Draft;
            campaign.Phase = CampaignPhase.Draft;
            campaign.CurrentRound = Math.Max(1, campaign.CurrentRound);
            campaign.CurrentTurnIndex = 0;
            campaign.FrozenSequenceNo = 0;
            campaign.StateVersion = 0;
            campaign.StartedAt = null;
            campaign.UpdatedAt = DateTimeOffset.Now;
            await UpsertCampaignAsync(
                connection,
                (SqliteTransaction)transaction,
                campaign,
                cancellationToken);

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText =
                    "DELETE FROM campaign_participants WHERE campaign_id = $campaignId;";
                delete.Parameters.AddWithValue("$campaignId", campaign.Id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var participant in participants.OrderBy(item => item.SortIndex))
            {
                await InsertParticipantAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    participant,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<CampaignAggregate> StartAsync(
        string campaignId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var campaign = await ReadCampaignAsync(
                               connection,
                               (SqliteTransaction)transaction,
                               campaignId,
                               cancellationToken)
                           ?? throw new InvalidOperationException("起始大厅草稿不存在。");
            var participants = await ReadParticipantsAsync(
                connection,
                (SqliteTransaction)transaction,
                campaignId,
                cancellationToken);
            ValidateStart(campaign, participants);

            campaign.Status = CampaignStatus.Active;
            campaign.Phase = CampaignPhase.Opening;
            campaign.CurrentRound = 1;
            campaign.CurrentTurnIndex = 0;
            campaign.FrozenSequenceNo = 1;
            campaign.StateVersion++;
            campaign.StartedAt = DateTimeOffset.Now;
            campaign.UpdatedAt = DateTimeOffset.Now;
            await UpdateCampaignAsync(
                connection,
                (SqliteTransaction)transaction,
                campaign,
                cancellationToken);
            await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                new CampaignEvent
                {
                    CampaignId = campaign.Id,
                    SequenceNo = 1,
                    RoundNo = 1,
                    Kind = CampaignEventKind.System,
                    ActorId = "system",
                    Visibility = CampaignVisibility.GmOnly,
                    Content = "开始游戏：剧本、角色、记忆导入和初始模型配置已冻结。",
                    SnapshotSequenceNo = 0,
                    GenerationStatus = CampaignGenerationStatus.Completed,
                    EndReason = CampaignEndReason.Normal,
                    OperationId = $"start:{campaign.Id}",
                    IsLocked = true
                },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return await GetAsync(campaignId, cancellationToken)
               ?? throw new InvalidOperationException("游戏开始后无法重新读取跑团。");
    }

    public async Task<CampaignAggregate> CloneAsDraftAsync(
        string campaignId,
        CancellationToken cancellationToken = default)
    {
        var source = await GetAsync(campaignId, cancellationToken)
                     ?? throw new InvalidOperationException("要另开一局的跑团不存在。");
        var now = DateTimeOffset.Now;
        var clone = new Campaign
        {
            StoryId = source.Campaign.StoryId,
            ParentCampaignId = source.Campaign.Id,
            Title = $"{source.Campaign.Title} · 新一局",
            WorldSetting = source.Campaign.WorldSetting,
            Rules = source.Campaign.Rules,
            OpeningPrompt = source.Campaign.OpeningPrompt,
            GmKind = source.Campaign.GmKind,
            UserAlsoPlayer = source.Campaign.UserAlsoPlayer,
            FlowPreset = source.Campaign.FlowPreset,
            WorldSummary = string.Empty,
            UserPersonaName = source.Campaign.UserPersonaName,
            UserPersonaDescription = source.Campaign.UserPersonaDescription,
            GmProviderId = source.Campaign.GmProviderId,
            GmModelId = source.Campaign.GmModelId,
            GmContextLimit = source.Campaign.GmContextLimit,
            GmMaxOutputTokens = source.Campaign.GmMaxOutputTokens,
            GmTemperature = source.Campaign.GmTemperature,
            GmTopP = source.Campaign.GmTopP,
            PlayerHistoryBudget = source.Campaign.PlayerHistoryBudget,
            GmHistoryBudget = source.Campaign.GmHistoryBudget,
            UpdatedAt = now
        };
        var participants = source.Participants
            .Select(item => new CampaignParticipant
            {
                CampaignId = clone.Id,
                Kind = item.Kind,
                SortIndex = item.SortIndex,
                IsEnabled = item.IsEnabled,
                SourceCharacterId = item.SourceCharacterId,
                DisplayName = item.DisplayName,
                CharacterSnapshotJson = item.CharacterSnapshotJson,
                PersonaSnapshotJson = item.PersonaSnapshotJson,
                MemorySnapshot = item.MemorySnapshot,
                OriginalWorldKnowledgeSnapshot = item.OriginalWorldKnowledgeSnapshot,
                ProviderId = item.ProviderId,
                ModelId = item.ModelId,
                ContextLimit = item.ContextLimit,
                MaxOutputTokens = item.MaxOutputTokens,
                Temperature = item.Temperature,
                TopP = item.TopP,
                UpdatedAt = now
            })
            .ToArray();
        await SaveDraftAsync(clone, participants, cancellationToken);
        return await GetAsync(clone.Id, cancellationToken)
               ?? throw new InvalidOperationException("另开一局后无法重新读取草稿。");
    }

    public async Task<CampaignEvent> AppendEventAsync(
        CampaignEvent campaignEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaignEvent);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var campaign = await ReadCampaignAsync(
                               connection,
                               (SqliteTransaction)transaction,
                               campaignEvent.CampaignId,
                               cancellationToken)
                           ?? throw new InvalidOperationException("跑团不存在。");
            if (campaign.Status != CampaignStatus.Active)
            {
                throw new InvalidOperationException("只有进行中的跑团可以追加事件。");
            }

            var existing = await ReadEventByOperationAsync(
                connection,
                (SqliteTransaction)transaction,
                campaignEvent.CampaignId,
                campaignEvent.OperationId,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            campaignEvent.SequenceNo = await NextSequenceAsync(
                connection,
                (SqliteTransaction)transaction,
                campaignEvent.CampaignId,
                cancellationToken);
            if (campaignEvent.RoundNo <= 0)
            {
                campaignEvent.RoundNo = campaign.CurrentRound;
            }

            campaignEvent.UpdatedAt = DateTimeOffset.Now;
            await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                campaignEvent,
                cancellationToken);
            await TouchCampaignAsync(
                connection,
                (SqliteTransaction)transaction,
                campaignEvent.CampaignId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return campaignEvent;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UpdateEventAsync(
        CampaignEvent campaignEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaignEvent);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var stored = await ReadEventByIdAsync(
                             connection,
                             (SqliteTransaction)transaction,
                             campaignEvent.Id,
                             cancellationToken)
                         ?? throw new InvalidOperationException("要更新的跑团缓存事件不存在。");
            if (!string.Equals(
                    stored.CampaignId,
                    campaignEvent.CampaignId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    stored.OperationId,
                    campaignEvent.OperationId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("事件身份与生成操作不匹配。");
            }

            if (IsTerminal(stored.GenerationStatus))
            {
                throw new InvalidOperationException("已经结束的生成尝试不能再次改写。");
            }

            ValidateGenerationTransition(
                stored.GenerationStatus,
                campaignEvent.GenerationStatus);
            campaignEvent.SequenceNo = stored.SequenceNo;
            campaignEvent.UpdatedAt = DateTimeOffset.Now;
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE campaign_events
                SET content = $content,
                    structured_data_json = $structuredDataJson,
                    generation_status = $generationStatus,
                    end_reason = $endReason,
                    is_locked = $isLocked,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$content", campaignEvent.Content);
            command.Parameters.AddWithValue(
                "$structuredDataJson",
                campaignEvent.StructuredDataJson);
            command.Parameters.AddWithValue(
                "$generationStatus",
                (int)campaignEvent.GenerationStatus);
            command.Parameters.AddWithValue("$endReason", (int)campaignEvent.EndReason);
            command.Parameters.AddWithValue("$isLocked", campaignEvent.IsLocked);
            command.Parameters.AddWithValue("$updatedAt", campaignEvent.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$id", campaignEvent.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await TouchCampaignAsync(
                connection,
                (SqliteTransaction)transaction,
                campaignEvent.CampaignId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UpdateRuntimeAsync(
        string campaignId,
        int expectedStateVersion,
        CampaignRuntimeUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var campaign = await ReadCampaignAsync(
                               connection,
                               (SqliteTransaction)transaction,
                               campaignId,
                               cancellationToken)
                           ?? throw new InvalidOperationException("跑团不存在。");
            if (campaign.Status != CampaignStatus.Active
                || campaign.StateVersion != expectedStateVersion)
            {
                throw new InvalidOperationException(
                    "跑团状态已经变化，旧操作没有覆盖新的世界状态。请重新载入。");
            }

            var frozenSequenceNo = Math.Max(0, update.FrozenSequenceNo);
            var activatedPendingUser = false;
            if (update.ActivatePendingUser)
            {
                var participants = await ReadParticipantsAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    campaignId,
                    cancellationToken);
                var pendingUsers = participants
                    .Where(item =>
                        item.Kind == CampaignParticipantKind.User
                        && !item.IsEnabled)
                    .ToArray();
                if (pendingUsers.Length > 1)
                {
                    throw new InvalidOperationException(
                        "跑团存在多个待加入 USER 席位，无法安全推进回合。");
                }

                if (pendingUsers.Length == 1)
                {
                    var now = DateTimeOffset.Now;
                    await using (var reserveUserIndex = connection.CreateCommand())
                    {
                        reserveUserIndex.Transaction =
                            (SqliteTransaction)transaction;
                        reserveUserIndex.CommandText = """
                            UPDATE campaign_participants
                            SET sort_index = -1,
                                updated_at = $updatedAt
                            WHERE id = $participantId
                              AND campaign_id = $campaignId
                              AND is_enabled = 0;
                            """;
                        reserveUserIndex.Parameters.AddWithValue(
                            "$updatedAt",
                            now.ToString("O"));
                        reserveUserIndex.Parameters.AddWithValue(
                            "$participantId",
                            pendingUsers[0].Id);
                        reserveUserIndex.Parameters.AddWithValue(
                            "$campaignId",
                            campaignId);
                        if (await reserveUserIndex.ExecuteNonQueryAsync(
                                cancellationToken) != 1)
                        {
                            throw new InvalidOperationException(
                                "待加入 USER 席位未能预留首位。");
                        }
                    }

                    foreach (var participant in participants
                                 .Where(item => item.IsEnabled)
                                 .OrderByDescending(item => item.SortIndex))
                    {
                        await using var shift = connection.CreateCommand();
                        shift.Transaction = (SqliteTransaction)transaction;
                        shift.CommandText = """
                            UPDATE campaign_participants
                            SET sort_index = $sortIndex,
                                updated_at = $updatedAt
                            WHERE id = $participantId
                              AND campaign_id = $campaignId
                              AND is_enabled = 1;
                            """;
                        shift.Parameters.AddWithValue(
                            "$sortIndex",
                            participant.SortIndex + 1);
                        shift.Parameters.AddWithValue(
                            "$updatedAt",
                            now.ToString("O"));
                        shift.Parameters.AddWithValue(
                            "$participantId",
                            participant.Id);
                        shift.Parameters.AddWithValue(
                            "$campaignId",
                            campaignId);
                        if (await shift.ExecuteNonQueryAsync(
                                cancellationToken) != 1)
                        {
                            throw new InvalidOperationException(
                                "原玩家席位未能为 USER 加入腾出位置。");
                        }
                    }

                    await using (var activate = connection.CreateCommand())
                    {
                        activate.Transaction = (SqliteTransaction)transaction;
                        activate.CommandText = """
                            UPDATE campaign_participants
                            SET is_enabled = 1,
                                sort_index = 0,
                                updated_at = $updatedAt
                            WHERE id = $participantId
                              AND campaign_id = $campaignId
                              AND is_enabled = 0;
                            """;
                        activate.Parameters.AddWithValue(
                            "$updatedAt",
                            now.ToString("O"));
                        activate.Parameters.AddWithValue(
                            "$participantId",
                            pendingUsers[0].Id);
                        activate.Parameters.AddWithValue(
                            "$campaignId",
                            campaignId);
                        if (await activate.ExecuteNonQueryAsync(
                                cancellationToken) != 1)
                        {
                            throw new InvalidOperationException(
                                "待加入 USER 席位未能在回合边界启用。");
                        }
                    }

                    var sequence = await NextSequenceAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        campaignId,
                        cancellationToken);
                    await InsertEventAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        new CampaignEvent
                        {
                            CampaignId = campaignId,
                            SequenceNo = sequence,
                            RoundNo = Math.Max(1, update.CurrentRound),
                            Kind = CampaignEventKind.System,
                            ActorId = "system",
                            Visibility = CampaignVisibility.Public,
                            Content =
                                $"{pendingUsers[0].DisplayName} 已从本回合起作为 USER 玩家加入。",
                            SnapshotSequenceNo = frozenSequenceNo,
                            GenerationStatus =
                                CampaignGenerationStatus.Completed,
                            EndReason = CampaignEndReason.Normal,
                            OperationId =
                                $"user-join-activated:{campaignId}:{Math.Max(1, update.CurrentRound)}",
                            IsLocked = true
                        },
                        cancellationToken);
                    frozenSequenceNo = sequence;
                    activatedPendingUser = true;
                }
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE campaigns
                SET phase = $phase,
                    current_round = $currentRound,
                    current_turn_index = $currentTurnIndex,
                    frozen_sequence_no = $frozenSequenceNo,
                    world_summary = $worldSummary,
                    user_also_player =
                        CASE WHEN $activatedPendingUser = 1
                             THEN 1 ELSE user_also_player END,
                    status =
                        CASE WHEN $markCompleted = 1
                             THEN $completed ELSE status END,
                    state_version = state_version + 1,
                    updated_at = $updatedAt
                WHERE id = $campaignId
                  AND status = $active
                  AND state_version = $expectedStateVersion;
                """;
            command.Parameters.AddWithValue("$phase", (int)update.Phase);
            command.Parameters.AddWithValue(
                "$currentRound",
                Math.Max(1, update.CurrentRound));
            command.Parameters.AddWithValue(
                "$currentTurnIndex",
                Math.Max(0, update.CurrentTurnIndex));
            command.Parameters.AddWithValue(
                "$frozenSequenceNo",
                frozenSequenceNo);
            command.Parameters.AddWithValue(
                "$worldSummary",
                update.WorldSummary ?? string.Empty);
            command.Parameters.AddWithValue(
                "$activatedPendingUser",
                activatedPendingUser);
            command.Parameters.AddWithValue(
                "$markCompleted",
                update.MarkCompleted);
            command.Parameters.AddWithValue(
                "$completed",
                (int)CampaignStatus.Completed);
            command.Parameters.AddWithValue(
                "$updatedAt",
                DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$campaignId", campaignId);
            command.Parameters.AddWithValue(
                "$active",
                (int)CampaignStatus.Active);
            command.Parameters.AddWithValue(
                "$expectedStateVersion",
                expectedStateVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    "跑团状态已经变化，旧操作没有覆盖新的世界状态。请重新载入。");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task ScheduleUserJoinAsync(
        string campaignId,
        int expectedStateVersion,
        string displayName,
        string personaSnapshotJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        var normalizedName = string.IsNullOrWhiteSpace(displayName)
            ? "USER"
            : displayName.Trim();
        var normalizedPersona = string.IsNullOrWhiteSpace(personaSnapshotJson)
            ? "{}"
            : personaSnapshotJson;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var campaign = await ReadCampaignAsync(
                               connection,
                               (SqliteTransaction)transaction,
                               campaignId,
                               cancellationToken)
                           ?? throw new InvalidOperationException("跑团不存在。");
            if (campaign.Status != CampaignStatus.Active
                || campaign.StateVersion != expectedStateVersion)
            {
                throw new InvalidOperationException(
                    "跑团状态已经变化，请重新载入后再安排 USER 加入。");
            }

            var participants = await ReadParticipantsAsync(
                connection,
                (SqliteTransaction)transaction,
                campaignId,
                cancellationToken);
            if (campaign.UserAlsoPlayer
                || participants.Any(item =>
                    item.Kind == CampaignParticipantKind.User
                    && item.IsEnabled))
            {
                throw new InvalidOperationException(
                    "当前跑团已经有 USER 玩家席位。");
            }

            if (participants.Any(item =>
                    item.Kind == CampaignParticipantKind.User))
            {
                throw new InvalidOperationException(
                    "USER 已安排从下一回合加入。");
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE campaigns
                    SET state_version = state_version + 1,
                        updated_at = $updatedAt
                    WHERE id = $campaignId
                      AND status = $active
                      AND state_version = $expectedStateVersion;
                    """;
                update.Parameters.AddWithValue(
                    "$updatedAt",
                    DateTimeOffset.Now.ToString("O"));
                update.Parameters.AddWithValue("$campaignId", campaignId);
                update.Parameters.AddWithValue(
                    "$active",
                    (int)CampaignStatus.Active);
                update.Parameters.AddWithValue(
                    "$expectedStateVersion",
                    expectedStateVersion);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        "跑团状态已经变化，请重新载入后再安排 USER 加入。");
                }
            }

            await InsertParticipantAsync(
                connection,
                (SqliteTransaction)transaction,
                new CampaignParticipant
                {
                    CampaignId = campaignId,
                    Kind = CampaignParticipantKind.User,
                    SortIndex = participants
                        .Where(item => item.IsEnabled)
                        .Select(item => item.SortIndex)
                        .DefaultIfEmpty(-1)
                        .Max() + 1,
                    IsEnabled = false,
                    DisplayName = normalizedName,
                    PersonaSnapshotJson = normalizedPersona
                },
                cancellationToken);
            var sequence = await NextSequenceAsync(
                connection,
                (SqliteTransaction)transaction,
                campaignId,
                cancellationToken);
            await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                new CampaignEvent
                {
                    CampaignId = campaignId,
                    SequenceNo = sequence,
                    RoundNo = campaign.CurrentRound,
                    Kind = CampaignEventKind.System,
                    ActorId = "system",
                    Visibility = CampaignVisibility.Public,
                    Content =
                        $"{normalizedName} 已安排从下一回合加入；当前回合阵容不变。",
                    SnapshotSequenceNo = campaign.FrozenSequenceNo,
                    GenerationStatus = CampaignGenerationStatus.Completed,
                    EndReason = CampaignEndReason.Normal,
                    OperationId = $"user-join-scheduled:{campaignId}",
                    IsLocked = true
                },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task UpdateParticipantRouteAsync(
        string campaignId,
        string participantId,
        CampaignModelRoute route,
        CancellationToken cancellationToken = default) =>
        UpdateRouteAsync(
            campaignId,
            participantId,
            route,
            isGm: false,
            cancellationToken);

    public Task UpdateGmRouteAsync(
        string campaignId,
        CampaignModelRoute route,
        CancellationToken cancellationToken = default) =>
        UpdateRouteAsync(
            campaignId,
            participantId: null,
            route,
            isGm: true,
            cancellationToken);

    public async Task ArchiveAsync(
        string campaignId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE campaigns
            SET status = $archived, updated_at = $updatedAt
            WHERE id = $campaignId;
            """;
        command.Parameters.AddWithValue("$archived", (int)CampaignStatus.Archived);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$campaignId", campaignId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateRouteAsync(
        string campaignId,
        string? participantId,
        CampaignModelRoute route,
        bool isGm,
        CancellationToken cancellationToken)
    {
        ValidateRoute(route);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var campaign = await ReadCampaignAsync(
                               connection,
                               (SqliteTransaction)transaction,
                               campaignId,
                               cancellationToken)
                           ?? throw new InvalidOperationException("跑团不存在。");
            if (campaign.Status is CampaignStatus.Completed or CampaignStatus.Archived)
            {
                throw new InvalidOperationException("已结束或已归档的跑团不能更换模型。");
            }

            string targetName;
            if (isGm)
            {
                if (campaign.GmKind != CampaignGmKind.Ai)
                {
                    throw new InvalidOperationException("当前跑团由用户主持，不存在 GM 模型。");
                }

                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE campaigns
                    SET gm_provider_id = $providerId,
                        gm_model_id = $modelId,
                        gm_context_limit = $contextLimit,
                        gm_max_output_tokens = $maxOutputTokens,
                        gm_temperature = $temperature,
                        gm_top_p = $topP,
                        updated_at = $updatedAt
                    WHERE id = $campaignId;
                    """;
                AddRouteParameters(update, campaignId, route);
                await update.ExecuteNonQueryAsync(cancellationToken);
                targetName = "GM";
            }
            else
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
                var participants = await ReadParticipantsAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    campaignId,
                    cancellationToken);
                var participant = participants.FirstOrDefault(item =>
                                      item.Id == participantId)
                                  ?? throw new InvalidOperationException("AI 席位不存在。");
                if (participant.Kind != CampaignParticipantKind.Ai)
                {
                    throw new InvalidOperationException("用户席位不调用模型。");
                }

                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE campaign_participants
                    SET provider_id = $providerId,
                        model_id = $modelId,
                        context_limit = $contextLimit,
                        max_output_tokens = $maxOutputTokens,
                        temperature = $temperature,
                        top_p = $topP,
                        updated_at = $updatedAt
                    WHERE campaign_id = $campaignId AND id = $participantId;
                    """;
                AddRouteParameters(update, campaignId, route);
                update.Parameters.AddWithValue("$participantId", participantId);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException("AI 席位模型更新失败。");
                }

                targetName = participant.DisplayName;
            }

            if (campaign.Status == CampaignStatus.Active)
            {
                var sequence = await NextSequenceAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    campaignId,
                    cancellationToken);
                await InsertEventAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    new CampaignEvent
                    {
                        CampaignId = campaignId,
                        SequenceNo = sequence,
                        RoundNo = campaign.CurrentRound,
                        Kind = CampaignEventKind.System,
                        ActorId = "system",
                        Visibility = CampaignVisibility.GmOnly,
                        Content =
                            $"{targetName} 从下一次生成起改用 {route.ProviderId} / {route.ModelId}。",
                        SnapshotSequenceNo = campaign.FrozenSequenceNo,
                        GenerationStatus = CampaignGenerationStatus.Completed,
                        EndReason = CampaignEndReason.Normal,
                        OperationId = $"route:{Guid.NewGuid():N}",
                        IsLocked = true
                    },
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidateStart(
        Campaign campaign,
        IReadOnlyList<CampaignParticipant> participants)
    {
        if (campaign.Status != CampaignStatus.Draft
            || campaign.Phase != CampaignPhase.Draft)
        {
            throw new InvalidOperationException("只有起始大厅草稿可以开始游戏。");
        }

        if (string.IsNullOrWhiteSpace(campaign.Title)
            || string.IsNullOrWhiteSpace(campaign.WorldSetting)
            || string.IsNullOrWhiteSpace(campaign.Rules)
            || string.IsNullOrWhiteSpace(campaign.OpeningPrompt))
        {
            throw new InvalidOperationException(
                "开始前必须填写标题、世界观、规则和开场情景。");
        }

        var enabled = participants
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.SortIndex)
            .ToArray();
        var aiPlayers = enabled
            .Where(item => item.Kind == CampaignParticipantKind.Ai)
            .ToArray();
        if (aiPlayers.Length > 4)
        {
            throw new InvalidOperationException("一局最多只能有 4 个 AI 玩家。");
        }

        if (campaign.UserAlsoPlayer
            && enabled.Count(item => item.Kind == CampaignParticipantKind.User) != 1)
        {
            throw new InvalidOperationException("玩家模式必须且只能有一个 USER 席位。");
        }

        if (!campaign.UserAlsoPlayer
            && enabled.Any(item => item.Kind == CampaignParticipantKind.User))
        {
            throw new InvalidOperationException("USER 未下场时不能保留 USER 玩家席位。");
        }

        if (enabled.Length == 0)
        {
            throw new InvalidOperationException(
                "至少需要一个玩家：USER 玩家或 AI 玩家均可。");
        }

        if (campaign.GmKind == CampaignGmKind.Ai)
        {
            ValidateRoute(new CampaignModelRoute(
                campaign.GmProviderId,
                campaign.GmModelId,
                campaign.GmContextLimit,
                campaign.GmMaxOutputTokens,
                campaign.GmTemperature,
                campaign.GmTopP));
        }

        foreach (var participant in aiPlayers)
        {
            if (string.IsNullOrWhiteSpace(participant.DisplayName)
                || string.IsNullOrWhiteSpace(participant.CharacterSnapshotJson))
            {
                throw new InvalidOperationException("AI 玩家缺少角色快照。");
            }

            ValidateRoute(new CampaignModelRoute(
                participant.ProviderId,
                participant.ModelId,
                participant.ContextLimit,
                participant.MaxOutputTokens,
                participant.Temperature,
                participant.TopP));
        }
    }

    private static void ValidateRoute(CampaignModelRoute route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route.ModelId);
        if (route.ContextLimit is < 1024 or > 4_194_304
            || route.MaxOutputTokens < 1
            || route.MaxOutputTokens > route.ContextLimit
            || route.Temperature is < 0 or > 2
            || route.TopP is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(route),
                "模型路由的上下文、输出、temperature 或 top_p 超出范围。");
        }
    }

    private static void ValidateParticipantsBelongToCampaign(
        string campaignId,
        IReadOnlyList<CampaignParticipant> participants)
    {
        if (participants.Any(item => !string.Equals(
                item.CampaignId,
                campaignId,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("参与者不属于当前跑团草稿。");
        }

        if (participants.GroupBy(item => item.SortIndex).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("参与者桌序不能重复。");
        }
    }

    private static void ValidateGenerationTransition(
        CampaignGenerationStatus current,
        CampaignGenerationStatus next)
    {
        var valid = current switch
        {
            CampaignGenerationStatus.Queued =>
                next is CampaignGenerationStatus.Streaming
                    or CampaignGenerationStatus.Completed
                    or CampaignGenerationStatus.Interrupted
                    or CampaignGenerationStatus.Failed,
            CampaignGenerationStatus.Streaming =>
                next is CampaignGenerationStatus.Completed
                    or CampaignGenerationStatus.Interrupted
                    or CampaignGenerationStatus.Failed,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException(
                $"非法生成状态转换：{current} → {next}。");
        }
    }

    private static bool IsTerminal(CampaignGenerationStatus status) =>
        status is CampaignGenerationStatus.Completed
            or CampaignGenerationStatus.Interrupted
            or CampaignGenerationStatus.Failed;

    private static void AddRouteParameters(
        SqliteCommand command,
        string campaignId,
        CampaignModelRoute route)
    {
        command.Parameters.AddWithValue("$providerId", route.ProviderId.Trim());
        command.Parameters.AddWithValue("$modelId", route.ModelId.Trim());
        command.Parameters.AddWithValue("$contextLimit", route.ContextLimit);
        command.Parameters.AddWithValue("$maxOutputTokens", route.MaxOutputTokens);
        command.Parameters.AddWithValue("$temperature", route.Temperature);
        command.Parameters.AddWithValue("$topP", route.TopP);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$campaignId", campaignId);
    }

    private static async Task<long> NextSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(sequence_no), 0) + 1
            FROM campaign_events
            WHERE campaign_id = $campaignId;
            """;
        command.Parameters.AddWithValue("$campaignId", campaignId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task TouchCampaignAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE campaigns
            SET updated_at = $updatedAt
            WHERE id = $campaignId;
            """;
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$campaignId", campaignId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCampaignAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Campaign campaign,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO campaigns(
                id, story_id, parent_campaign_id, title, world_setting, rules,
                opening_prompt, gm_kind, user_also_player, flow_preset, status,
                phase, current_round, current_turn_index, frozen_sequence_no,
                state_version, world_summary, user_persona_name,
                user_persona_description, gm_provider_id, gm_model_id,
                gm_context_limit, gm_max_output_tokens, gm_temperature, gm_top_p,
                player_history_budget, gm_history_budget, created_at, updated_at,
                started_at)
            VALUES(
                $id, $storyId, $parentCampaignId, $title, $worldSetting, $rules,
                $openingPrompt, $gmKind, $userAlsoPlayer, $flowPreset, $status,
                $phase, $currentRound, $currentTurnIndex, $frozenSequenceNo,
                $stateVersion, $worldSummary, $userPersonaName,
                $userPersonaDescription, $gmProviderId, $gmModelId,
                $gmContextLimit, $gmMaxOutputTokens, $gmTemperature, $gmTopP,
                $playerHistoryBudget, $gmHistoryBudget, $createdAt, $updatedAt,
                $startedAt)
            ON CONFLICT(id) DO UPDATE SET
                story_id = excluded.story_id,
                parent_campaign_id = excluded.parent_campaign_id,
                title = excluded.title,
                world_setting = excluded.world_setting,
                rules = excluded.rules,
                opening_prompt = excluded.opening_prompt,
                gm_kind = excluded.gm_kind,
                user_also_player = excluded.user_also_player,
                flow_preset = excluded.flow_preset,
                world_summary = excluded.world_summary,
                user_persona_name = excluded.user_persona_name,
                user_persona_description = excluded.user_persona_description,
                gm_provider_id = excluded.gm_provider_id,
                gm_model_id = excluded.gm_model_id,
                gm_context_limit = excluded.gm_context_limit,
                gm_max_output_tokens = excluded.gm_max_output_tokens,
                gm_temperature = excluded.gm_temperature,
                gm_top_p = excluded.gm_top_p,
                player_history_budget = excluded.player_history_budget,
                gm_history_budget = excluded.gm_history_budget,
                updated_at = excluded.updated_at
            WHERE campaigns.status = $draft;
            """;
        AddCampaignParameters(command, campaign);
        command.Parameters.AddWithValue("$draft", (int)CampaignStatus.Draft);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateCampaignAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Campaign campaign,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE campaigns
            SET status = $status,
                phase = $phase,
                current_round = $currentRound,
                current_turn_index = $currentTurnIndex,
                frozen_sequence_no = $frozenSequenceNo,
                state_version = $stateVersion,
                world_summary = $worldSummary,
                updated_at = $updatedAt,
                started_at = $startedAt
            WHERE id = $id AND status = $draft;
            """;
        AddCampaignParameters(command, campaign);
        command.Parameters.AddWithValue("$draft", (int)CampaignStatus.Draft);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("跑团已冻结，开始操作未覆盖现有状态。");
        }
    }

    private static void AddCampaignParameters(
        SqliteCommand command,
        Campaign campaign)
    {
        command.Parameters.AddWithValue("$id", campaign.Id);
        command.Parameters.AddWithValue("$storyId", campaign.StoryId);
        command.Parameters.AddWithValue(
            "$parentCampaignId",
            (object?)campaign.ParentCampaignId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", campaign.Title.Trim());
        command.Parameters.AddWithValue("$worldSetting", campaign.WorldSetting.Trim());
        command.Parameters.AddWithValue("$rules", campaign.Rules.Trim());
        command.Parameters.AddWithValue("$openingPrompt", campaign.OpeningPrompt.Trim());
        command.Parameters.AddWithValue("$gmKind", (int)campaign.GmKind);
        command.Parameters.AddWithValue("$userAlsoPlayer", campaign.UserAlsoPlayer);
        command.Parameters.AddWithValue("$flowPreset", (int)campaign.FlowPreset);
        command.Parameters.AddWithValue("$status", (int)campaign.Status);
        command.Parameters.AddWithValue("$phase", (int)campaign.Phase);
        command.Parameters.AddWithValue("$currentRound", campaign.CurrentRound);
        command.Parameters.AddWithValue("$currentTurnIndex", campaign.CurrentTurnIndex);
        command.Parameters.AddWithValue("$frozenSequenceNo", campaign.FrozenSequenceNo);
        command.Parameters.AddWithValue("$stateVersion", campaign.StateVersion);
        command.Parameters.AddWithValue("$worldSummary", campaign.WorldSummary);
        command.Parameters.AddWithValue("$userPersonaName", campaign.UserPersonaName.Trim());
        command.Parameters.AddWithValue(
            "$userPersonaDescription",
            campaign.UserPersonaDescription.Trim());
        command.Parameters.AddWithValue("$gmProviderId", campaign.GmProviderId);
        command.Parameters.AddWithValue("$gmModelId", campaign.GmModelId);
        command.Parameters.AddWithValue("$gmContextLimit", campaign.GmContextLimit);
        command.Parameters.AddWithValue(
            "$gmMaxOutputTokens",
            campaign.GmMaxOutputTokens);
        command.Parameters.AddWithValue("$gmTemperature", campaign.GmTemperature);
        command.Parameters.AddWithValue("$gmTopP", campaign.GmTopP);
        command.Parameters.AddWithValue(
            "$playerHistoryBudget",
            campaign.PlayerHistoryBudget);
        command.Parameters.AddWithValue("$gmHistoryBudget", campaign.GmHistoryBudget);
        command.Parameters.AddWithValue("$createdAt", campaign.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", campaign.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$startedAt",
            campaign.StartedAt is null
                ? DBNull.Value
                : campaign.StartedAt.Value.ToString("O"));
    }

    private static async Task InsertParticipantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignParticipant participant,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO campaign_participants(
                id, campaign_id, participant_kind, sort_index, is_enabled,
                source_character_id, display_name, character_snapshot_json,
                persona_snapshot_json, memory_snapshot,
                original_world_knowledge_snapshot, provider_id, model_id,
                context_limit, max_output_tokens, temperature, top_p, updated_at)
            VALUES(
                $id, $campaignId, $participantKind, $sortIndex, $isEnabled,
                $sourceCharacterId, $displayName, $characterSnapshotJson,
                $personaSnapshotJson, $memorySnapshot,
                $originalWorldKnowledgeSnapshot, $providerId, $modelId,
                $contextLimit, $maxOutputTokens, $temperature, $topP, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", participant.Id);
        command.Parameters.AddWithValue("$campaignId", participant.CampaignId);
        command.Parameters.AddWithValue("$participantKind", (int)participant.Kind);
        command.Parameters.AddWithValue("$sortIndex", participant.SortIndex);
        command.Parameters.AddWithValue("$isEnabled", participant.IsEnabled);
        command.Parameters.AddWithValue(
            "$sourceCharacterId",
            (object?)participant.SourceCharacterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayName", participant.DisplayName.Trim());
        command.Parameters.AddWithValue(
            "$characterSnapshotJson",
            participant.CharacterSnapshotJson);
        command.Parameters.AddWithValue(
            "$personaSnapshotJson",
            participant.PersonaSnapshotJson);
        command.Parameters.AddWithValue("$memorySnapshot", participant.MemorySnapshot);
        command.Parameters.AddWithValue(
            "$originalWorldKnowledgeSnapshot",
            participant.OriginalWorldKnowledgeSnapshot);
        command.Parameters.AddWithValue("$providerId", participant.ProviderId);
        command.Parameters.AddWithValue("$modelId", participant.ModelId);
        command.Parameters.AddWithValue("$contextLimit", participant.ContextLimit);
        command.Parameters.AddWithValue(
            "$maxOutputTokens",
            participant.MaxOutputTokens);
        command.Parameters.AddWithValue("$temperature", participant.Temperature);
        command.Parameters.AddWithValue("$topP", participant.TopP);
        command.Parameters.AddWithValue("$updatedAt", participant.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignEvent item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO campaign_events(
                id, campaign_id, sequence_no, round_no, event_kind, actor_id,
                recipient_id, visibility, content, structured_data_json,
                snapshot_sequence_no, attempt_no, generation_status, end_reason,
                operation_id, replaces_event_id, is_locked, created_at, updated_at)
            VALUES(
                $id, $campaignId, $sequenceNo, $roundNo, $eventKind, $actorId,
                $recipientId, $visibility, $content, $structuredDataJson,
                $snapshotSequenceNo, $attemptNo, $generationStatus, $endReason,
                $operationId, $replacesEventId, $isLocked, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$campaignId", item.CampaignId);
        command.Parameters.AddWithValue("$sequenceNo", item.SequenceNo);
        command.Parameters.AddWithValue("$roundNo", item.RoundNo);
        command.Parameters.AddWithValue("$eventKind", (int)item.Kind);
        command.Parameters.AddWithValue("$actorId", item.ActorId);
        command.Parameters.AddWithValue(
            "$recipientId",
            (object?)item.RecipientId ?? DBNull.Value);
        command.Parameters.AddWithValue("$visibility", (int)item.Visibility);
        command.Parameters.AddWithValue("$content", item.Content);
        command.Parameters.AddWithValue(
            "$structuredDataJson",
            item.StructuredDataJson);
        command.Parameters.AddWithValue(
            "$snapshotSequenceNo",
            item.SnapshotSequenceNo);
        command.Parameters.AddWithValue("$attemptNo", item.AttemptNo);
        command.Parameters.AddWithValue(
            "$generationStatus",
            (int)item.GenerationStatus);
        command.Parameters.AddWithValue("$endReason", (int)item.EndReason);
        command.Parameters.AddWithValue("$operationId", item.OperationId);
        command.Parameters.AddWithValue(
            "$replacesEventId",
            (object?)item.ReplacesEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isLocked", item.IsLocked);
        command.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Campaign?> ReadCampaignAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string campaignId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, story_id, parent_campaign_id, title, world_setting, rules,
                   opening_prompt, gm_kind, user_also_player, flow_preset, status,
                   phase, current_round, current_turn_index, frozen_sequence_no,
                   state_version, world_summary, user_persona_name,
                   user_persona_description, gm_provider_id, gm_model_id,
                   gm_context_limit, gm_max_output_tokens, gm_temperature, gm_top_p,
                   player_history_budget, gm_history_budget, created_at, updated_at,
                   started_at
            FROM campaigns
            WHERE id = $campaignId;
            """;
        command.Parameters.AddWithValue("$campaignId", campaignId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCampaign(reader)
            : null;
    }

    private static async Task<IReadOnlyList<CampaignParticipant>> ReadParticipantsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string campaignId,
        CancellationToken cancellationToken)
    {
        var result = new List<CampaignParticipant>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, campaign_id, participant_kind, sort_index, is_enabled,
                   source_character_id, display_name, character_snapshot_json,
                   persona_snapshot_json, memory_snapshot,
                   original_world_knowledge_snapshot, provider_id, model_id,
                   context_limit, max_output_tokens, temperature, top_p, updated_at
            FROM campaign_participants
            WHERE campaign_id = $campaignId
            ORDER BY sort_index, id;
            """;
        command.Parameters.AddWithValue("$campaignId", campaignId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadParticipant(reader));
        }

        return result;
    }

    private static async Task<IReadOnlyList<CampaignEvent>> ReadEventsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string campaignId,
        CancellationToken cancellationToken)
    {
        var result = new List<CampaignEvent>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, campaign_id, sequence_no, round_no, event_kind, actor_id,
                   recipient_id, visibility, content, structured_data_json,
                   snapshot_sequence_no, attempt_no, generation_status, end_reason,
                   operation_id, replaces_event_id, is_locked, created_at, updated_at
            FROM campaign_events
            WHERE campaign_id = $campaignId
            ORDER BY sequence_no;
            """;
        command.Parameters.AddWithValue("$campaignId", campaignId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadEvent(reader));
        }

        return result;
    }

    private static async Task<CampaignEvent?> ReadEventByOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string campaignId,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, campaign_id, sequence_no, round_no, event_kind, actor_id,
                   recipient_id, visibility, content, structured_data_json,
                   snapshot_sequence_no, attempt_no, generation_status, end_reason,
                   operation_id, replaces_event_id, is_locked, created_at, updated_at
            FROM campaign_events
            WHERE campaign_id = $campaignId AND operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$campaignId", campaignId);
        command.Parameters.AddWithValue("$operationId", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    private static async Task<CampaignEvent?> ReadEventByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, campaign_id, sequence_no, round_no, event_kind, actor_id,
                   recipient_id, visibility, content, structured_data_json,
                   snapshot_sequence_no, attempt_no, generation_status, end_reason,
                   operation_id, replaces_event_id, is_locked, created_at, updated_at
            FROM campaign_events
            WHERE id = $eventId;
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    private static Campaign ReadCampaign(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            StoryId = reader.GetString(1),
            ParentCampaignId = reader.IsDBNull(2) ? null : reader.GetString(2),
            Title = reader.GetString(3),
            WorldSetting = reader.GetString(4),
            Rules = reader.GetString(5),
            OpeningPrompt = reader.GetString(6),
            GmKind = (CampaignGmKind)reader.GetInt32(7),
            UserAlsoPlayer = reader.GetBoolean(8),
            FlowPreset = (CampaignFlowPreset)reader.GetInt32(9),
            Status = (CampaignStatus)reader.GetInt32(10),
            Phase = (CampaignPhase)reader.GetInt32(11),
            CurrentRound = reader.GetInt32(12),
            CurrentTurnIndex = reader.GetInt32(13),
            FrozenSequenceNo = reader.GetInt64(14),
            StateVersion = reader.GetInt32(15),
            WorldSummary = reader.GetString(16),
            UserPersonaName = reader.GetString(17),
            UserPersonaDescription = reader.GetString(18),
            GmProviderId = reader.GetString(19),
            GmModelId = reader.GetString(20),
            GmContextLimit = reader.GetInt32(21),
            GmMaxOutputTokens = reader.GetInt32(22),
            GmTemperature = reader.GetDouble(23),
            GmTopP = reader.GetDouble(24),
            PlayerHistoryBudget = reader.GetInt32(25),
            GmHistoryBudget = reader.GetInt32(26),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(27)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(28)),
            StartedAt = reader.IsDBNull(29)
                ? null
                : DateTimeOffset.Parse(reader.GetString(29))
        };

    private static CampaignParticipant ReadParticipant(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            CampaignId = reader.GetString(1),
            Kind = (CampaignParticipantKind)reader.GetInt32(2),
            SortIndex = reader.GetInt32(3),
            IsEnabled = reader.GetBoolean(4),
            SourceCharacterId = reader.IsDBNull(5) ? null : reader.GetString(5),
            DisplayName = reader.GetString(6),
            CharacterSnapshotJson = reader.GetString(7),
            PersonaSnapshotJson = reader.GetString(8),
            MemorySnapshot = reader.GetString(9),
            OriginalWorldKnowledgeSnapshot = reader.GetString(10),
            ProviderId = reader.GetString(11),
            ModelId = reader.GetString(12),
            ContextLimit = reader.GetInt32(13),
            MaxOutputTokens = reader.GetInt32(14),
            Temperature = reader.GetDouble(15),
            TopP = reader.GetDouble(16),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(17))
        };

    private static CampaignEvent ReadEvent(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            CampaignId = reader.GetString(1),
            SequenceNo = reader.GetInt64(2),
            RoundNo = reader.GetInt32(3),
            Kind = (CampaignEventKind)reader.GetInt32(4),
            ActorId = reader.GetString(5),
            RecipientId = reader.IsDBNull(6) ? null : reader.GetString(6),
            Visibility = (CampaignVisibility)reader.GetInt32(7),
            Content = reader.GetString(8),
            StructuredDataJson = reader.GetString(9),
            SnapshotSequenceNo = reader.GetInt64(10),
            AttemptNo = reader.GetInt32(11),
            GenerationStatus = (CampaignGenerationStatus)reader.GetInt32(12),
            EndReason = (CampaignEndReason)reader.GetInt32(13),
            OperationId = reader.GetString(14),
            ReplacesEventId = reader.IsDBNull(15) ? null : reader.GetString(15),
            IsLocked = reader.GetBoolean(16),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(17)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(18))
        };
}
