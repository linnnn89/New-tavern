using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Storage;

namespace TavernDesk.Infrastructure.Providers;

public interface IGrokCliRunner
{
    IAsyncEnumerable<string> StreamReplyAsync(
        string prompt,
        string? modelId,
        string workingDirectory,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken = default);
}

public sealed class GrokCliProviderGateway : IProviderGateway
{
    public const string DefaultModelId = "grok-cli-default";
    private readonly IProviderProfileRepository _profiles;
    private readonly AppDataPaths _paths;
    private readonly IGrokCliRunner _runner;

    public GrokCliProviderGateway(
        IProviderProfileRepository profiles,
        AppDataPaths paths,
        IGrokCliRunner? runner = null)
    {
        _profiles = profiles;
        _paths = paths;
        _runner = runner ?? new GrokCliAcpRunner();
    }

    public async Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        await ResolveProfileAsync(providerId, cancellationToken);
        return
        [
            new ProviderModelDescriptor(
                DefaultModelId,
                "Grok CLI 当前默认模型（订阅）")
        ];
    }

    public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
        ModelExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var profile = await ResolveProfileAsync(
            request.ProviderId,
            cancellationToken);
        var prompt = BuildPrompt(request);
        var modelId = string.Equals(
            request.ModelId,
            DefaultModelId,
            StringComparison.Ordinal)
            ? null
            : request.ModelId;
        var receivedContent = false;

        await foreach (var chunk in _runner.StreamReplyAsync(
                           prompt,
                           modelId,
                           _paths.GrokCliRuntimeDirectory,
                           TimeSpan.FromSeconds(profile.RequestTimeoutSeconds),
                           cancellationToken))
        {
            if (chunk.Length == 0)
            {
                continue;
            }

            receivedContent = true;
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                chunk);
        }

        if (!receivedContent)
        {
            throw new InvalidDataException("Grok CLI 已结束，但没有返回聊天正文。");
        }

        yield return new ProviderStreamEvent(
            ProviderStreamEventKind.Completed,
            FinishReason: "stop");
    }

    private async Task<ProviderProfile> ResolveProfileAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(providerId, cancellationToken)
                      ?? throw new InvalidOperationException(
                          "模型分配引用的接入商不存在。");
        if (!profile.IsEnabled)
        {
            throw new InvalidOperationException($"接入商“{profile.Name}”已停用。");
        }

        if (profile.AdapterKind != ProviderAdapterKind.GrokCli)
        {
            throw new NotSupportedException(
                $"接入商“{profile.Name}”不是 Grok CLI 适配器。");
        }

        if (!string.Equals(
                profile.BaseUrl.Trim().TrimEnd('/'),
                "grok://local",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Grok CLI 本机后端地址必须是 grok://local。");
        }

        return profile;
    }

    private static string BuildPrompt(ModelExecutionRequest request) =>
        JsonSerializer.Serialize(new
        {
            task = "根据 messages 中的完整上下文，生成下一条 assistant 角色回复正文。",
            output = new
            {
                format = "plain_text",
                max_output_tokens = request.MaxOutputTokens
            },
            messages = request.Messages.Select(message => new
            {
                role = message.Role,
                content = message.Content
            })
        });
}

public sealed class GrokCliAcpRunner : IGrokCliRunner
{
    private const string SystemPrompt =
        "你是 TavernDesk 的纯文本角色聊天后端。"
        + "把 session/prompt 内 JSON 的 messages 数组视为完整对话，严格遵循其中的 system 角色设定，"
        + "生成下一条 assistant 回复。不得调用终端、文件、网络、MCP、技能、子代理或记忆工具。"
        + "不要解释协议，不要输出 JSON 包装，只输出角色回复正文。";

