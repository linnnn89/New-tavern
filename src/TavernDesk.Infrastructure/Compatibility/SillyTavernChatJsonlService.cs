using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Storage;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed class SillyTavernChatJsonlService : IChatArchiveService
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const int MaximumRecords = 250_000;
    private const int MaximumLineCharacters = 8_000_000;
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false
    };

    private readonly SqliteDatabase _database;
    private readonly ICharacterRepository _characters;
    private readonly IConversationRepository _conversations;
    private readonly AppDataPaths _paths;

    public SillyTavernChatJsonlService(
        SqliteDatabase database,
        ICharacterRepository characters,
        IConversationRepository conversations,
        AppDataPaths? paths = null)
    {
        _database = database;
        _characters = characters;
        _conversations = conversations;
        _paths = paths ?? new AppDataPaths();
    }

    public async Task<ChatJsonlImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var fileInfo = new FileInfo(sourceFullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("聊天 JSONL 文件不存在。", sourceFullPath);
        }

        if (fileInfo.Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException(
                $"聊天 JSONL 超过 {MaximumArchiveBytes / 1024 / 1024} MiB 安全上限。");
        }

        // Parse and validate the complete archive before opening a write
        // transaction. A malformed late line must not leave a partial import.
        var parsed = await ParseAsync(
            sourceFullPath,
            fileInfo.LastWriteTimeUtc,
            cancellationToken);
        if (parsed.Messages.Count == 0)
        {
            throw new InvalidDataException("聊天 JSONL 中没有可导入的消息。");
        }

        var warnings = parsed.Warnings.ToList();
        var characters = await _characters.ListAsync(cancellationToken);
        var matchingCharacters = characters
            .Where(character => string.Equals(
                character.Name.Trim(),
                parsed.CharacterName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var linkedCharacter = matchingCharacters.FirstOrDefault();
        if (matchingCharacters.Length > 1)
        {
            warnings.Add(
                $"存在 {matchingCharacters.Length} 个同名角色“{parsed.CharacterName}”，"
                + $"已关联 ID 为 {linkedCharacter!.Id} 的角色。");
        }

        var createdPlaceholder = linkedCharacter is null;
        linkedCharacter ??= CreatePlaceholderCharacter(parsed.CharacterName);
        if (createdPlaceholder)
        {
            warnings.Add(
                $"未找到同名角色“{parsed.CharacterName}”，已创建仅用于承接聊天记录的占位角色；"
                + "可以随后编辑或替换其角色卡设定。");
        }

        var createdAt = parsed.Messages.Min(message => message.CreatedAt);
        var updatedAt = parsed.Messages.Max(message => message.CreatedAt);
        var conversation = new Conversation
        {
            CharacterId = linkedCharacter.Id,
            Title = NormalizeConversationTitle(
                Path.GetFileNameWithoutExtension(sourceFullPath),
                parsed.CharacterName),
            Mode = ConversationMode.SingleCharacter,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
        var importedMessages = parsed.Messages
            .Select((message, index) => CreateImportedMessage(
                conversation.Id,
                linkedCharacter.Id,
                parsed.UserName,
                message,
                index + 1,
                warnings))
            .ToArray();

        // Placeholder character, conversation, raw compatibility payloads,
        // messages and candidates form one import unit and roll back together.
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        try
        {
            if (createdPlaceholder)
            {
                await InsertCharacterAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    linkedCharacter,
                    cancellationToken);
            }

            await InsertConversationAsync(
                connection,
                (SqliteTransaction)transaction,
                conversation,
                cancellationToken);
            await InsertArchiveHeaderAsync(
                connection,
                (SqliteTransaction)transaction,
                conversation.Id,
                Path.GetFileName(sourceFullPath),
                parsed.Header.ToJsonString(CompactJson),
                cancellationToken);
            foreach (var imported in importedMessages)
            {
                await InsertMessageAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    imported.Message,
                    imported.RawJson,
                    cancellationToken);
                foreach (var candidate in imported.Candidates)
                {
                    await InsertCandidateAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        candidate,
                        cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new ChatJsonlImportResult(
            conversation,
            linkedCharacter.Name,
            createdPlaceholder,
            importedMessages.Length,
            importedMessages.Sum(message => message.Candidates.Count),
            warnings);
    }

    public async Task<ChatJsonlExportResult> ExportAsync(
        string conversationId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetAsync(
            conversationId,
            cancellationToken)
            ?? throw new InvalidOperationException("要导出的会话不存在。");
        var messages = await _conversations.ListMessagesAsync(
            conversationId,
            cancellationToken);
        var character = conversation.CharacterId is { Length: > 0 } characterId
            ? await _characters.GetAsync(characterId, cancellationToken)
            : null;
        var archive = await ReadStoredArchiveAsync(
            conversationId,
            cancellationToken);
        var warnings = new List<string>();
        var header = ParseStoredObject(
                         archive.HeaderJson,
                         "聊天头部",
                         warnings)
                     ?? new JsonObject();
        var userName = ReadString(header, "user_name") ?? "USER";
        var characterName = character?.Name
                            ?? ReadString(header, "character_name")
                            ?? conversation.Title;
        header["user_name"] = userName;
        header["character_name"] = characterName;
        if (!header.ContainsKey("create_date"))
        {
            header["create_date"] = conversation.CreatedAt.ToString("O");
        }

        if (header["chat_metadata"] is not JsonObject)
        {
            header["chat_metadata"] = new JsonObject();
        }

        var lines = new List<string>(messages.Count + 1)
        {
            header.ToJsonString(CompactJson)
        };
        var candidateCount = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Start from the original SillyTavern object so unknown extension
            // fields survive round-trip, then overwrite only TavernDesk-owned
            // message and candidate fields with current values.
            var raw = archive.RawMessages.TryGetValue(message.Id, out var rawJson)
                ? ParseStoredObject(rawJson, $"消息 {message.Id}", warnings)
                : null;
            var payload = raw ?? new JsonObject();
            var candidates = archive.Candidates.GetValueOrDefault(message.Id)
                             ?? [];
            candidateCount += candidates.Count;
            WriteKnownMessageFields(
                payload,
                message,
                candidates,
                userName,
                characterName);
            lines.Add(payload.ToJsonString(CompactJson));
        }

        var destinationFullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destinationFullPath)
                        ?? throw new InvalidOperationException("导出路径缺少父目录。");
        Directory.CreateDirectory(directory);
        // Write beside the destination and rename only after a complete flush;
        // cancellation cannot replace a valid export with a truncated JSONL.
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationFullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            await using (var writer = new StreamWriter(
                             stream,
                             new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(line);
                }

                await writer.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationFullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }

        return new ChatJsonlExportResult(
            conversationId,
            destinationFullPath,
            messages.Count,
            candidateCount,
            warnings);
    }

    private static async Task<ParsedArchive> ParseAsync(
        string sourcePath,
        DateTime fileTimestampUtc,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        JsonObject? header = null;
        var messages = new List<ParsedMessage>();
        await using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Length > MaximumLineCharacters)
            {
                throw new InvalidDataException(
                    $"聊天 JSONL 第 {lineNumber} 行超过 {MaximumLineCharacters} 字符上限。");
            }

            JsonObject payload;
            try
            {
                payload = JsonNode.Parse(
                              line,
                              documentOptions: new JsonDocumentOptions
                              {
                                  AllowTrailingCommas = true,
                                  CommentHandling = JsonCommentHandling.Skip,
                                  MaxDepth = 256
                              }) as JsonObject
                          ?? throw new InvalidDataException(
                              $"聊天 JSONL 第 {lineNumber} 行根节点不是对象。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"聊天 JSONL 第 {lineNumber} 行无法解析：{exception.Message}",
                    exception);
            }

            if (header is null && !payload.ContainsKey("mes"))
            {
                header = (JsonObject)payload.DeepClone();
                continue;
            }

            if (!payload.ContainsKey("mes"))
            {
                warnings.Add($"第 {lineNumber} 行不是聊天消息，已跳过。");
                continue;
            }

            if (messages.Count >= MaximumRecords)
            {
                throw new InvalidDataException(
                    $"聊天 JSONL 超过 {MaximumRecords} 条消息安全上限。");
            }

            var fallbackTimestamp = new DateTimeOffset(
                    DateTime.SpecifyKind(fileTimestampUtc, DateTimeKind.Utc))
                .AddMilliseconds(messages.Count);
            messages.Add(ParseMessage(
                payload,
                lineNumber,
                fallbackTimestamp,
                warnings));
        }

        header ??= new JsonObject();
        var characterName = ReadString(header, "character_name")
                            ?? messages
                                .Where(message => !message.IsUser && !message.IsSystem)
                                .Select(message => message.Name)
                                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                            ?? Path.GetFileNameWithoutExtension(sourcePath);
        characterName = NormalizeName(characterName, "导入角色");
        var userName = NormalizeName(
            ReadString(header, "user_name")
            ?? messages
                .Where(message => message.IsUser)
                .Select(message => message.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? "USER",
            "USER");
        header["character_name"] = characterName;
        header["user_name"] = userName;
        if (header["chat_metadata"] is not JsonObject)
        {
            header["chat_metadata"] = new JsonObject();
        }

        return new ParsedArchive(
            header,
            characterName,
            userName,
            messages,
            warnings);
    }

    private static ParsedMessage ParseMessage(
        JsonObject payload,
        int lineNumber,
        DateTimeOffset fallbackTimestamp,
        ICollection<string> warnings)
    {
        var content = ReadString(payload, "mes") ?? string.Empty;
        var isUser = ReadBoolean(payload, "is_user") == true;
        var isSystem = ReadBoolean(payload, "is_system") == true;
        var name = NormalizeName(
            ReadString(payload, "name")
            ?? (isUser ? "USER" : isSystem ? "System" : "Character"),
            isUser ? "USER" : isSystem ? "System" : "Character");
        var swipes = payload["swipes"] is JsonArray swipeArray
            ? swipeArray
                .Select(node => node is JsonValue value
                                && value.TryGetValue<string>(out var swipe)
                    ? swipe
                    : null)
                .Where(swipe => swipe is not null)
                .Cast<string>()
                .ToArray()
            : [];
        var requestedSwipe = ReadInt32(payload, "swipe_id") ?? 0;
        // Preserve the source body even when it disagrees with swipe_id. Both
        // representations are retained so export can round-trip imperfect files.
        var activeSwipe = swipes.Length == 0
            ? 0
            : Math.Clamp(requestedSwipe, 0, swipes.Length - 1);
        if (swipes.Length > 0 && requestedSwipe != activeSwipe)
        {
            warnings.Add(
                $"第 {lineNumber} 行 swipe_id={requestedSwipe} 越界，"
                + $"已按 {activeSwipe} 导入。");
        }

        if (swipes.Length > 0
            && !string.Equals(
                swipes[activeSwipe],
                content,
                StringComparison.Ordinal))
        {
            warnings.Add(
                $"第 {lineNumber} 行当前正文与活动 swipe 不一致；"
                + "正文与原始候选均已分别保留。");
        }

        return new ParsedMessage(
            (JsonObject)payload.DeepClone(),
            name,
            content,
            isUser,
            isSystem,
            swipes,
            activeSwipe,
            ReadTimestamp(payload["send_date"], fallbackTimestamp));
    }

    private static ImportedMessage CreateImportedMessage(
        string conversationId,
        string characterId,
        string userName,
        ParsedMessage source,
        int sequenceNo,
        ICollection<string> warnings)
    {
        var senderKind = source.IsSystem
            ? MessageSenderKind.System
            : source.IsUser
                ? MessageSenderKind.User
                : MessageSenderKind.Character;
        var senderId = senderKind switch
        {
            MessageSenderKind.User => "local-user",
            MessageSenderKind.Character => characterId,
            _ => "system"
        };
        var message = new ChatMessage
        {
            ConversationId = conversationId,
            SequenceNo = sequenceNo,
            SenderKind = senderKind,
            SenderId = senderId,
            Content = source.Content,
            ActiveCandidateIndex = source.ActiveSwipe,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.CreatedAt
        };
        var candidates = source.Swipes
            .Select((content, index) => new MessageCandidate
            {
                MessageId = message.Id,
                CandidateIndex = index,
                Content = content,
                CreatedAt = source.CreatedAt
            })
            .ToArray();
        var raw = (JsonObject)source.Raw.DeepClone();
        raw["name"] = senderKind == MessageSenderKind.User
            ? userName
            : source.Name;
        if (senderKind == MessageSenderKind.Tool)
        {
            warnings.Add(
                $"消息 #{sequenceNo} 的工具角色已按系统消息导入。");
        }

        return new ImportedMessage(
            message,
            candidates,
            raw.ToJsonString(CompactJson));
    }

    private static Character CreatePlaceholderCharacter(string name)
    {
        var normalizedName = NormalizeName(name, "导入角色");
        var raw = new JsonObject
        {
            ["spec"] = "chara_card_v2",
            ["spec_version"] = "2.0",
            ["data"] = new JsonObject
            {
                ["name"] = normalizedName,
                ["description"] = string.Empty,
                ["personality"] = string.Empty,
                ["scenario"] = string.Empty,
                ["first_mes"] = string.Empty,
                ["mes_example"] = string.Empty,
                ["alternate_greetings"] = new JsonArray(),
                ["tags"] = new JsonArray("聊天 JSONL 占位角色"),
                ["extensions"] = new JsonObject()
            }
        };
        return new Character
        {
            Name = normalizedName,
            RawCardJson = raw.ToJsonString(CompactJson),
            SourceCardFormat = CharacterCardFormat.Json,
            ImportReportJson = new JsonObject
            {
                ["source"] = "chat_jsonl",
                ["placeholder"] = true,
                ["created_at"] = DateTimeOffset.Now.ToString("O")
            }.ToJsonString(CompactJson)
        };
    }

    private static void WriteKnownMessageFields(
        JsonObject payload,
        ChatMessage message,
        IReadOnlyList<MessageCandidate> candidates,
        string userName,
        string characterName)
    {
        var isUser = message.SenderKind == MessageSenderKind.User;
        var isSystem = message.SenderKind
            is MessageSenderKind.System or MessageSenderKind.Tool;
        payload["name"] = isUser
            ? userName
            : isSystem
                ? "System"
                : characterName;
        payload["is_user"] = isUser;
        payload["is_system"] = isSystem;
        payload["mes"] = message.Content;
        if (!payload.ContainsKey("send_date"))
        {
            payload["send_date"] = message.CreatedAt.ToString("O");
        }

        if (payload["extra"] is not JsonObject)
        {
            payload["extra"] = new JsonObject();
        }

        if (message.SenderKind == MessageSenderKind.Tool)
        {
            payload["extra"]!["taverndesk_sender_kind"] = "tool";
        }

        if (candidates.Count > 0)
        {
            payload["swipes"] = new JsonArray(candidates
                .OrderBy(candidate => candidate.CandidateIndex)
                .Select(candidate => (JsonNode?)JsonValue.Create(candidate.Content))
                .ToArray());
            payload["swipe_id"] = Math.Clamp(
                message.ActiveCandidateIndex,
                0,
                candidates.Count - 1);
        }
    }

    private async Task<StoredArchive> ReadStoredArchiveAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        string? headerJson = null;
        var rawMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, List<MessageCandidate>>(
            StringComparer.Ordinal);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var headerCommand = connection.CreateCommand())
        {
            headerCommand.CommandText = """
                SELECT header_json
                FROM chat_jsonl_archives
                WHERE conversation_id = $conversationId;
                """;
            headerCommand.Parameters.AddWithValue(
                "$conversationId",
                conversationId);
            headerJson = await headerCommand.ExecuteScalarAsync(cancellationToken)
                as string;
        }

        await using (var payloadCommand = connection.CreateCommand())
        {
            payloadCommand.CommandText = """
                SELECT p.message_id, p.raw_json
                FROM chat_jsonl_message_payloads p
                JOIN messages m ON m.id = p.message_id
                WHERE m.conversation_id = $conversationId;
                """;
            payloadCommand.Parameters.AddWithValue(
                "$conversationId",
                conversationId);
            await using var reader = await payloadCommand.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rawMessages[reader.GetString(0)] = reader.GetString(1);
            }
        }

        await using (var candidateCommand = connection.CreateCommand())
        {
            candidateCommand.CommandText = """
                SELECT mc.id, mc.message_id, mc.candidate_index,
                       mc.content, mc.created_at
                FROM message_candidates mc
                JOIN messages m ON m.id = mc.message_id
                WHERE m.conversation_id = $conversationId
                  AND m.is_deleted = 0
                ORDER BY mc.message_id, mc.candidate_index;
                """;
            candidateCommand.Parameters.AddWithValue(
                "$conversationId",
                conversationId);
            await using var reader = await candidateCommand.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var candidate = new MessageCandidate
                {
                    Id = reader.GetString(0),
                    MessageId = reader.GetString(1),
                    CandidateIndex = reader.GetInt32(2),
                    Content = reader.GetString(3),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(4))
                };
                if (!candidates.TryGetValue(
                        candidate.MessageId,
                        out var messageCandidates))
                {
                    messageCandidates = [];
                    candidates[candidate.MessageId] = messageCandidates;
                }

                messageCandidates.Add(candidate);
            }
        }

        return new StoredArchive(
            headerJson,
            rawMessages,
            candidates.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<MessageCandidate>)pair.Value,
                StringComparer.Ordinal));
    }

    private async Task InsertCharacterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Character character,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO characters(
                id, name, description, personality, scenario, first_message,
                avatar_path, raw_card_json, source_card_format, source_card_path,
                import_report_json, created_at, updated_at)
            VALUES(
                $id, $name, $description, $personality, $scenario, $firstMessage,
                $avatarPath, $rawCardJson, $sourceCardFormat, $sourceCardPath,
                $importReportJson, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", character.Id);
        command.Parameters.AddWithValue("$name", character.Name);
        command.Parameters.AddWithValue("$description", character.Description);
        command.Parameters.AddWithValue("$personality", character.Personality);
        command.Parameters.AddWithValue("$scenario", character.Scenario);
        command.Parameters.AddWithValue("$firstMessage", character.FirstMessage);
        command.Parameters.AddWithValue(
            "$avatarPath",
            _paths.ToManagedStoredPath(
                character.AvatarPath,
                AppDataPaths.CharacterCardsDirectoryName,
                character.Id));
        command.Parameters.AddWithValue("$rawCardJson", character.RawCardJson);
        command.Parameters.AddWithValue(
            "$sourceCardFormat",
            (int)character.SourceCardFormat);
        command.Parameters.AddWithValue(
            "$sourceCardPath",
            _paths.ToManagedStoredPath(
                character.SourceCardPath,
                AppDataPaths.CharacterCardsDirectoryName,
                character.Id));
        command.Parameters.AddWithValue(
            "$importReportJson",
            character.ImportReportJson);
        command.Parameters.AddWithValue("$createdAt", character.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", character.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations(
                id, character_id, title, mode, created_at, updated_at)
            VALUES(
                $id, $characterId, $title, $mode, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$characterId", conversation.CharacterId);
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$mode", (int)conversation.Mode);
        command.Parameters.AddWithValue("$createdAt", conversation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", conversation.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertArchiveHeaderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        string sourceFileName,
        string headerJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chat_jsonl_archives(
                conversation_id, source_file_name, header_json, imported_at)
            VALUES(
                $conversationId, $sourceFileName, $headerJson, $importedAt);
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$sourceFileName", sourceFileName);
        command.Parameters.AddWithValue("$headerJson", headerJson);
        command.Parameters.AddWithValue("$importedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessage message,
        string rawJson,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO messages(
                    id, conversation_id, sequence_no, sender_kind, sender_id,
                    content, active_candidate_index, created_at, updated_at,
                    is_deleted)
                VALUES(
                    $id, $conversationId, $sequenceNo, $senderKind, $senderId,
                    $content, $activeCandidateIndex, $createdAt, $updatedAt, 0);
                """;
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$conversationId", message.ConversationId);
            command.Parameters.AddWithValue("$sequenceNo", message.SequenceNo);
            command.Parameters.AddWithValue("$senderKind", (int)message.SenderKind);
            command.Parameters.AddWithValue("$senderId", message.SenderId);
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue(
                "$activeCandidateIndex",
                message.ActiveCandidateIndex);
            command.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", message.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var search = connection.CreateCommand())
        {
            search.Transaction = transaction;
            search.CommandText = """
                INSERT INTO message_search(
                    message_id, conversation_id, content)
                VALUES($messageId, $conversationId, $content);
                """;
            search.Parameters.AddWithValue("$messageId", message.Id);
            search.Parameters.AddWithValue("$conversationId", message.ConversationId);
            search.Parameters.AddWithValue("$content", message.Content);
            await search.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var archive = connection.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = """
                INSERT INTO chat_jsonl_message_payloads(message_id, raw_json)
                VALUES($messageId, $rawJson);
                """;
            archive.Parameters.AddWithValue("$messageId", message.Id);
            archive.Parameters.AddWithValue("$rawJson", rawJson);
            await archive.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_candidates(
                id, message_id, candidate_index, content, created_at)
            VALUES(
                $id, $messageId, $candidateIndex, $content, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", candidate.Id);
        command.Parameters.AddWithValue("$messageId", candidate.MessageId);
        command.Parameters.AddWithValue("$candidateIndex", candidate.CandidateIndex);
        command.Parameters.AddWithValue("$content", candidate.Content);
        command.Parameters.AddWithValue("$createdAt", candidate.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static JsonObject? ParseStoredObject(
        string? json,
        string label,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject
                   ?? throw new JsonException("根节点不是对象。");
        }
        catch (JsonException exception)
        {
            warnings.Add($"{label}的归档 JSON 无法读取，已使用兼容结构：{exception.Message}");
            return null;
        }
    }

    private static DateTimeOffset ReadTimestamp(
        JsonNode? node,
        DateTimeOffset fallback)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var integer))
            {
                try
                {
                    return integer > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(integer)
                        : DateTimeOffset.FromUnixTimeSeconds(integer);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return fallback;
                }
            }

            if (value.TryGetValue<string>(out var text)
                && (DateTimeOffset.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces
                        | DateTimeStyles.AssumeLocal,
                        out var invariant)
                    || DateTimeOffset.TryParse(
                        text,
                        CultureInfo.CurrentCulture,
                        DateTimeStyles.AllowWhiteSpaces
                        | DateTimeStyles.AssumeLocal,
                        out invariant)))
            {
                return invariant;
            }
        }

        return fallback;
    }

    private static string? ReadString(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value
        && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static bool? ReadBoolean(JsonObject source, string propertyName)
    {
        if (source[propertyName] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }

        return value.TryGetValue<int>(out var integer)
            ? integer != 0
            : null;
    }

    private static int? ReadInt32(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value
        && value.TryGetValue<int>(out var result)
            ? result
            : null;

    private static string NormalizeName(string value, string fallback)
    {
        var normalized = value.Trim();
        return normalized.Length == 0
            ? fallback
            : normalized.Length <= 160
                ? normalized
                : normalized[..160];
    }

    private static string NormalizeConversationTitle(
        string fileStem,
        string characterName)
    {
        var title = string.IsNullOrWhiteSpace(fileStem)
            ? $"{characterName} · 导入聊天"
            : fileStem.Trim();
        return title.Length <= 200 ? title : title[..200];
    }

    private sealed record ParsedArchive(
        JsonObject Header,
        string CharacterName,
        string UserName,
        IReadOnlyList<ParsedMessage> Messages,
        IReadOnlyList<string> Warnings);

    private sealed record ParsedMessage(
        JsonObject Raw,
        string Name,
        string Content,
        bool IsUser,
        bool IsSystem,
        IReadOnlyList<string> Swipes,
        int ActiveSwipe,
        DateTimeOffset CreatedAt);

    private sealed record ImportedMessage(
        ChatMessage Message,
        IReadOnlyList<MessageCandidate> Candidates,
        string RawJson);

    private sealed record StoredArchive(
        string? HeaderJson,
        IReadOnlyDictionary<string, string> RawMessages,
        IReadOnlyDictionary<string, IReadOnlyList<MessageCandidate>> Candidates);
}
