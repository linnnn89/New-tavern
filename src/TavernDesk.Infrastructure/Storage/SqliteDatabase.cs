using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteDatabase : IDatabaseInitializer
{
    public const int CurrentSchemaVersion = 19;
    private readonly AppDataPaths _paths;

    public SqliteDatabase(AppDataPaths paths)
    {
        _paths = paths;
    }

    public string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();

    public SqliteConnection CreateConnection() => new(ConnectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                """;
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        var appliedVersions = await ReadAppliedVersionsAsync(connection, cancellationToken);
        if (appliedVersions.Count > 0)
        {
            for (var index = 0; index < appliedVersions.Count; index++)
            {
                var expected = index + 1;
                if (appliedVersions[index] != expected)
                {
                    throw new InvalidOperationException(
                        $"数据库迁移记录不连续：期望版本 {expected}，实际为 {appliedVersions[index]}。");
                }
            }
        }

        var currentVersion = appliedVersions.Count == 0 ? 0 : appliedVersions[^1];
        if (currentVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"数据库版本 {currentVersion} 高于当前软件支持的版本 {CurrentSchemaVersion}。请使用更新版本的 TavernDesk 打开，软件不会自动降级数据库。");
        }

        if (currentVersion == CurrentSchemaVersion)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var migration in Migrations.Where(item => item.Version > currentVersion))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken);

                await using var versionCommand = connection.CreateCommand();
                versionCommand.Transaction = (SqliteTransaction)transaction;
                versionCommand.CommandText = """
                    INSERT INTO schema_info(version, applied_at)
                    VALUES($version, $appliedAt);
                    """;
                versionCommand.Parameters.AddWithValue("$version", migration.Version);
                versionCommand.Parameters.AddWithValue("$appliedAt", DateTimeOffset.Now.ToString("O"));
                await versionCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"数据库从版本 {currentVersion} 升级到 {CurrentSchemaVersion} 失败，所有迁移修改已回滚。",
                exception);
        }
    }

    private static readonly IReadOnlyList<SchemaMigration> Migrations =
    [
        new(
            1,
            """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL,
                applied_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS characters (
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

            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY,
                character_id TEXT NULL,
                title TEXT NOT NULL,
                mode INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                sender_kind INTEGER NOT NULL,
                sender_id TEXT NOT NULL,
                content TEXT NOT NULL,
                active_candidate_index INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS message_candidates (
                id TEXT PRIMARY KEY,
                message_id TEXT NOT NULL,
                candidate_index INTEGER NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(message_id, candidate_index),
                FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS provider_profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                adapter_kind INTEGER NOT NULL,
                base_url TEXT NOT NULL,
                secret_reference TEXT NOT NULL,
                request_timeout_seconds REAL NOT NULL,
                is_enabled INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS memory_banks (
                id TEXT PRIMARY KEY,
                owner_id TEXT NOT NULL UNIQUE,
                body TEXT NOT NULL,
                target_tokens INTEGER NOT NULL DEFAULT 5000,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_conversations_updated_at
                ON conversations(updated_at DESC);

            CREATE INDEX IF NOT EXISTS ix_messages_conversation_created
                ON messages(conversation_id, created_at);

            CREATE VIRTUAL TABLE IF NOT EXISTS message_search USING fts5(
                message_id UNINDEXED,
                conversation_id UNINDEXED,
                content,
                tokenize = 'unicode61'
            );
            """),
        new(
            2,
            """
            ALTER TABLE messages
                ADD COLUMN sequence_no INTEGER NOT NULL DEFAULT 0;

            WITH ranked AS (
                SELECT id,
                       ROW_NUMBER() OVER (
                           PARTITION BY conversation_id
                           ORDER BY created_at, rowid
                       ) AS assigned_sequence
                FROM messages
            )
            UPDATE messages
            SET sequence_no = (
                SELECT assigned_sequence
                FROM ranked
                WHERE ranked.id = messages.id
            );

            CREATE UNIQUE INDEX ix_messages_conversation_sequence
                ON messages(conversation_id, sequence_no);

            CREATE INDEX ix_conversations_character_updated
                ON conversations(character_id, updated_at DESC);

            CREATE UNIQUE INDEX ux_schema_info_version
                ON schema_info(version);
            """),
        new(
            3,
            """
            ALTER TABLE characters
                ADD COLUMN source_card_format INTEGER NOT NULL DEFAULT 0;

            ALTER TABLE characters
                ADD COLUMN source_card_path TEXT NOT NULL DEFAULT '';

            ALTER TABLE characters
                ADD COLUMN import_report_json TEXT NOT NULL DEFAULT '{}';

            CREATE TABLE character_shelves (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                sort_index INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE character_shelf_items (
                shelf_id TEXT NOT NULL,
                character_id TEXT NOT NULL,
                sort_index INTEGER NOT NULL DEFAULT 0,
                added_at TEXT NOT NULL,
                PRIMARY KEY(shelf_id, character_id),
                FOREIGN KEY(shelf_id) REFERENCES character_shelves(id) ON DELETE CASCADE,
                FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_character_shelf_items_character
                ON character_shelf_items(character_id, shelf_id);
            """),
        new(
            4,
            """
            CREATE TABLE provider_models (
                provider_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                context_limit INTEGER NOT NULL,
                max_output_tokens INTEGER NOT NULL,
                supports_streaming INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(provider_id, model_id),
                FOREIGN KEY(provider_id) REFERENCES provider_profiles(id) ON DELETE CASCADE
            );

            CREATE TABLE model_function_assignments (
                function_kind INTEGER PRIMARY KEY,
                provider_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                context_limit INTEGER NOT NULL,
                max_output_tokens INTEGER NOT NULL,
                temperature REAL NOT NULL,
                top_p REAL NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(provider_id) REFERENCES provider_profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_provider_models_display_name
                ON provider_models(provider_id, display_name COLLATE NOCASE);
            """),
        new(
            5,
            """
            CREATE TABLE memory_workflow_settings (
                owner_id TEXT PRIMARY KEY,
                auto_generate_enabled INTEGER NOT NULL,
                update_interval_turns INTEGER NOT NULL,
                update_system_prompt TEXT NOT NULL,
                update_user_template TEXT NOT NULL,
                compression_system_prompt TEXT NOT NULL,
                compression_user_template TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE memory_checkpoints (
                owner_id TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                last_sequence_no INTEGER NOT NULL,
                processed_user_turns INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(owner_id, conversation_id),
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE TABLE memory_update_drafts (
                id TEXT PRIMARY KEY,
                target_owner_id TEXT NOT NULL,
                source_conversation_id TEXT NOT NULL,
                draft_kind INTEGER NOT NULL,
                body TEXT NOT NULL,
                request_preview TEXT NOT NULL,
                target_tokens INTEGER NOT NULL,
                source_through_sequence_no INTEGER NOT NULL,
                source_user_turns INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(target_owner_id, source_conversation_id, draft_kind),
                FOREIGN KEY(source_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE TABLE group_chat_settings (
                conversation_id TEXT PRIMARY KEY,
                relay_mode INTEGER NOT NULL,
                auto_continue_enabled INTEGER NOT NULL,
                maximum_automatic_turns INTEGER NOT NULL,
                pause_on_user_mention INTEGER NOT NULL,
                group_system_prompt TEXT NOT NULL,
                merge_system_prompt TEXT NOT NULL,
                merge_user_template TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE TABLE group_chat_members (
                conversation_id TEXT NOT NULL,
                character_id TEXT NOT NULL,
                sort_index INTEGER NOT NULL,
                is_enabled INTEGER NOT NULL,
                PRIMARY KEY(conversation_id, character_id),
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
                FOREIGN KEY(character_id) REFERENCES characters(id) ON DELETE CASCADE
            );

            CREATE TABLE group_chat_state (
                conversation_id TEXT PRIMARY KEY,
                current_speaker_id TEXT NOT NULL,
                next_speaker_id TEXT NOT NULL,
                automatic_turns INTEGER NOT NULL,
                is_paused INTEGER NOT NULL,
                pause_reason TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_memory_update_drafts_source
                ON memory_update_drafts(source_conversation_id, updated_at DESC);

            CREATE INDEX ix_group_chat_members_order
                ON group_chat_members(conversation_id, sort_index);
            """),
        new(
            6,
            """
            CREATE TABLE retrieval_settings (
                conversation_id TEXT PRIMARY KEY,
                is_enabled INTEGER NOT NULL,
                scope INTEGER NOT NULL,
                recent_message_count INTEGER NOT NULL,
                maximum_results INTEGER NOT NULL,
                token_budget INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE TABLE presets (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                description TEXT NOT NULL,
                overlay_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE preset_mounts (
                scope_kind INTEGER NOT NULL,
                scope_id TEXT NOT NULL,
                preset_id TEXT NOT NULL,
                sort_index INTEGER NOT NULL,
                is_enabled INTEGER NOT NULL,
                PRIMARY KEY(scope_kind, scope_id, preset_id),
                FOREIGN KEY(preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_preset_mounts_order
                ON preset_mounts(scope_kind, scope_id, sort_index);

            CREATE VIRTUAL TABLE message_search_trigram USING fts5(
                message_id UNINDEXED,
                conversation_id UNINDEXED,
                content,
                tokenize = 'trigram'
            );

            INSERT INTO message_search_trigram(message_id, conversation_id, content)
            SELECT id, conversation_id, content
            FROM messages
            WHERE is_deleted = 0;

            CREATE TRIGGER messages_search_v2_insert
            AFTER INSERT ON messages
            WHEN NEW.is_deleted = 0
            BEGIN
                INSERT INTO message_search_trigram(message_id, conversation_id, content)
                VALUES(NEW.id, NEW.conversation_id, NEW.content);
            END;

            CREATE TRIGGER messages_search_v2_update
            AFTER UPDATE OF content, is_deleted, conversation_id ON messages
            BEGIN
                DELETE FROM message_search_trigram
                WHERE message_id = OLD.id;

                INSERT INTO message_search_trigram(message_id, conversation_id, content)
                SELECT NEW.id, NEW.conversation_id, NEW.content
                WHERE NEW.is_deleted = 0;
            END;

            CREATE TRIGGER messages_search_v2_delete
            AFTER DELETE ON messages
            BEGIN
                DELETE FROM message_search_trigram
                WHERE message_id = OLD.id;
            END;
            """),
        new(
            7,
            """
            CREATE TABLE chat_jsonl_archives (
                conversation_id TEXT PRIMARY KEY,
                source_file_name TEXT NOT NULL,
                header_json TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );

            CREATE TABLE chat_jsonl_message_payloads (
                message_id TEXT PRIMARY KEY,
                raw_json TEXT NOT NULL,
                FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
            );
            """),
        new(
            8,
            """
            CREATE TABLE campaign_scenarios (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                summary TEXT NOT NULL,
                world_setting TEXT NOT NULL,
                public_rules TEXT NOT NULL,
                gm_instructions TEXT NOT NULL,
                opening_setup TEXT NOT NULL,
                opening_narration TEXT NOT NULL,
                lobby_instructions TEXT NOT NULL,
                legacy_examples_archive TEXT NOT NULL,
                source_card_json TEXT NOT NULL,
                source_file_name TEXT NOT NULL,
                cover_path TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE campaigns (
                id TEXT PRIMARY KEY,
                story_id TEXT NOT NULL,
                parent_campaign_id TEXT NULL,
                title TEXT NOT NULL,
                world_setting TEXT NOT NULL,
                rules TEXT NOT NULL,
                opening_prompt TEXT NOT NULL,
                gm_kind INTEGER NOT NULL,
                user_also_player INTEGER NOT NULL,
                flow_preset INTEGER NOT NULL,
                status INTEGER NOT NULL,
                phase INTEGER NOT NULL,
                current_round INTEGER NOT NULL,
                current_turn_index INTEGER NOT NULL,
                frozen_sequence_no INTEGER NOT NULL,
                state_version INTEGER NOT NULL,
                world_summary TEXT NOT NULL,
                user_persona_name TEXT NOT NULL,
                user_persona_description TEXT NOT NULL,
                gm_provider_id TEXT NOT NULL,
                gm_model_id TEXT NOT NULL,
                gm_context_limit INTEGER NOT NULL,
                gm_max_output_tokens INTEGER NOT NULL,
                gm_temperature REAL NOT NULL,
                gm_top_p REAL NOT NULL,
                player_history_budget INTEGER NOT NULL,
                gm_history_budget INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                started_at TEXT NULL,
                FOREIGN KEY(story_id) REFERENCES campaign_scenarios(id) ON DELETE RESTRICT,
                FOREIGN KEY(parent_campaign_id) REFERENCES campaigns(id) ON DELETE SET NULL
            );

            CREATE TABLE campaign_participants (
                id TEXT PRIMARY KEY,
                campaign_id TEXT NOT NULL,
                participant_kind INTEGER NOT NULL,
                sort_index INTEGER NOT NULL,
                is_enabled INTEGER NOT NULL,
                source_character_id TEXT NULL,
                display_name TEXT NOT NULL,
                character_snapshot_json TEXT NOT NULL,
                persona_snapshot_json TEXT NOT NULL,
                memory_snapshot TEXT NOT NULL,
                original_world_knowledge_snapshot TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                context_limit INTEGER NOT NULL,
                max_output_tokens INTEGER NOT NULL,
                temperature REAL NOT NULL,
                top_p REAL NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(campaign_id, sort_index),
                FOREIGN KEY(campaign_id) REFERENCES campaigns(id) ON DELETE CASCADE,
                FOREIGN KEY(source_character_id) REFERENCES characters(id) ON DELETE SET NULL
            );

            CREATE TABLE campaign_events (
                id TEXT PRIMARY KEY,
                campaign_id TEXT NOT NULL,
                sequence_no INTEGER NOT NULL,
                round_no INTEGER NOT NULL,
                event_kind INTEGER NOT NULL,
                actor_id TEXT NOT NULL,
                recipient_id TEXT NULL,
                visibility INTEGER NOT NULL,
                content TEXT NOT NULL,
                structured_data_json TEXT NOT NULL,
                snapshot_sequence_no INTEGER NOT NULL,
                attempt_no INTEGER NOT NULL,
                generation_status INTEGER NOT NULL,
                end_reason INTEGER NOT NULL,
                operation_id TEXT NOT NULL,
                replaces_event_id TEXT NULL,
                is_locked INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(campaign_id, sequence_no),
                UNIQUE(campaign_id, operation_id),
                FOREIGN KEY(campaign_id) REFERENCES campaigns(id) ON DELETE CASCADE,
                FOREIGN KEY(replaces_event_id) REFERENCES campaign_events(id) ON DELETE SET NULL
            );

            CREATE INDEX ix_campaigns_updated
                ON campaigns(status, updated_at DESC);

            CREATE INDEX ix_campaign_scenarios_updated
                ON campaign_scenarios(updated_at DESC);

            CREATE INDEX ix_campaigns_story
                ON campaigns(story_id, updated_at DESC);

            CREATE INDEX ix_campaign_participants_order
                ON campaign_participants(campaign_id, sort_index);

            CREATE INDEX ix_campaign_events_round
                ON campaign_events(campaign_id, round_no, sequence_no);

            CREATE INDEX ix_campaign_events_recipient
                ON campaign_events(campaign_id, recipient_id, sequence_no);
            """),
        new(
            9,
            """
            ALTER TABLE model_function_assignments
                ADD COLUMN reasoning_enabled INTEGER NOT NULL DEFAULT 0;
            """),
        new(
            10,
            """
            DELETE FROM message_search
            WHERE message_id IN (
                SELECT id
                FROM messages
                WHERE is_deleted = 1
            );

            DELETE FROM messages
            WHERE is_deleted = 1;
            """),
        new(
            11,
            """
            ALTER TABLE provider_models
                ADD COLUMN model_kind INTEGER NOT NULL DEFAULT 0;

            CREATE INDEX ix_provider_models_kind
                ON provider_models(provider_id, model_kind, display_name COLLATE NOCASE);
            """),
        new(
            12,
            """
            CREATE TABLE IF NOT EXISTS worldbooks (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                source_kind INTEGER NOT NULL,
                source_path TEXT NOT NULL,
                source_file_name TEXT NOT NULL,
                source_sha256 TEXT NOT NULL,
                raw_json TEXT NOT NULL,
                is_enabled INTEGER NOT NULL,
                scan_depth INTEGER NOT NULL,
                token_budget INTEGER NOT NULL,
                recursive_scanning INTEGER NOT NULL,
                revision INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS worldbook_sources (
                id TEXT PRIMARY KEY,
                worldbook_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                source_format TEXT NOT NULL,
                source_sha256 TEXT NOT NULL,
                raw_json TEXT NOT NULL,
                parser_version TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                FOREIGN KEY(worldbook_id) REFERENCES worldbooks(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS worldbook_entries (
                worldbook_id TEXT NOT NULL,
                entry_id TEXT NOT NULL,
                title TEXT NOT NULL,
                comment TEXT NOT NULL,
                content TEXT NOT NULL,
                keys_json TEXT NOT NULL,
                secondary_keys_json TEXT NOT NULL,
                content_type INTEGER NOT NULL,
                visibility INTEGER NOT NULL,
                semantic_enabled INTEGER NOT NULL,
                enabled INTEGER NOT NULL,
                constant INTEGER NOT NULL,
                case_sensitive INTEGER NOT NULL,
                match_whole_words INTEGER NOT NULL,
                selective_logic INTEGER NOT NULL,
                insertion_order INTEGER NOT NULL,
                position INTEGER NOT NULL,
                depth INTEGER NOT NULL,
                provider_role TEXT NOT NULL,
                probability INTEGER NOT NULL,
                use_probability INTEGER NOT NULL,
                inclusion_group TEXT NOT NULL,
                group_weight INTEGER NOT NULL,
                exclude_recursion INTEGER NOT NULL,
                original_index INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                extensions_json TEXT NOT NULL,
                PRIMARY KEY(worldbook_id, entry_id),
                FOREIGN KEY(worldbook_id) REFERENCES worldbooks(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS worldbook_mounts (
                worldbook_id TEXT NOT NULL,
                scope_kind INTEGER NOT NULL,
                scope_id TEXT NOT NULL,
                sort_index INTEGER NOT NULL,
                is_enabled INTEGER NOT NULL,
                mounted_revision INTEGER NOT NULL,
                PRIMARY KEY(worldbook_id, scope_kind, scope_id),
                FOREIGN KEY(worldbook_id) REFERENCES worldbooks(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS worldbook_chunks (
                id TEXT PRIMARY KEY,
                worldbook_id TEXT NOT NULL,
                entry_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                content TEXT NOT NULL,
                normalized_content TEXT NOT NULL,
                token_count INTEGER NOT NULL,
                source_locator TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(worldbook_id, entry_id, chunk_index),
                FOREIGN KEY(worldbook_id, entry_id)
                    REFERENCES worldbook_entries(worldbook_id, entry_id)
                    ON DELETE CASCADE
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS worldbook_chunks_fts USING fts5(
                chunk_id UNINDEXED,
                worldbook_id UNINDEXED,
                entry_id UNINDEXED,
                content,
                normalized_content,
                tokenize = 'unicode61'
            );

            CREATE TABLE IF NOT EXISTS embedding_profiles (
                id TEXT PRIMARY KEY,
                provider_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                dimension INTEGER NULL,
                normalize INTEGER NOT NULL,
                batch_size INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS worldbook_embeddings (
                chunk_id TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                dimension INTEGER NOT NULL,
                vector_blob BLOB NOT NULL,
                content_hash TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(chunk_id, profile_id),
                FOREIGN KEY(chunk_id) REFERENCES worldbook_chunks(id) ON DELETE CASCADE,
                FOREIGN KEY(profile_id) REFERENCES embedding_profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_worldbooks_updated
                ON worldbooks(updated_at DESC);

            CREATE INDEX IF NOT EXISTS ix_worldbook_mounts_scope
                ON worldbook_mounts(scope_kind, scope_id, sort_index);

            CREATE INDEX IF NOT EXISTS ix_worldbook_entries_semantic
                ON worldbook_entries(worldbook_id, semantic_enabled, enabled);

            CREATE INDEX IF NOT EXISTS ix_worldbook_chunks_book
                ON worldbook_chunks(worldbook_id, entry_id, chunk_index);
            """),
        new(
            13,
            """
            CREATE TABLE embedding_profiles_v13 (
                id TEXT PRIMARY KEY,
                provider_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                dimension INTEGER NULL,
                normalize INTEGER NOT NULL,
                batch_size INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            INSERT INTO embedding_profiles_v13(
                id, provider_id, model_id, dimension,
                normalize, batch_size, updated_at)
            SELECT id, provider_id, model_id, dimension,
                   normalize, batch_size, updated_at
            FROM embedding_profiles;

            CREATE TABLE worldbook_embeddings_backup_v13 (
                chunk_id TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                dimension INTEGER NOT NULL,
                vector_blob BLOB NOT NULL,
                content_hash TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            INSERT INTO worldbook_embeddings_backup_v13(
                chunk_id, profile_id, model_id, dimension,
                vector_blob, content_hash, updated_at)
            SELECT chunk_id, profile_id, model_id, dimension,
                   vector_blob, content_hash, updated_at
            FROM worldbook_embeddings;

            DROP TABLE worldbook_embeddings;
            DROP TABLE embedding_profiles;
            ALTER TABLE embedding_profiles_v13 RENAME TO embedding_profiles;

            CREATE TABLE worldbook_embeddings (
                chunk_id TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                model_id TEXT NOT NULL,
                dimension INTEGER NOT NULL,
                vector_blob BLOB NOT NULL,
                content_hash TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(chunk_id, profile_id),
                FOREIGN KEY(chunk_id) REFERENCES worldbook_chunks(id) ON DELETE CASCADE,
                FOREIGN KEY(profile_id) REFERENCES embedding_profiles(id) ON DELETE CASCADE
            );

            INSERT INTO worldbook_embeddings(
                chunk_id, profile_id, model_id, dimension,
                vector_blob, content_hash, updated_at)
            SELECT chunk_id, profile_id, model_id, dimension,
                   vector_blob, content_hash, updated_at
            FROM worldbook_embeddings_backup_v13;

            DROP TABLE worldbook_embeddings_backup_v13;
            """),
        new(
            14,
            """
            ALTER TABLE memory_workflow_settings
                ADD COLUMN maximum_source_user_turns INTEGER NOT NULL DEFAULT 20;

            ALTER TABLE memory_workflow_settings
                ADD COLUMN send_only_new_messages INTEGER NOT NULL DEFAULT 1;
            """),
        new(
            15,
            """
            CREATE TABLE IF NOT EXISTS campaign_memory_banks (
                id TEXT PRIMARY KEY,
                campaign_id TEXT NOT NULL,
                scope INTEGER NOT NULL,
                body TEXT NOT NULL,
                target_tokens INTEGER NOT NULL,
                source_through_event_sequence INTEGER NOT NULL,
                prompt_version TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(campaign_id, scope),
                FOREIGN KEY(campaign_id) REFERENCES campaigns(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS campaign_memory_checkpoints (
                campaign_id TEXT NOT NULL,
                scope INTEGER NOT NULL,
                last_event_sequence INTEGER NOT NULL,
                processed_round INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(campaign_id, scope),
                FOREIGN KEY(campaign_id) REFERENCES campaigns(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_campaign_memory_banks_campaign
                ON campaign_memory_banks(campaign_id, scope);

            CREATE INDEX IF NOT EXISTS ix_campaign_memory_checkpoints_campaign
                ON campaign_memory_checkpoints(campaign_id, scope);
            """),
        new(
            16,
            """
            ALTER TABLE campaigns
                ADD COLUMN context_token_budget INTEGER NOT NULL DEFAULT 15000;

            ALTER TABLE campaigns
                ADD COLUMN memory_update_interval_rounds INTEGER NOT NULL DEFAULT 3;

            ALTER TABLE campaigns
                ADD COLUMN memory_update_pending_token_threshold
                    INTEGER NOT NULL DEFAULT 4000;
            """),
        new(
            17,
            """
            ALTER TABLE campaigns
                ADD COLUMN memory_enabled INTEGER NOT NULL DEFAULT 1;
            """),
        new(
            18,
            """
            ALTER TABLE campaign_scenarios
                ADD COLUMN new_npc_permission INTEGER NOT NULL DEFAULT 2;
            ALTER TABLE campaign_scenarios
                ADD COLUMN relationship_change_permission INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE campaign_scenarios
                ADD COLUMN independent_plot_permission INTEGER NOT NULL DEFAULT 1;

            ALTER TABLE campaigns
                ADD COLUMN gm_instructions TEXT NOT NULL DEFAULT '';
            ALTER TABLE campaigns
                ADD COLUMN new_npc_permission INTEGER NOT NULL DEFAULT 2;
            ALTER TABLE campaigns
                ADD COLUMN relationship_change_permission INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE campaigns
                ADD COLUMN independent_plot_permission INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE campaigns
                ADD COLUMN narrative_state_json TEXT NOT NULL DEFAULT '{}';

            UPDATE campaigns
            SET gm_instructions = COALESCE(
                    (SELECT gm_instructions
                     FROM campaign_scenarios
                     WHERE campaign_scenarios.id = campaigns.story_id),
                    ''),
                new_npc_permission = COALESCE(
                    (SELECT new_npc_permission
                     FROM campaign_scenarios
                     WHERE campaign_scenarios.id = campaigns.story_id),
                    2),
                relationship_change_permission = COALESCE(
                    (SELECT relationship_change_permission
                     FROM campaign_scenarios
                     WHERE campaign_scenarios.id = campaigns.story_id),
                    1),
                independent_plot_permission = COALESCE(
                    (SELECT independent_plot_permission
                     FROM campaign_scenarios
                     WHERE campaign_scenarios.id = campaigns.story_id),
                    1);
            """),
        new(
            19,
            """
            UPDATE campaigns
            SET gm_max_output_tokens = MIN(
                    6000,
                    COALESCE(
                        (SELECT provider_models.max_output_tokens
                         FROM provider_models
                         WHERE provider_models.provider_id = campaigns.gm_provider_id
                           AND provider_models.model_id = campaigns.gm_model_id),
                        gm_max_output_tokens))
            WHERE gm_max_output_tokens = 4096
              AND EXISTS (
                    SELECT 1
                    FROM provider_models
                    WHERE provider_models.provider_id = campaigns.gm_provider_id
                      AND provider_models.model_id = campaigns.gm_model_id
                      AND provider_models.max_output_tokens > 4096);
            """)
    ];

    private static async Task<List<int>> ReadAppliedVersionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'schema_info';
            """;
        if (Convert.ToInt32(await tableCommand.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            return [];
        }

        var versions = new List<int>();
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT version FROM schema_info ORDER BY version;";
        await using var reader = await versionCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private sealed record SchemaMigration(int Version, string Sql);
}
