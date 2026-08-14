using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteGroupChatRepository : IGroupChatRepository
{
    private readonly SqliteDatabase _database;

    public SqliteGroupChatRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task CreateAsync(
        Conversation conversation,
        GroupChatSettings settings,
        IReadOnlyList<GroupChatMember> members,
        CancellationToken cancellationToken = default)
    {
        if (conversation.Mode != ConversationMode.Group
            || conversation.Id != settings.ConversationId
            || members.Any(member => member.ConversationId != conversation.Id))
        {
            throw new ArgumentException("群聊会话、设置和成员必须引用同一个群聊 ID。");
        }

        ValidateSettings(settings);
        var normalized = NormalizeMembers(members);
        settings.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var saveConversation = connection.CreateCommand())
            {
                saveConversation.Transaction = (SqliteTransaction)transaction;
                saveConversation.CommandText = """
                    INSERT INTO conversations(
                        id, character_id, title, mode, created_at, updated_at)
                    VALUES(
                        $id, NULL, $title, $mode, $createdAt, $updatedAt);
                    """;
                saveConversation.Parameters.AddWithValue("$id", conversation.Id);
                saveConversation.Parameters.AddWithValue("$title", conversation.Title);
                saveConversation.Parameters.AddWithValue("$mode", (int)ConversationMode.Group);
                saveConversation.Parameters.AddWithValue(
                    "$createdAt",
                    conversation.CreatedAt.ToString("O"));
                saveConversation.Parameters.AddWithValue(
                    "$updatedAt",
                    conversation.UpdatedAt.ToString("O"));
                await saveConversation.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertSettingsAsync(
                connection,
                (SqliteTransaction)transaction,
                settings,
                cancellationToken);
            await InsertMembersAsync(
                connection,
                (SqliteTransaction)transaction,
                conversation.Id,
                normalized,
                cancellationToken);
            await using (var state = connection.CreateCommand())
            {
                state.Transaction = (SqliteTransaction)transaction;
                state.CommandText = """
                    INSERT INTO group_chat_state(
                        conversation_id, current_speaker_id, next_speaker_id,
                        automatic_turns, is_paused, pause_reason, updated_at)
                    VALUES($conversationId, '', '', 0, 0, '', $updatedAt);
                    """;
                state.Parameters.AddWithValue("$conversationId", conversation.Id);
                state.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
                await state.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<GroupChatSettings?> GetSettingsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id, relay_mode, auto_continue_enabled,
                   maximum_automatic_turns, pause_on_user_mention,
                   member_memory_enabled, memory_pending_token_threshold,
                   group_system_prompt, merge_system_prompt,
                   merge_user_template, updated_at
            FROM group_chat_settings
            WHERE conversation_id = $conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSettings(reader)
            : null;
    }

    public async Task SaveSettingsAsync(
        GroupChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        settings.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO group_chat_settings(
                conversation_id, relay_mode, auto_continue_enabled,
                maximum_automatic_turns, pause_on_user_mention,
                member_memory_enabled, memory_pending_token_threshold,
                group_system_prompt, merge_system_prompt,
                merge_user_template, updated_at)
            SELECT
                id, $relayMode, $autoContinueEnabled,
                $maximumAutomaticTurns, $pauseOnUserMention,
                $memberMemoryEnabled, $memoryPendingTokenThreshold,
                $groupSystemPrompt, $mergeSystemPrompt,
                $mergeUserTemplate, $updatedAt
            FROM conversations
            WHERE id = $conversationId AND mode = $groupMode
            ON CONFLICT(conversation_id) DO UPDATE SET
                relay_mode = excluded.relay_mode,
                auto_continue_enabled = excluded.auto_continue_enabled,
                maximum_automatic_turns = excluded.maximum_automatic_turns,
                pause_on_user_mention = excluded.pause_on_user_mention,
                member_memory_enabled = excluded.member_memory_enabled,
                memory_pending_token_threshold = excluded.memory_pending_token_threshold,
                group_system_prompt = excluded.group_system_prompt,
                merge_system_prompt = excluded.merge_system_prompt,
                merge_user_template = excluded.merge_user_template,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$conversationId", settings.ConversationId);
        command.Parameters.AddWithValue("$groupMode", (int)ConversationMode.Group);
        command.Parameters.AddWithValue("$relayMode", (int)settings.RelayMode);
        command.Parameters.AddWithValue(
            "$autoContinueEnabled",
            settings.AutoContinueEnabled);
        command.Parameters.AddWithValue(
            "$maximumAutomaticTurns",
            settings.MaximumAutomaticTurns);
        command.Parameters.AddWithValue(
            "$pauseOnUserMention",
            settings.PauseOnUserMention);
        command.Parameters.AddWithValue(
            "$memberMemoryEnabled",
            settings.MemberMemoryEnabled);
        command.Parameters.AddWithValue(
            "$memoryPendingTokenThreshold",
            settings.MemoryPendingTokenThreshold);
        command.Parameters.AddWithValue("$groupSystemPrompt", settings.GroupSystemPrompt);
        command.Parameters.AddWithValue("$mergeSystemPrompt", settings.MergeSystemPrompt);
        command.Parameters.AddWithValue("$mergeUserTemplate", settings.MergeUserTemplate);
        command.Parameters.AddWithValue("$updatedAt", settings.UpdatedAt.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException("群聊设置引用的会话不存在或不是群聊。");
        }
    }

    public async Task SaveConfigurationAsync(
        GroupChatSettings settings,
        IReadOnlyList<GroupChatMember> members,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        if (members.Any(member => member.ConversationId != settings.ConversationId))
        {
            throw new ArgumentException(
                "群聊设置与成员必须引用同一个群聊会话。",
                nameof(members));
        }

        var normalized = NormalizeMembers(members);
        settings.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureGroupConversationAsync(
                connection,
                (SqliteTransaction)transaction,
                settings.ConversationId,
                cancellationToken);

            await using (var save = connection.CreateCommand())
            {
                save.Transaction = (SqliteTransaction)transaction;
                save.CommandText = """
                    INSERT INTO group_chat_settings(
                        conversation_id, relay_mode, auto_continue_enabled,
                        maximum_automatic_turns, pause_on_user_mention,
                        member_memory_enabled, memory_pending_token_threshold,
                        group_system_prompt, merge_system_prompt,
                        merge_user_template, updated_at)
                    VALUES(
                        $conversationId, $relayMode, $autoContinueEnabled,
                        $maximumAutomaticTurns, $pauseOnUserMention,
                        $memberMemoryEnabled, $memoryPendingTokenThreshold,
                        $groupSystemPrompt, $mergeSystemPrompt,
                        $mergeUserTemplate, $updatedAt)
                    ON CONFLICT(conversation_id) DO UPDATE SET
                        relay_mode = excluded.relay_mode,
                        auto_continue_enabled = excluded.auto_continue_enabled,
                        maximum_automatic_turns = excluded.maximum_automatic_turns,
                        pause_on_user_mention = excluded.pause_on_user_mention,
                        member_memory_enabled = excluded.member_memory_enabled,
                        memory_pending_token_threshold = excluded.memory_pending_token_threshold,
                        group_system_prompt = excluded.group_system_prompt,
                        merge_system_prompt = excluded.merge_system_prompt,
                        merge_user_template = excluded.merge_user_template,
                        updated_at = excluded.updated_at;
                    """;
                save.Parameters.AddWithValue("$conversationId", settings.ConversationId);
                save.Parameters.AddWithValue("$relayMode", (int)settings.RelayMode);
                save.Parameters.AddWithValue("$autoContinueEnabled", settings.AutoContinueEnabled);
                save.Parameters.AddWithValue("$maximumAutomaticTurns", settings.MaximumAutomaticTurns);
                save.Parameters.AddWithValue("$pauseOnUserMention", settings.PauseOnUserMention);
                save.Parameters.AddWithValue("$memberMemoryEnabled", settings.MemberMemoryEnabled);
                save.Parameters.AddWithValue("$memoryPendingTokenThreshold", settings.MemoryPendingTokenThreshold);
                save.Parameters.AddWithValue("$groupSystemPrompt", settings.GroupSystemPrompt);
                save.Parameters.AddWithValue("$mergeSystemPrompt", settings.MergeSystemPrompt);
                save.Parameters.AddWithValue("$mergeUserTemplate", settings.MergeUserTemplate);
                save.Parameters.AddWithValue("$updatedAt", settings.UpdatedAt.ToString("O"));
                await save.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText =
                    "DELETE FROM group_chat_members WHERE conversation_id = $conversationId;";
                delete.Parameters.AddWithValue("$conversationId", settings.ConversationId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertMembersAsync(
                connection,
                (SqliteTransaction)transaction,
                settings.ConversationId,
                normalized,
                cancellationToken);
            await DeleteOrphanedMemberMemoriesAsync(
                connection,
                (SqliteTransaction)transaction,
                settings.ConversationId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<GroupChatMember>> ListMembersAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<GroupChatMember>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id, character_id, sort_index, is_enabled
            FROM group_chat_members
            WHERE conversation_id = $conversationId
            ORDER BY sort_index, character_id;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new GroupChatMember
            {
                ConversationId = reader.GetString(0),
                CharacterId = reader.GetString(1),
                SortIndex = reader.GetInt32(2),
                IsEnabled = reader.GetBoolean(3)
            });
        }

        return result;
    }

    public async Task ReplaceMembersAsync(
        string conversationId,
        IReadOnlyList<GroupChatMember> members,
        CancellationToken cancellationToken = default)
    {
        var normalized = members
            .ToArray();
        normalized = NormalizeMembers(normalized);

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureGroupConversationAsync(
                connection,
                (SqliteTransaction)transaction,
                conversationId,
                cancellationToken);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText =
                    "DELETE FROM group_chat_members WHERE conversation_id = $conversationId;";
                delete.Parameters.AddWithValue("$conversationId", conversationId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertMembersAsync(
                connection,
                (SqliteTransaction)transaction,
                conversationId,
                normalized,
                cancellationToken);
            await DeleteOrphanedMemberMemoriesAsync(
                connection,
                (SqliteTransaction)transaction,
                conversationId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<GroupChatState> GetStateAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id, current_speaker_id, next_speaker_id,
                   automatic_turns, is_paused, pause_reason, updated_at
            FROM group_chat_state
            WHERE conversation_id = $conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GroupChatState
            {
                ConversationId = reader.GetString(0),
                CurrentSpeakerId = reader.GetString(1),
                NextSpeakerId = reader.GetString(2),
                AutomaticTurns = reader.GetInt32(3),
                IsPaused = reader.GetBoolean(4),
                PauseReason = reader.GetString(5),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
            }
            : new GroupChatState { ConversationId = conversationId };
    }

    public async Task SaveStateAsync(
        GroupChatState state,
        CancellationToken cancellationToken = default)
    {
        state.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO group_chat_state(
                conversation_id, current_speaker_id, next_speaker_id,
                automatic_turns, is_paused, pause_reason, updated_at)
            VALUES(
                $conversationId, $currentSpeakerId, $nextSpeakerId,
                $automaticTurns, $isPaused, $pauseReason, $updatedAt)
            ON CONFLICT(conversation_id) DO UPDATE SET
                current_speaker_id = excluded.current_speaker_id,
                next_speaker_id = excluded.next_speaker_id,
                automatic_turns = excluded.automatic_turns,
                is_paused = excluded.is_paused,
                pause_reason = excluded.pause_reason,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$conversationId", state.ConversationId);
        command.Parameters.AddWithValue("$currentSpeakerId", state.CurrentSpeakerId);
        command.Parameters.AddWithValue("$nextSpeakerId", state.NextSpeakerId);
        command.Parameters.AddWithValue("$automaticTurns", state.AutomaticTurns);
        command.Parameters.AddWithValue("$isPaused", state.IsPaused);
        command.Parameters.AddWithValue("$pauseReason", state.PauseReason);
        command.Parameters.AddWithValue("$updatedAt", state.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateSettings(GroupChatSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ConversationId);
        if (settings.MaximumAutomaticTurns is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.MaximumAutomaticTurns),
                "自动接力上限必须在 1–100 次之间。");
        }

        if (settings.MemoryPendingTokenThreshold is < 256 or > 100000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.MemoryPendingTokenThreshold),
                "群聊记忆待处理 Token 阈值必须在 256–100000 之间。");
        }

        if (string.IsNullOrWhiteSpace(settings.GroupSystemPrompt)
            || string.IsNullOrWhiteSpace(settings.MergeSystemPrompt)
            || string.IsNullOrWhiteSpace(settings.MergeUserTemplate))
        {
            throw new ArgumentException("群聊与记忆合并提示词不能为空。", nameof(settings));
        }
    }

    private static GroupChatMember[] NormalizeMembers(
        IReadOnlyList<GroupChatMember> members)
    {
        var normalized = members
            .GroupBy(member => member.CharacterId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(member => member.SortIndex)
            .ToArray();
        if (normalized.Count(member => member.IsEnabled) < 2)
        {
            throw new ArgumentException("群聊至少需要两个启用角色。", nameof(members));
        }

        return normalized;
    }

    private static async Task InsertSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GroupChatSettings settings,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO group_chat_settings(
                conversation_id, relay_mode, auto_continue_enabled,
                maximum_automatic_turns, pause_on_user_mention,
                member_memory_enabled, memory_pending_token_threshold,
                group_system_prompt, merge_system_prompt,
                merge_user_template, updated_at)
            VALUES(
                $conversationId, $relayMode, $autoContinueEnabled,
                $maximumAutomaticTurns, $pauseOnUserMention,
                $memberMemoryEnabled, $memoryPendingTokenThreshold,
                $groupSystemPrompt, $mergeSystemPrompt,
                $mergeUserTemplate, $updatedAt);
            """;
        command.Parameters.AddWithValue("$conversationId", settings.ConversationId);
        command.Parameters.AddWithValue("$relayMode", (int)settings.RelayMode);
        command.Parameters.AddWithValue(
            "$autoContinueEnabled",
            settings.AutoContinueEnabled);
        command.Parameters.AddWithValue(
            "$maximumAutomaticTurns",
            settings.MaximumAutomaticTurns);
        command.Parameters.AddWithValue(
            "$pauseOnUserMention",
            settings.PauseOnUserMention);
        command.Parameters.AddWithValue(
            "$memberMemoryEnabled",
            settings.MemberMemoryEnabled);
        command.Parameters.AddWithValue(
            "$memoryPendingTokenThreshold",
            settings.MemoryPendingTokenThreshold);
        command.Parameters.AddWithValue("$groupSystemPrompt", settings.GroupSystemPrompt);
        command.Parameters.AddWithValue("$mergeSystemPrompt", settings.MergeSystemPrompt);
        command.Parameters.AddWithValue("$mergeUserTemplate", settings.MergeUserTemplate);
        command.Parameters.AddWithValue("$updatedAt", settings.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMembersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        IReadOnlyList<GroupChatMember> members,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO group_chat_members(
                    conversation_id, character_id, sort_index, is_enabled)
                VALUES($conversationId, $characterId, $sortIndex, $isEnabled);
                """;
            insert.Parameters.AddWithValue("$conversationId", conversationId);
            insert.Parameters.AddWithValue("$characterId", member.CharacterId);
            insert.Parameters.AddWithValue("$sortIndex", index);
            insert.Parameters.AddWithValue("$isEnabled", member.IsEnabled);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task DeleteOrphanedMemberMemoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM group_memory_checkpoints
            WHERE conversation_id = $conversationId
              AND scope = $memberScope
              AND character_id NOT IN (
                  SELECT character_id
                  FROM group_chat_members
                  WHERE conversation_id = $conversationId);
            DELETE FROM group_memory_banks
            WHERE conversation_id = $conversationId
              AND scope = $memberScope
              AND character_id NOT IN (
                  SELECT character_id
                  FROM group_chat_members
                  WHERE conversation_id = $conversationId);
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$memberScope", (int)GroupMemoryScope.Member);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureGroupConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM conversations
            WHERE id = $conversationId AND mode = $groupMode;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$groupMode", (int)ConversationMode.Group);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            throw new InvalidOperationException("成员列表引用的会话不存在或不是群聊。");
        }
    }

    private static GroupChatSettings ReadSettings(SqliteDataReader reader) =>
        new()
        {
            ConversationId = reader.GetString(0),
            RelayMode = (GroupRelayMode)reader.GetInt32(1),
            AutoContinueEnabled = reader.GetBoolean(2),
            MaximumAutomaticTurns = reader.GetInt32(3),
            PauseOnUserMention = reader.GetBoolean(4),
            MemberMemoryEnabled = reader.GetBoolean(5),
            MemoryPendingTokenThreshold = reader.GetInt32(6),
            GroupSystemPrompt = reader.GetString(7),
            MergeSystemPrompt = reader.GetString(8),
            MergeUserTemplate = reader.GetString(9),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(10))
        };
}