    public async IAsyncEnumerable<string> StreamReplyAsync(
        string prompt,
        string? modelId,
        string workingDirectory,
        TimeSpan requestTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        Directory.CreateDirectory(workingDirectory);
        await using var connection = AcpConnection.Start(
            BuildStartInfo(modelId, workingDirectory));

        var initialize = await connection.RequestAsync(
            "initialize",
            new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new
                    {
                        readTextFile = false,
                        writeTextFile = false
                    },
                    terminal = false
                }
            },
            requestTimeout,
            cancellationToken);
        if (!HasAuthenticationMethod(initialize, "cached_token"))
        {
            throw new InvalidOperationException(
                "Grok CLI 没有可用的订阅登录。请先在终端执行 grok login，再回到 TavernDesk 重试。");
        }

        await connection.RequestAsync(
            "authenticate",
            new
            {
                methodId = "cached_token",
                _meta = new
                {
                    headless = true
                }
            },
            requestTimeout,
            cancellationToken);
        var session = await connection.RequestAsync(
            "session/new",
            new
            {
                cwd = workingDirectory,
                mcpServers = Array.Empty<object>()
            },
            requestTimeout,
            cancellationToken);
        if (!session.TryGetProperty("sessionId", out var sessionIdElement)
            || sessionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
        {
            throw new InvalidDataException(
                "Grok CLI ACP 没有返回有效的 sessionId。");
        }

        var promptTask = connection.RequestAsync(
            "session/prompt",
            new
            {
                sessionId = sessionIdElement.GetString(),
                prompt = new[]
                {
                    new
                    {
                        type = "text",
                        text = prompt
                    }
                }
            },
            requestTimeout,
            cancellationToken);
        var promptCompleted = false;
        var quietChecks = 0;

        while (!promptCompleted || quietChecks < 2)
        {
            var readChunk = false;
            while (connection.Chunks.TryRead(out var chunk))
            {
                readChunk = true;
                quietChecks = 0;
                yield return chunk;
            }

            if (!promptCompleted && promptTask.IsCompleted)
            {
                await promptTask;
                promptCompleted = true;
                continue;
            }

            if (promptCompleted)
            {
                if (!readChunk)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(150),
                        cancellationToken);
                    quietChecks++;
                }

                continue;
            }

            var chunksReady = connection.Chunks.WaitToReadAsync(
                cancellationToken).AsTask();
            var completed = await Task.WhenAny(promptTask, chunksReady);
            if (completed == promptTask)
            {
                await promptTask;
                promptCompleted = true;
            }
            else if (!await chunksReady)
            {
                await promptTask;
                promptCompleted = true;
            }
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string? modelId,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable(),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        startInfo.Environment["NO_COLOR"] = "1";
        AddArgument(startInfo, "--no-auto-update");
        AddArgument(startInfo, "--cwd", workingDirectory);
        AddArgument(startInfo, "--disable-web-search");
        AddArgument(startInfo, "--no-memory");
        AddArgument(startInfo, "--no-plan");
        AddArgument(startInfo, "--no-subagents");
        AddArgument(startInfo, "--max-turns", "1");
        AddArgument(startInfo, "--permission-mode", "dontAsk");
        foreach (var tool in new[]
                 {
                     "Bash",
                     "Edit",
                     "Read",
                     "Grep",
                     "MCPTool",
                     "WebFetch",
                     "WebSearch"
                 })
        {
            AddArgument(startInfo, "--deny", tool);
        }

        AddArgument(startInfo, "--system-prompt-override", SystemPrompt);
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            AddArgument(startInfo, "--model", modelId);
        }

        AddArgument(startInfo, "agent");
        AddArgument(startInfo, "--no-leader");
        AddArgument(startInfo, "stdio");
        return startInfo;
    }

    private static void AddArgument(
        ProcessStartInfo startInfo,
        string name,
        string? value = null)
    {
        startInfo.ArgumentList.Add(name);
        if (value is not null)
        {
            startInfo.ArgumentList.Add(value);
        }
    }

    private static string ResolveExecutable()
    {
        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        var canonical = Path.Combine(userProfile, ".grok", "bin", "grok.exe");
        if (File.Exists(canonical))
        {
            return canonical;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH")
                     ?? string.Empty)
                 .Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "grok.exe");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception) when (
                directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                // Ignore malformed PATH entries and continue with known names.
            }
        }

        throw new FileNotFoundException(
            "未找到官方 grok.exe。请先安装 Grok Build，并确认 ~/.grok/bin/grok.exe 存在。");
    }

    private static bool HasAuthenticationMethod(
        JsonElement initialize,
        string methodId)
    {
        if (!initialize.TryGetProperty("authMethods", out var methods)
            || methods.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var method in methods.EnumerateArray())
        {
            if (method.ValueKind == JsonValueKind.Object
                && method.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(
                    id.GetString(),
                    methodId,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class AcpConnection : IAsyncDisposable
    {
        private const int MaximumStderrCharacters = 16 * 1024;
        private readonly Process _process;
        private readonly ConcurrentDictionary<
            long,
            TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _readerTask;
        private readonly Task<string> _stderrTask;
        private long _nextId;

        private AcpConnection(Process process)
        {
            _process = process;
            _readerTask = ReadLoopAsync();
            _stderrTask = ReadStderrAsync();
        }

        public ChannelReader<string> Chunks => _chunks.Reader;

        public static AcpConnection Start(ProcessStartInfo startInfo)
        {
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "Windows 未能启动 Grok CLI。");
                }

                return new AcpConnection(process);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        public async Task<JsonElement> RequestAsync(
            string method,
            object parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _nextId);
            var completion =
                new TaskCompletionSource<JsonElement>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("ACP 请求编号冲突。");
            }

            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    method,
                    @params = parameters
                });
                await _writeGate.WaitAsync(cancellationToken);
                try
                {
                    await _process.StandardInput.WriteLineAsync(
                        json.AsMemory(),
                        cancellationToken);
                    await _process.StandardInput.FlushAsync(cancellationToken);
                }
                finally
                {
                    _writeGate.Release();
                }

                return await completion.Task.WaitAsync(
                    timeout,
                    cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Grok CLI ACP 方法 {method} 超过等待上限。",
                    exception);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            try
            {
                _process.StandardInput.Close();
            }
            catch
            {
                // The process may already have closed the pipe.
            }

            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Disposal is best-effort; no child should outlive the request.
                }
            }

            try
            {
                await Task.WhenAll(_readerTask, _stderrTask);
            }
            catch
            {
                // Request failures were already delivered through pending tasks.
            }

            _writeGate.Dispose();
            _lifetime.Dispose();
            _process.Dispose();
        }

        private async Task ReadLoopAsync()
        {
            Exception? failure = null;
            try
            {
                while (true)
                {
                    var line = await _process.StandardOutput.ReadLineAsync(
                        _lifetime.Token);
                    if (line is null)
                    {
                        break;
                    }

                    using var document = JsonDocument.Parse(
                        line,
                        new JsonDocumentOptions
                        {
                            MaxDepth = 64
                        });
                    var message = document.RootElement;
                    if (string.Equals(
                            message.TryGetProperty(
                                "method",
                                out var methodElement)
                                ? methodElement.GetString()
                                : null,
                            "session/update",
                            StringComparison.Ordinal))
                    {
                        PublishChunk(message);
                        continue;
                    }

                    if (!message.TryGetProperty("id", out var idElement)
                        || !idElement.TryGetInt64(out var id)
                        || !_pending.TryRemove(id, out var completion))
                    {
                        continue;
                    }

                    if (message.TryGetProperty("error", out var error))
                    {
                        completion.TrySetException(
                            new InvalidOperationException(
                                ReadAcpError(error)));
                    }
                    else
                    {
                        var result = message.TryGetProperty(
                            "result",
                            out var resultElement)
                            ? resultElement.Clone()
                            : JsonSerializer.SerializeToElement(new { });
                        completion.TrySetResult(result);
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Normal disposal.
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                var exception = failure ?? new EndOfStreamException(
                    "Grok CLI ACP 输出流提前结束。");
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetException(exception);
                }

                _pending.Clear();
                _chunks.Writer.TryComplete(failure);
            }
        }

        private void PublishChunk(JsonElement message)
        {
            if (!message.TryGetProperty("params", out var parameters)
                || !parameters.TryGetProperty("update", out var update)
                || !update.TryGetProperty(
                    "sessionUpdate",
                    out var updateKind)
                || !string.Equals(
                    updateKind.GetString(),
                    "agent_message_chunk",
                    StringComparison.Ordinal)
                || !update.TryGetProperty("content", out var content)
                || !content.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var value = text.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                _chunks.Writer.TryWrite(value);
            }
        }

        private async Task<string> ReadStderrAsync()
        {
            var result = new StringBuilder();
            var buffer = new char[2048];
            while (true)
            {
                var read = await _process.StandardError.ReadAsync(
                    buffer,
                    _lifetime.Token);
                if (read == 0)
                {
                    break;
                }

                var remaining = MaximumStderrCharacters - result.Length;
                if (remaining > 0)
                {
                    result.Append(
                        buffer,
                        0,
                        Math.Min(read, remaining));
                }
            }

            return result.ToString();
        }

        private static string ReadAcpError(JsonElement error)
        {
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(message.GetString()))
            {
                return $"Grok CLI ACP：{message.GetString()}";
            }

            var text = error.GetRawText();
            return text.Length <= 800
                ? $"Grok CLI ACP：{text}"
                : $"Grok CLI ACP：{text[..800]}…";
        }
    }
}
