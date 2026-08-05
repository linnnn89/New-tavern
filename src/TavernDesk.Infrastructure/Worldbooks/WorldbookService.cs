using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Worldbooks;

public sealed class WorldbookService : IWorldbookService
{
    private const int MaximumChunkCharacters = 900;
    private const int ChunkOverlapCharacters = 120;
    private readonly IWorldbookRepository _repository;
    private readonly IModelAssignmentRepository _assignments;
    private readonly IEmbeddingProviderGateway _embeddings;
    private readonly IReadOnlyList<ICharacterCardCodec> _cardCodecs;
    private readonly IMacroEngine _macros;
    private readonly IProviderProfileRepository? _providers;

    public WorldbookService(
        IWorldbookRepository repository,
        IModelAssignmentRepository assignments,
        IEmbeddingProviderGateway embeddings,
        IReadOnlyList<ICharacterCardCodec> cardCodecs,
        IMacroEngine macros,
        IProviderProfileRepository? providers = null)
    {
        _repository = repository;
        _assignments = assignments;
        _embeddings = embeddings;
        _cardCodecs = cardCodecs;
        _macros = macros;
        _providers = providers;
    }

    public Task<IReadOnlyList<Worldbook>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken);

    public Task<IReadOnlyList<WorldbookEntry>> ListEntriesAsync(
        string worldbookId,
        CancellationToken cancellationToken = default) =>
        _repository.ListEntriesAsync(worldbookId, cancellationToken);

    public Task UpdateEntryTitleAsync(
        string worldbookId,
        string entryId,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("词条名不能为空。", nameof(title));
        }

        return _repository.UpdateEntryTitleAsync(
            worldbookId,
            entryId,
            title,
            cancellationToken);
    }

    public Task<IReadOnlyList<WorldbookMount>> ListMountsAsync(
        string worldbookId,
        CancellationToken cancellationToken = default) =>
        _repository.ListMountsAsync(worldbookId, cancellationToken);

    public Task UpsertMountAsync(
        WorldbookMount mount,
        CancellationToken cancellationToken = default) =>
        _repository.UpsertMountAsync(mount, cancellationToken);

    public Task RemoveMountAsync(
        string worldbookId,
        WorldbookScopeKind scopeKind,
        string scopeId,
        CancellationToken cancellationToken = default) =>
        _repository.RemoveMountAsync(
            worldbookId,
            scopeKind,
            scopeId,
            cancellationToken);

    public Task ReplaceCharacterMountsAsync(
        string worldbookId,
        IReadOnlyList<WorldbookMount> mounts,
        CancellationToken cancellationToken = default) =>
        _repository.ReplaceCharacterMountsAsync(worldbookId, mounts, cancellationToken);

    public Task ReplaceScopeMountsAsync(
        string worldbookId,
        WorldbookScopeKind scopeKind,
        IReadOnlyList<WorldbookMount> mounts,
        CancellationToken cancellationToken = default) =>
        _repository.ReplaceScopeMountsAsync(
            worldbookId,
            scopeKind,
            mounts,
            cancellationToken);

    public async Task<WorldbookImportResult> ImportAsync(
        string sourcePath,
        WorldbookScopeKind scopeKind,
        string? scopeId,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("世界书来源文件不存在。", fullPath);
        }

        var sourceBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes))
            .ToLowerInvariant();
        var extension = file.Extension.ToLowerInvariant();
        var warnings = new List<string>();
        string rawJson;
        WorldbookSourceKind sourceKind;

        if (extension == ".json")
        {
            var text = Encoding.UTF8.GetString(sourceBytes).TrimStart('\uFEFF');
            var parsed = WorldbookJsonParser.Parse(
                text,
                Path.GetFileNameWithoutExtension(fullPath));
            if (parsed.FoundBook && !IsCharacterCardJson(text))
            {
                rawJson = text;
                sourceKind = WorldbookSourceKind.StandaloneJson;
                warnings.AddRange(parsed.Diagnostics);
            }
            else
            {
                var decoded = await DecodeCharacterCardAsync(
                    fullPath,
                    cancellationToken);
                rawJson = decoded.Character.RawCardJson;
                sourceKind = WorldbookSourceKind.CharacterCardEmbedded;
                warnings.AddRange(decoded.Report.Warnings);
            }
        }
        else
        {
            var decoded = await DecodeCharacterCardAsync(
                fullPath,
                cancellationToken);
            rawJson = decoded.Character.RawCardJson;
            sourceKind = WorldbookSourceKind.CharacterCardEmbedded;
            warnings.AddRange(decoded.Report.Warnings);
        }

        var document = WorldbookJsonParser.Parse(
            rawJson,
            Path.GetFileNameWithoutExtension(fullPath));
        if (!document.FoundBook)
        {
            throw new InvalidDataException(
                "所选文件不是可识别的酒馆世界书，也没有包含 character_book。"
                + (document.Diagnostics.Count == 0
                    ? string.Empty
                    : $"\n{string.Join("\n", document.Diagnostics)}"));
        }

        warnings.AddRange(document.Diagnostics);
        var effectiveScope = scopeKind is (WorldbookScopeKind.Character
                                  or WorldbookScopeKind.Campaign)
                              && !string.IsNullOrWhiteSpace(scopeId)
            ? scopeKind
            : WorldbookScopeKind.Global;
        var existingWorldbook = (await _repository.ListAsync(cancellationToken))
            .FirstOrDefault(book =>
                string.Equals(book.SourceSha256, sourceHash, StringComparison.OrdinalIgnoreCase)
                && book.SourceKind == sourceKind);
        if (existingWorldbook is not null)
        {
            var existingEntries = await _repository.ListEntriesAsync(
                existingWorldbook.Id,
                cancellationToken);
            await _repository.UpsertMountAsync(
                new WorldbookMount
                {
                    WorldbookId = existingWorldbook.Id,
                    ScopeKind = effectiveScope,
                    ScopeId = effectiveScope is (WorldbookScopeKind.Character
                        or WorldbookScopeKind.Campaign)
                        ? scopeId!.Trim()
                        : string.Empty,
                    SortIndex = 100,
                    IsEnabled = true,
                    MountedRevision = existingWorldbook.Revision
                },
                cancellationToken);
            warnings.Add("来源内容的 SHA-256 与已有世界书一致；已复用已有工作副本并补充当前挂载，不重复创建索引。" );
            return new WorldbookImportResult(existingWorldbook, existingEntries, warnings);
        }

        var worldbook = new Worldbook
        {
            Name = document.Name,
            Description = document.Description,
            SourceKind = sourceKind,
            SourcePath = fullPath,
            SourceFileName = file.Name,
            SourceSha256 = sourceHash,
            RawJson = rawJson,
            IsEnabled = true,
            ScanDepth = document.ScanDepth,
            TokenBudget = document.TokenBudget,
            RecursiveScanning = document.RecursiveScanning,
            Revision = 1,
            UpdatedAt = DateTimeOffset.Now
        };
        var entries = BindEntries(worldbook.Id, document.Entries, warnings);
        await _repository.UpsertAsync(worldbook, entries, cancellationToken);

        await _repository.UpsertMountAsync(
            new WorldbookMount
            {
                WorldbookId = worldbook.Id,
                ScopeKind = effectiveScope,
                ScopeId = effectiveScope is (WorldbookScopeKind.Character
                    or WorldbookScopeKind.Campaign)
                    ? scopeId!.Trim()
                    : string.Empty,
                SortIndex = 100,
                IsEnabled = true,
                MountedRevision = worldbook.Revision
            },
            cancellationToken);

        return new WorldbookImportResult(worldbook, entries, warnings);
    }

    public Task DeleteAsync(
        string worldbookId,
        CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(worldbookId, cancellationToken);

    public async Task<WorldbookIndexResult> RebuildIndexAsync(
        string worldbookId,
        CancellationToken cancellationToken = default)
    {
        var worldbook = await _repository.GetAsync(worldbookId, cancellationToken)
                         ?? throw new InvalidOperationException("世界书不存在，无法建立索引。");
        var entries = await _repository.ListEntriesAsync(worldbookId, cancellationToken);
        var chunks = BuildChunks(worldbook, entries);
        var diagnostics = new List<string>
        {
            $"已准备 {chunks.Count} 个世界书块用于本地检索索引。"
        };
        if (chunks.Count == 0)
        {
            await _repository.ReplaceChunksAsync(worldbookId, chunks, cancellationToken);
            diagnostics.Add("没有启用语义索引的有效条目；本次未发送 Embedding 请求。");
            return new WorldbookIndexResult(worldbookId, 0, 0, diagnostics);
        }

        var assignment = await _assignments.GetAsync(
            ModelFunctionKind.Embedding,
            cancellationToken);
        if (assignment is null
            || string.IsNullOrWhiteSpace(assignment.ProviderId)
            || string.IsNullOrWhiteSpace(assignment.ModelId))
        {
            diagnostics.Add(
                "尚未配置 Embedding 模型；本次已完成 FTS5 文本索引，未发送远程请求。"
                + "请在设置中为“Embedding 向量化”分配接入商和模型后再次重建，才会建立向量索引。"
                );
            await _repository.ReplaceChunksAsync(worldbookId, chunks, cancellationToken);
            return new WorldbookIndexResult(worldbookId, chunks.Count, 0, diagnostics);
        }

        var profileId = await ProfileIdAsync(
            assignment.ProviderId,
            assignment.ModelId,
            cancellationToken);
        var profile = new EmbeddingProfile
        {
            Id = profileId,
            ProviderId = assignment.ProviderId,
            ModelId = assignment.ModelId,
            Normalize = true,
            BatchSize = 32,
            UpdatedAt = DateTimeOffset.Now
        };
        var existingProfile = await _repository.GetEmbeddingProfileAsync(
            profileId,
            cancellationToken);
        if (existingProfile is not null)
        {
            profile.Dimension = existingProfile.Dimension;
            profile.Normalize = existingProfile.Normalize;
            profile.BatchSize = existingProfile.BatchSize > 0
                ? existingProfile.BatchSize
                : profile.BatchSize;
        }
        var chunkById = chunks.ToDictionary(chunk => chunk.Id, StringComparer.Ordinal);
        var reusableEmbeddings = new Dictionary<string, WorldbookEmbedding>(
            StringComparer.Ordinal);
        if (existingProfile is not null)
        {
            var indexed = await _repository.ListEmbeddingsAsync(
                chunkById.Keys.ToHashSet(StringComparer.Ordinal),
                profileId,
                cancellationToken);
            foreach (var embedding in indexed)
            {
                if (!chunkById.TryGetValue(embedding.ChunkId, out var chunk)
                    || !string.Equals(
                        embedding.ContentHash,
                        chunk.ContentHash,
                        StringComparison.Ordinal)
                    || embedding.VectorBlob.Length != embedding.Dimension * sizeof(float))
                {
                    continue;
                }

                profile.Dimension ??= embedding.Dimension;
                if (profile.Dimension != embedding.Dimension)
                {
                    throw new InvalidDataException(
                        "现有 Embedding 索引包含多个维度；拒绝混合使用，请更换 profile 或重建索引。" );
                }

                reusableEmbeddings[embedding.ChunkId] = embedding;
            }
        }

        var allEmbeddings = new List<WorldbookEmbedding>(chunks.Count);
        var chunksToEmbed = new List<WorldbookChunk>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (reusableEmbeddings.TryGetValue(chunk.Id, out var reusable))
            {
                allEmbeddings.Add(reusable);
            }
            else
            {
                chunksToEmbed.Add(chunk);
            }
        }

        diagnostics.Add(
            $"准备向 Embedding 服务发送 {chunksToEmbed.Count} 个新增或变更文本块；"
            + $"复用 {reusableEmbeddings.Count} 个未变化文本块；模型：{assignment.ModelId}。"
            + "原文不会被改写、总结或净化。");

        for (var offset = 0; offset < chunksToEmbed.Count; offset += profile.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = chunksToEmbed
                .Skip(offset)
                .Take(profile.BatchSize)
                .ToArray();
            var response = await _embeddings.CreateEmbeddingsAsync(
                new EmbeddingRequest(
                    assignment.ProviderId,
                    assignment.ModelId,
                    batch.Select(chunk => chunk.NormalizedContent).ToArray()),
                cancellationToken);
            var seenVectorIndexes = new HashSet<int>();
            foreach (var vector in response.Vectors)
            {
                if (vector.Index < 0 || vector.Index >= batch.Length)
                {
                    throw new InvalidDataException(
                        $"Embedding 服务返回了越界的向量索引 {vector.Index}。");
                }

                if (!seenVectorIndexes.Add(vector.Index))
                {
                    throw new InvalidDataException(
                        $"Embedding 服务重复返回了向量索引 {vector.Index}。" );
                }

                if (vector.Values.Count == 0)
                {
                    throw new InvalidDataException(
                        "Embedding 服务返回了空向量，索引未保存。");
                }

                var values = Normalize(vector.Values, profile.Normalize);
                profile.Dimension ??= values.Length;
                if (profile.Dimension != values.Length)
                {
                    throw new InvalidDataException(
                        $"Embedding 向量维度不一致：已知 {profile.Dimension}，本次为 {values.Length}。"
                        + "不同维度不会混合保存。");
                }

                var chunk = batch[vector.Index];
                allEmbeddings.Add(new WorldbookEmbedding
                {
                    ChunkId = chunk.Id,
                    ProfileId = profileId,
                    ModelId = assignment.ModelId,
                    Dimension = values.Length,
                    VectorBlob = ToBlob(values),
                    ContentHash = chunk.ContentHash,
                    UpdatedAt = DateTimeOffset.Now
                });
            }

            if (seenVectorIndexes.Count != batch.Length)
            {
                throw new InvalidDataException(
                    $"Embedding 服务本批返回 {seenVectorIndexes.Count} 个唯一向量，预期 {batch.Length} 个；索引未保存。" );
            }
        }

        if (allEmbeddings.Count != chunks.Count)
        {
            throw new InvalidDataException(
                $"Embedding 服务返回 {allEmbeddings.Count} 个向量，但本地发送了 {chunks.Count} 个文本块；索引未保存。"
                );
        }

        await _repository.ReplaceIndexedChunksAsync(
            worldbookId,
            chunks,
            profile,
            allEmbeddings,
            cancellationToken);
        diagnostics.Add(
            $"Embedding 索引完成：{allEmbeddings.Count} 个文本块，维度 {profile.Dimension}。"
            + "聊天时将与 FTS5 结果做混合召回。");
        return new WorldbookIndexResult(
            worldbookId,
            chunks.Count,
            profile.Dimension ?? 0,
            diagnostics);
    }

    public async Task<WorldbookRetrievalResult> RetrieveAsync(
        WorldbookRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.QueryText))
        {
            return new WorldbookRetrievalResult([], []);
        }

        var books = await _repository.ListEnabledForCharacterAsync(
            request.CharacterId,
            cancellationToken);
        if (books.Count == 0)
        {
            return new WorldbookRetrievalResult([], []);
        }

        var diagnostics = new List<string>();
        var bookIds = books.Select(book => book.Id).ToHashSet(StringComparer.Ordinal);
        var entries = new Dictionary<(string BookId, string EntryId), WorldbookEntry>();
        foreach (var book in books)
        {
            foreach (var entry in await _repository.ListEntriesAsync(book.Id, cancellationToken))
            {
                entries[(book.Id, entry.Id)] = entry;
            }
        }

        var chunks = await _repository.ListChunksAsync(bookIds, cancellationToken);
        var eligibleChunks = chunks
            .Where(chunk => entries.TryGetValue((chunk.WorldbookId, chunk.EntryId), out var entry)
                            && entry.Enabled
                            && entry.SemanticEnabled
                            && entry.Visibility == WorldbookVisibility.Public)
            .ToArray();
        if (eligibleChunks.Length == 0)
        {
            diagnostics.Add("当前挂载的世界书没有可用于语义/FTS召回的索引块。");
            return new WorldbookRetrievalResult([], diagnostics);
        }

        var ftsHits = await _repository.SearchTextAsync(
            bookIds,
            request.QueryText,
            Math.Clamp(request.MaximumResults * 4, 4, 100),
            cancellationToken);
        var ftsRanks = ftsHits
            .Where(hit => eligibleChunks.Any(chunk => chunk.Id == hit.ChunkId))
            .Select((hit, index) => (hit.ChunkId, Rank: 1d / (60d + index + 1d)))
            .ToDictionary(item => item.ChunkId, item => item.Rank, StringComparer.Ordinal);

        var vectorRanks = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!request.AllowRemoteEmbedding)
        {
            diagnostics.Add("本次为本地上下文预览，未发送 Embedding 查询请求。" );
        }
        else
        {
            var assignment = await _assignments.GetAsync(
                ModelFunctionKind.Embedding,
                cancellationToken);
            if (assignment is null
                || string.IsNullOrWhiteSpace(assignment.ProviderId)
                || string.IsNullOrWhiteSpace(assignment.ModelId))
            {
                diagnostics.Add("未配置 Embedding 模型；本轮仅使用 FTS5 世界书召回。");
            }
            else
            {
            var profileId = await ProfileIdAsync(
                assignment.ProviderId,
                assignment.ModelId,
                cancellationToken);
            var indexed = await _repository.ListEmbeddingsAsync(
                eligibleChunks.Select(chunk => chunk.Id).ToHashSet(StringComparer.Ordinal),
                profileId,
                cancellationToken);
            var vectors = indexed
                .Where(item => eligibleChunks.Any(chunk => chunk.Id == item.ChunkId))
                .ToDictionary(
                    item => item.ChunkId,
                    item => FromBlob(item.VectorBlob, item.Dimension),
                    StringComparer.Ordinal);
            if (vectors.Count == 0)
            {
                diagnostics.Add("当前挂载世界书尚未建立与当前 Embedding 模型匹配的向量索引；本轮仅使用 FTS5。"
                                + "可在世界书页面点击“重建 Embedding 索引”。");
            }
            else
            {
                var queryResponse = await _embeddings.CreateEmbeddingsAsync(
                    new EmbeddingRequest(
                        assignment.ProviderId,
                        assignment.ModelId,
                        [NormalizeText(request.QueryText)]),
                    cancellationToken);
                var queryVector = queryResponse.Vectors.FirstOrDefault()?.Values;
                if (queryVector is null || queryVector.Count == 0)
                {
                    diagnostics.Add("Embedding 查询返回空向量；本轮仅使用 FTS5。");
                }
                else
                {
                    var normalizedQuery = Normalize(queryVector, true);
                    var scored = vectors
                        .Select(item =>
                        {
                            var score = Cosine(normalizedQuery, item.Value);
                            return (item.Key, Score: score);
                        })
                        .Where(item => item.Score >= request.MinimumScore)
                        .OrderByDescending(item => item.Score)
                        .Take(Math.Clamp(request.MaximumResults * 4, 4, 100))
                        .ToArray();
                    foreach (var item in scored.Select((value, index) =>
                                 (value.Key, Rank: 1d / (60d + index + 1d))))
                    {
                        vectorRanks[item.Key] = item.Rank;
                    }

                    diagnostics.Add(
                        $"本轮向量召回候选 {scored.Length} 个；与 FTS5 结果按倒数排名融合。"
                        );
                }
            }
            }
        }

        var combined = ftsRanks
            .Concat(vectorRanks)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => (ChunkId: group.Key, Score: group.Sum(item => item.Value)))
            .OrderByDescending(item => item.Score)
            .ToArray();
        var chunkById = eligibleChunks.ToDictionary(chunk => chunk.Id, StringComparer.Ordinal);
        var bookById = books.ToDictionary(book => book.Id, StringComparer.Ordinal);
        var matches = new List<WorldbookMatch>();
        var usedEntries = new HashSet<(string WorldbookId, string EntryId)>();
        var usedTokens = 0;
        foreach (var candidate in combined)
        {
            if (!chunkById.TryGetValue(candidate.ChunkId, out var chunk)
                || !entries.TryGetValue((chunk.WorldbookId, chunk.EntryId), out var entry)
                || !bookById.TryGetValue(chunk.WorldbookId, out var book))
            {
                continue;
            }

            var content = _macros.Expand(chunk.Content, request.MacroVariables);
            var tokenCount = Math.Max(1, chunk.TokenCount);
            if (usedTokens + tokenCount > Math.Max(1, request.TokenBudget))
            {
                diagnostics.Add(
                    $"世界书语义结果“{entry.Title}”因本轮世界资料 Token 预算不足而跳过。"
                    );
                continue;
            }

            if (!usedEntries.Add((chunk.WorldbookId, chunk.EntryId)))
            {
                diagnostics.Add(
                    $"世界书条目“{entry.Title}”命中了多个重叠文本块；本轮仅保留排名最高的文本块。"
                    );
                continue;
            }

            usedTokens += tokenCount;
            matches.Add(new WorldbookMatch(
                $"semantic:{book.Id}:{chunk.Id}",
                $"{book.Name} / {entry.Title}",
                content,
                entry.Position,
                entry.Depth,
                entry.ProviderRole,
                entry.InsertionOrder,
                0,
                book.Id,
                candidate.Score,
                entry.ContentType,
                entry.Id));
            if (matches.Count >= Math.Clamp(request.MaximumResults, 1, 50))
            {
                break;
            }
        }

        diagnostics.Add(
            $"本轮世界书混合召回最终注入 {matches.Count} 个文本块，约 {usedTokens} tokens。"
            );
        return new WorldbookRetrievalResult(matches, diagnostics);
    }

    public Task<IReadOnlyList<Worldbook>> ListEnabledForCharacterAsync(
        string? characterId,
        CancellationToken cancellationToken = default) =>
        _repository.ListEnabledForCharacterAsync(characterId, cancellationToken);

    private async Task<CharacterCardImportResult> DecodeCharacterCardAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var codec = _cardCodecs.FirstOrDefault(item => item.CanRead(path))
                    ?? throw new NotSupportedException(
                        $"不支持此世界书来源格式：{Path.GetExtension(path)}");
        return await codec.ImportAsync(path, cancellationToken);
    }

    private static bool IsCharacterCardJson(string rawJson)
    {
        try
        {
            var root = JsonNode.Parse(rawJson) as JsonObject;
            return root is not null
                   && root.ContainsKey("spec")
                   && root.ContainsKey("data");
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<WorldbookEntry> BindEntries(
        string worldbookId,
        IReadOnlyList<WorldbookEntry> parsed,
        ICollection<string> warnings)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WorldbookEntry>(parsed.Count);
        foreach (var entry in parsed)
        {
            var id = entry.Id;
            if (!usedIds.Add(id))
            {
                id = $"{id}:{entry.OriginalIndex}";
                warnings.Add(
                    $"世界书存在重复条目 ID“{entry.Id}”；已使用“{id}”作为本地稳定 ID，原始 JSON 未修改。"
                    );
            }

            result.Add(new WorldbookEntry
            {
                WorldbookId = worldbookId,
                Id = id,
                Title = entry.Title,
                Comment = entry.Comment,
                Content = entry.Content,
                Keys = entry.Keys,
                SecondaryKeys = entry.SecondaryKeys,
                ContentType = entry.ContentType,
                Visibility = entry.Visibility,
                SemanticEnabled = entry.SemanticEnabled,
                Enabled = entry.Enabled,
                Constant = entry.Constant,
                CaseSensitive = entry.CaseSensitive,
                MatchWholeWords = entry.MatchWholeWords,
                SelectiveLogic = entry.SelectiveLogic,
                InsertionOrder = entry.InsertionOrder,
                Position = entry.Position,
                Depth = entry.Depth,
                ProviderRole = entry.ProviderRole,
                Probability = entry.Probability,
                UseProbability = entry.UseProbability,
                InclusionGroup = entry.InclusionGroup,
                GroupWeight = entry.GroupWeight,
                ExcludeRecursion = entry.ExcludeRecursion,
                OriginalIndex = entry.OriginalIndex,
                ContentHash = entry.ContentHash,
                ExtensionsJson = entry.ExtensionsJson
            });
        }

        return result;
    }

    private static IReadOnlyList<WorldbookChunk> BuildChunks(
        Worldbook worldbook,
        IReadOnlyList<WorldbookEntry> entries)
    {
        var result = new List<WorldbookChunk>();
        foreach (var entry in entries.Where(item => item.Enabled && item.SemanticEnabled))
        {
            var pieces = SplitText(entry.Content);
            for (var index = 0; index < pieces.Count; index++)
            {
                var content = pieces[index];
                var normalized = NormalizeText(
                    $"标题：{entry.Title}\n"
                    + $"关键词：{string.Join("、", entry.Keys)}\n"
                    + content);
                var hash = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes($"{entry.ContentHash}:{index}:{content}")))
                    .ToLowerInvariant();
                result.Add(new WorldbookChunk
                {
                    Id = $"{worldbook.Id}:{entry.Id}:{index}",
                    WorldbookId = worldbook.Id,
                    EntryId = entry.Id,
                    ChunkIndex = index,
                    Content = content,
                    NormalizedContent = normalized,
                    TokenCount = EstimateTokenCount(normalized),
                    SourceLocator = $"entry:{entry.Id}#chunk:{index}",
                    ContentHash = hash,
                    UpdatedAt = DateTimeOffset.Now
                });
            }
        }

        return result;
    }

    private static IReadOnlyList<string> SplitText(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length <= MaximumChunkCharacters)
        {
            return normalized.Length == 0 ? [] : [normalized];
        }

        var result = new List<string>();
        var start = 0;
        while (start < normalized.Length)
        {
            var proposedEnd = Math.Min(start + MaximumChunkCharacters, normalized.Length);
            var end = proposedEnd;
            if (proposedEnd < normalized.Length)
            {
                var newline = normalized.LastIndexOf('\n', proposedEnd - 1,
                    proposedEnd - start);
                if (newline >= start + MaximumChunkCharacters / 2)
                {
                    end = newline;
                }
            }

            var piece = normalized[start..end].Trim();
            if (piece.Length > 0)
            {
                result.Add(piece);
            }

            if (end >= normalized.Length)
            {
                break;
            }

            start = Math.Max(start + 1, end - ChunkOverlapCharacters);
        }

        return result;
    }

    private static string NormalizeText(string text, string? characterName = null)
    {
        var normalized = text
            .Replace("{{user}}", "用户/玩家", StringComparison.OrdinalIgnoreCase)
            .Replace("{{char}}", characterName ?? "角色", StringComparison.OrdinalIgnoreCase)
            .Replace("{{character}}", characterName ?? "角色", StringComparison.OrdinalIgnoreCase)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        return normalized.Trim();
    }

    private static int EstimateTokenCount(string text)
    {
        var estimate = 0d;
        foreach (var character in text)
        {
            if (character is >= '\u4E00' and <= '\u9FFF')
            {
                estimate += 1.05;
            }
            else if (char.IsWhiteSpace(character))
            {
                estimate += 0.25;
            }
            else if (char.IsPunctuation(character) || char.IsSymbol(character))
            {
                estimate += 0.55;
            }
            else
            {
                estimate += 0.33;
            }
        }

        return Math.Max(1, (int)Math.Ceiling(estimate));
    }

    private static float[] Normalize(
        IReadOnlyList<float> values,
        bool normalize)
    {
        var result = values.ToArray();
        if (!normalize)
        {
            return result;
        }

        var norm = Math.Sqrt(result.Sum(value => (double)value * value));
        if (norm <= double.Epsilon)
        {
            return result;
        }

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = (float)(result[index] / norm);
        }

        return result;
    }

    private static byte[] ToBlob(IReadOnlyList<float> values)
    {
        var blob = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                blob.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(values[index]));
        }

        return blob;
    }

    private static float[] FromBlob(byte[] blob, int dimension)
    {
        if (blob.Length != dimension * sizeof(float))
        {
            return [];
        }

        var values = new float[dimension];
        for (var index = 0; index < dimension; index++)
        {
            values[index] = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(
                    blob.AsSpan(index * sizeof(float), sizeof(float))));
        }

        return values;
    }

    private static double Cosine(
        IReadOnlyList<float> left,
        IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return -1;
        }

        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        return leftNorm <= double.Epsilon || rightNorm <= double.Epsilon
            ? -1
            : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private async Task<string> ProfileIdAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken)
    {
        var provider = _providers is null
            ? null
            : await _providers.GetAsync(providerId, cancellationToken);
        var endpoint = provider?.BaseUrl
            ?.Trim()
            .TrimEnd('/')
            .ToLowerInvariant()
            ?? string.Empty;
        var adapter = provider is null
            ? string.Empty
            : ((int)provider.AdapterKind).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return ProfileId(providerId, modelId, adapter, endpoint);
    }

    private static string ProfileId(
        string providerId,
        string modelId,
        string adapter,
        string endpoint)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{providerId}\0{adapter}\0{endpoint}\0{modelId}")))
            .ToLowerInvariant();
        return $"embedding-{hash[..24]}";
    }
}
