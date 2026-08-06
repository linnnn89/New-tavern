using Microsoft.Data.Sqlite;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Storage;

namespace TavernDesk.Tests;

public sealed class DatabaseAndRepositoryTests
{
    [Fact]
    public async Task VersionOneDatabaseMigratesMessagesToStableSequence()
    {
        using var workspace = new TestWorkspace();
        var paths = new AppDataPaths(workspace.Root);
        paths.EnsureDirectories();
        await CreateVersionOneFixtureAsync(paths.DatabasePath);

        var database = new SqliteDatabase(paths);
        await database.InitializeAsync();

        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, sequence_no
            FROM messages
            ORDER BY sequence_no;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var observed = new List<(string Id, long Sequence)>();
        while (await reader.ReadAsync())
        {
            observed.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        Assert.Equal([("m1", 1L), ("m2", 2L)], observed);
        Assert.Equal(SqliteDatabase.CurrentSchemaVersion, await ReadCurrentVersionAsync(connection));
    }

    [Fact]
    public async Task DeleteConversationRemovesConversationCacheButKeepsCharacterMemory()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var character = new Character
        {
            Name = "保留长期记忆的角色",
            Description = "用于验证会话删除边界。",
            Personality = "稳定",
            Scenario = "本地",
            FirstMessage = ""
        };
        await services.Characters.UpsertAsync(character);
        await services.MemoryBanks.SaveBodyAsync(
            character.Id,
            "角色整体记忆：这段内容不能随聊天记录删除。",
            5000);

        var deletedConversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "需要删除的聊天"
        };
        var retainedConversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "仍要保留的聊天"
        };
        await services.Conversations.UpsertAsync(deletedConversation);
        await services.Conversations.UpsertAsync(retainedConversation);

        var message = new ChatMessage
        {
            ConversationId = deletedConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = character.Id,
            Content = "这条消息和候选回复都应删除。"
        };
        await services.Conversations.AddMessageAsync(message);
        await services.Conversations.AddCandidateAsync(new MessageCandidate
        {
            MessageId = message.Id,
            CandidateIndex = 0,
            Content = message.Content
        });
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = retainedConversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = "保留的聊天消息。"
        });

        await services.Conversations.DeleteConversationAsync(
            deletedConversation.Id);

        Assert.Null(await services.Conversations.GetAsync(deletedConversation.Id));
        Assert.Empty(await services.Conversations.ListMessagesAsync(deletedConversation.Id));
        Assert.Empty(await services.Conversations.ListCandidatesAsync(message.Id));
        Assert.NotNull(await services.Conversations.GetAsync(retainedConversation.Id));
        Assert.Equal(
            "角色整体记忆：这段内容不能随聊天记录删除。",
            (await services.MemoryBanks.GetAsync(character.Id))?.Body);

        await using var connection = services.Database.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM message_search WHERE conversation_id = $id),
                (SELECT COUNT(*) FROM message_search_trigram WHERE conversation_id = $id),
                (SELECT COUNT(*) FROM messages WHERE conversation_id = $id),
                (SELECT COUNT(*) FROM message_candidates WHERE message_id = $messageId);
            """;
        command.Parameters.AddWithValue("$id", deletedConversation.Id);
        command.Parameters.AddWithValue("$messageId", message.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
    }

    [Fact]
    public async Task FutureDatabaseVersionIsRejectedWithoutDowngrade()
    {
        using var workspace = new TestWorkspace();
        var paths = new AppDataPaths(workspace.Root);
        paths.EnsureDirectories();
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_info(version INTEGER NOT NULL, applied_at TEXT NOT NULL);
                INSERT INTO schema_info(version, applied_at) VALUES(1, '2026-08-01T00:00:00+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(2, '2026-08-01T00:00:01+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(3, '2026-08-01T00:00:02+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(4, '2026-08-01T00:00:03+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(5, '2026-08-01T00:00:04+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(6, '2026-08-01T00:00:05+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(7, '2026-08-01T00:00:06+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(8, '2026-08-01T00:00:07+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(9, '2026-08-01T00:00:08+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(10, '2026-08-01T00:00:09+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(11, '2026-08-01T00:00:10+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(12, '2026-08-01T00:00:11+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(13, '2026-08-01T00:00:12+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(14, '2026-08-01T00:00:13+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(15, '2026-08-01T00:00:14+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(16, '2026-08-01T00:00:15+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(17, '2026-08-01T00:00:16+08:00');
                INSERT INTO schema_info(version, applied_at) VALUES(18, '2026-08-01T00:00:17+08:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SqliteDatabase(paths).InitializeAsync());
        Assert.Contains("高于当前软件支持", exception.Message);
    }

    [Fact]
    public async Task VersionTenMigrationPurgesLegacySoftDeletedMessages()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var conversation = new Conversation { Title = "旧回收箱迁移" };
        await services.Conversations.UpsertAsync(conversation);
        var active = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.User,
            Content = "保留消息"
        };
        var legacyDeleted = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.Character,
            Content = "旧回收箱消息"
        };
        await services.Conversations.AddMessageAsync(active);
        await services.Conversations.AddMessageAsync(legacyDeleted);

        await using (var connection = services.Database.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE messages
                SET is_deleted = 1
                WHERE id = $messageId;

                DELETE FROM message_search
                WHERE message_id = $messageId;

                DROP INDEX IF EXISTS ix_provider_models_kind;

                ALTER TABLE provider_models
                DROP COLUMN model_kind;

                ALTER TABLE memory_workflow_settings
                DROP COLUMN maximum_source_user_turns;

                ALTER TABLE memory_workflow_settings
                DROP COLUMN send_only_new_messages;

                ALTER TABLE campaigns
                DROP COLUMN context_token_budget;

                ALTER TABLE campaigns
                DROP COLUMN memory_update_interval_rounds;

                ALTER TABLE campaigns
                DROP COLUMN memory_update_pending_token_threshold;

                ALTER TABLE campaigns
                DROP COLUMN memory_enabled;

                DELETE FROM schema_info
                WHERE version >= 10;
                """;
            command.Parameters.AddWithValue("$messageId", legacyDeleted.Id);
            await command.ExecuteNonQueryAsync();
        }

        await new SqliteDatabase(services.Paths).InitializeAsync();

        Assert.Equal(
            active.Id,
            Assert.Single(
                await services.Conversations.ListMessagesAsync(conversation.Id)).Id);
        await using var verification = services.Database.CreateConnection();
        await verification.OpenAsync();
        await using var count = verification.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM messages WHERE id = $messageId;";
        count.Parameters.AddWithValue("$messageId", legacyDeleted.Id);
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        Assert.Equal(
            SqliteDatabase.CurrentSchemaVersion,
            await ReadCurrentVersionAsync(verification));
    }

    [Fact]
    public async Task ForkCopiesCandidatesWithIndependentMessageIds()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var character = new Character { Name = "候选回复角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "候选回复会话"
        };
        await services.Conversations.UpsertAsync(conversation);
        var message = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = character.Id,
            Content = "当前采用第二个候选",
            ActiveCandidateIndex = 1
        };
        await services.Conversations.AddMessageAsync(message);
        await services.Conversations.AddCandidateAsync(new MessageCandidate
        {
            MessageId = message.Id,
            CandidateIndex = 0,
            Content = "候选一"
        });
        await services.Conversations.AddCandidateAsync(new MessageCandidate
        {
            MessageId = message.Id,
            CandidateIndex = 1,
            Content = "候选二"
        });

        var fork = await services.Conversations.ForkThroughMessageAsync(
            conversation.Id,
            message.Id);
        var forkMessage = Assert.Single(await services.Conversations.ListMessagesAsync(fork.Id));
        var forkCandidates = await services.Conversations.ListCandidatesAsync(forkMessage.Id);

        Assert.NotEqual(message.Id, forkMessage.Id);
        Assert.Equal(1, forkMessage.ActiveCandidateIndex);
        Assert.Equal([0, 1], forkCandidates.Select(item => item.CandidateIndex));
        Assert.All(forkCandidates, item => Assert.Equal(forkMessage.Id, item.MessageId));
        Assert.DoesNotContain(forkCandidates, item => item.Id == message.Id);
    }

    [Fact]
    public async Task PermanentDeleteFollowingUsesConversationSequenceAndCascadesCandidates()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "顺序角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "顺序会话"
        };
        await services.Conversations.UpsertAsync(conversation);

        var messages = Enumerable.Range(1, 4)
            .Select(index => new ChatMessage
            {
                ConversationId = conversation.Id,
                SenderKind = MessageSenderKind.User,
                SenderId = "local-user",
                Content = $"消息 {index}"
            })
            .ToArray();
        foreach (var message in messages)
        {
            await services.Conversations.AddMessageAsync(message);
        }
        await services.Conversations.AddCandidateAsync(new MessageCandidate
        {
            MessageId = messages[1].Id,
            CandidateIndex = 1,
            Content = "将随消息永久删除的候选"
        });

        await services.Conversations.DeleteMessageAsync(
            messages[1].Id,
            includeSubsequent: true);
        var remaining = await services.Conversations.ListMessagesAsync(conversation.Id);

        var survivor = Assert.Single(remaining);
        Assert.Equal(messages[0].Id, survivor.Id);
        Assert.Equal(1, survivor.SequenceNo);
        Assert.Empty(await services.Conversations.ListCandidatesAsync(messages[1].Id));
    }

    [Fact]
    public async Task ConversationPreviewUsesLatestUserOrCharacterMessage()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var conversation = new Conversation { Title = "预览会话" };
        await services.Conversations.UpsertAsync(conversation);
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.User,
            Content = "用户正文"
        });
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.System,
            Content = "系统内部状态"
        });

        var summary = Assert.Single(await services.Conversations.ListAllAsync());

        Assert.Equal("用户正文", summary.LastMessagePreview);
    }

    [Fact]
    public async Task ActivatingGeneratedCandidateUpdatesMessageAndSearchAtomically()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var conversation = new Conversation { Title = "候选原子切换" };
        await services.Conversations.UpsertAsync(conversation);
        var message = new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.Character,
            Content = "原始候选"
        };
        await services.Conversations.AddMessageAsync(message);

        await services.Conversations.AddAndActivateCandidateAsync(new MessageCandidate
        {
            MessageId = message.Id,
            CandidateIndex = 1,
            Content = "新的活动候选"
        });

        var stored = Assert.Single(
            await services.Conversations.ListMessagesAsync(conversation.Id));
        Assert.Equal("新的活动候选", stored.Content);
        Assert.Equal(1, stored.ActiveCandidateIndex);
        var candidate = Assert.Single(
            await services.Conversations.ListCandidatesAsync(message.Id));
        Assert.Equal("新的活动候选", candidate.Content);
    }

    private static async Task CreateVersionOneFixtureAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_info(version INTEGER NOT NULL, applied_at TEXT NOT NULL);
            INSERT INTO schema_info(version, applied_at)
            VALUES(1, '2026-08-01T00:00:00+08:00');

            CREATE TABLE conversations(
                id TEXT PRIMARY KEY,
                character_id TEXT NULL,
                title TEXT NOT NULL,
                mode INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE characters(
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                personality TEXT NOT NULL,
                scenario TEXT NOT NULL,
                first_message TEXT NOT NULL,
                avatar_path TEXT NOT NULL,
                raw_card_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE messages(
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                sender_kind INTEGER NOT NULL,
                sender_id TEXT NOT NULL,
                content TEXT NOT NULL,
                active_candidate_index INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0
            );

            CREATE VIRTUAL TABLE message_search USING fts5(
                message_id UNINDEXED,
                conversation_id UNINDEXED,
                content,
                tokenize = 'unicode61'
            );

            INSERT INTO conversations(
                id, character_id, title, mode, created_at, updated_at)
            VALUES(
                'c1', NULL, '迁移会话', 0,
                '2026-08-01T00:00:00+08:00',
                '2026-08-01T00:00:02+08:00');

            INSERT INTO messages(
                id, conversation_id, sender_kind, sender_id, content,
                active_candidate_index, created_at, updated_at, is_deleted)
            VALUES(
                'm2', 'c1', 0, 'u', '第二条',
                0, '2026-08-01T00:00:02+08:00',
                '2026-08-01T00:00:02+08:00', 0);

            INSERT INTO messages(
                id, conversation_id, sender_kind, sender_id, content,
                active_candidate_index, created_at, updated_at, is_deleted)
            VALUES(
                'm1', 'c1', 0, 'u', '第一条',
                0, '2026-08-01T00:00:01+08:00',
                '2026-08-01T00:00:01+08:00', 0);

            INSERT INTO message_search(message_id, conversation_id, content)
            VALUES
                ('m1', 'c1', '第一条'),
                ('m2', 'c1', '第二条');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ReadCurrentVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_info;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
