using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Diagnostics;

public sealed partial class TavernDeskDiagnostics : ITavernDeskDiagnostics
{
    public const long DefaultMaximumErrorLogBytes = 10L * 1024 * 1024;
    public const int DefaultRetainedErrorLogFiles = 10;
    public const long DefaultMaximumApiTestOutputBytes = 500L * 1024 * 1024;
    private const int MaximumVisibleResponseCharacters = 16 * 1024 * 1024;
    private const string ErrorLogFileName = "taverndesk-errors.jsonl";
    private const string LoggedExceptionMarker = "TavernDesk.Diagnostics.Logged";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    private readonly object _errorLogGate = new();
    private readonly SemaphoreSlim _testOutputGate = new(1, 1);
    private readonly HashSet<string> _activeTraceFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SensitiveDataRedactor _redactor;
    private readonly long _maximumErrorLogBytes;
    private readonly int _retainedErrorLogFiles;
    private readonly long _maximumApiTestOutputBytes;
    private bool _apiTestModeEnabled;
    private int _activeApiTestTraces;

    public TavernDeskDiagnostics(
        string? errorLogDirectory = null,
        string? applicationRoot = null,
        long maximumErrorLogBytes = DefaultMaximumErrorLogBytes,
        int retainedErrorLogFiles = DefaultRetainedErrorLogFiles,
        long maximumApiTestOutputBytes = DefaultMaximumApiTestOutputBytes)
    {
        if (maximumErrorLogBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumErrorLogBytes));
        }

        if (retainedErrorLogFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedErrorLogFiles));
        }

        if (maximumApiTestOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumApiTestOutputBytes));
        }

        ApplicationRoot = string.IsNullOrWhiteSpace(applicationRoot)
            ? ResolveApplicationRoot()
            : Path.GetFullPath(applicationRoot);
        ErrorLogDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(errorLogDirectory)
                ? Path.Combine(
                    ResolveLocalApplicationData(),
                    "TavernDesk",
                    "logs")
                : errorLogDirectory);
        ApiTestOutputDirectory = Path.GetFullPath(
            Path.Combine(ApplicationRoot, "tests", "output"));
        EnsureExpectedTestOutputPath();
        _maximumErrorLogBytes = maximumErrorLogBytes;
        _retainedErrorLogFiles = retainedErrorLogFiles;
        _maximumApiTestOutputBytes = maximumApiTestOutputBytes;
        _redactor = new SensitiveDataRedactor(
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (ApplicationRoot, "<APP_ROOT>")
        ]);

        try
        {
            Directory.CreateDirectory(ErrorLogDirectory);
        }
        catch
        {
            // Logging must never prevent the application from starting.
        }
    }

    public string ApplicationRoot { get; }

    public string ErrorLogDirectory { get; }

    public string ApiTestOutputDirectory { get; }

    public bool IsApiTestModeEnabled => Volatile.Read(ref _apiTestModeEnabled);

    public bool HasActiveApiTestTraces =>
        Volatile.Read(ref _activeApiTestTraces) > 0;

    public void LogError(
        string category,
        Exception exception,
        IReadOnlyDictionary<string, object?>? context = null,
        bool includeExceptionMessage = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            lock (exception.Data)
            {
                if (exception.Data.Contains(LoggedExceptionMarker))
                {
                    return;
                }

                exception.Data[LoggedExceptionMarker] = true;
            }

            var record = new JsonObject
            {
                ["timestamp"] = DateTimeOffset.Now.ToString("O"),
                ["level"] = "error",
                ["category"] = _redactor.Redact(category),
                ["application_version"] = ApplicationVersion(),
                ["exception_type"] = exception.GetType().FullName,
                ["message"] = includeExceptionMessage
                    ? _redactor.Redact(exception.Message)
                    : "[OMITTED_FOR_PRIVACY]",
                ["stack_trace"] = _redactor.Redact(exception.StackTrace),
                ["inner_exceptions"] = ReadInnerExceptions(
                    exception,
                    includeExceptionMessage),
                ["context"] = SanitizeContext(context)
            };
            var line = record.ToJsonString(JsonOptions) + Environment.NewLine;
            var bytes = Utf8WithoutBom.GetBytes(line);

            lock (_errorLogGate)
            {
                Directory.CreateDirectory(ErrorLogDirectory);
                var currentPath = Path.Combine(
                    ErrorLogDirectory,
                    ErrorLogFileName);
                if (File.Exists(currentPath)
                    && new FileInfo(currentPath).Length + bytes.Length
                    > _maximumErrorLogBytes)
                {
                    RotateErrorLogs(currentPath);
                }

                using var stream = new FileStream(
                    currentPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
        }
        catch
        {
            // Diagnostics are best-effort and must not create a second failure.
        }
    }

    public async Task SetApiTestModeEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _testOutputGate.WaitAsync(cancellationToken);
        try
        {
            if (!enabled)
            {
                Volatile.Write(ref _apiTestModeEnabled, false);
                return;
            }

            EnsureExpectedTestOutputPath();
            Directory.CreateDirectory(ApiTestOutputDirectory);
            var probePath = Path.Combine(
                ApiTestOutputDirectory,
                $".write-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(
                    probePath,
                    string.Empty,
                    Utf8WithoutBom,
                    cancellationToken);
            }
            finally
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }

            PruneApiTestOutput();
            Volatile.Write(ref _apiTestModeEnabled, true);
        }
        finally
        {
            _testOutputGate.Release();
        }
    }

    public async Task<ApiTestTraceSession> BeginApiTraceAsync(
        ApiTestTraceMetadata metadata,
        object? requestBody,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!IsApiTestModeEnabled)
        {
            return await NullTavernDeskDiagnostics.Instance.BeginApiTraceAsync(
                metadata,
                requestBody,
                cancellationToken);
        }

        await _testOutputGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsApiTestModeEnabled)
            {
                return await NullTavernDeskDiagnostics.Instance.BeginApiTraceAsync(
                    metadata,
                    requestBody,
                    cancellationToken);
            }

            EnsureExpectedTestOutputPath();
            Directory.CreateDirectory(ApiTestOutputDirectory);
            PruneApiTestOutput();
            var traceId = Guid.NewGuid().ToString("N");
            var operationName = SanitizeFileName(metadata.Operation);
            var filePath = Path.Combine(
                ApiTestOutputDirectory,
                $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{operationName}-{traceId}.jsonl");
            var requestRecord = new JsonObject
            {
                ["event"] = "request",
                ["trace_id"] = traceId,
                ["timestamp"] = DateTimeOffset.Now.ToString("O"),
                ["operation"] = metadata.Operation,
                ["provider_id"] = metadata.ProviderId,
                ["provider_name"] = metadata.ProviderName,
                ["model_id"] = metadata.ModelId,
                ["adapter"] = metadata.Adapter,
                ["endpoint"] = _redactor.RedactEndpoint(metadata.Endpoint),
                ["body"] = _redactor.SanitizeObject(requestBody)
            };
            await AppendTraceRecordUnsafeAsync(
                filePath,
                requestRecord,
                cancellationToken);
            _activeTraceFiles.Add(filePath);
            Interlocked.Increment(ref _activeApiTestTraces);
            return new FileApiTestTraceSession(
                this,
                traceId,
                filePath,
                metadata);
        }
        catch (Exception exception)
        {
            LogError(
                "diagnostics.api-test.start",
                exception,
                new Dictionary<string, object?>
                {
                    ["operation"] = metadata.Operation,
                    ["output_directory"] = ApiTestOutputDirectory
                });
            return await NullTavernDeskDiagnostics.Instance.BeginApiTraceAsync(
                metadata,
                requestBody,
                CancellationToken.None);
        }
        finally
        {
            _testOutputGate.Release();
        }
    }

    public async Task<ApiTestOutputSummary> GetApiTestOutputSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        await _testOutputGate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(ApiTestOutputDirectory))
            {
                return new ApiTestOutputSummary(0, 0);
            }

            var files = Directory.EnumerateFiles(
                    ApiTestOutputDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .ToArray();
            return new ApiTestOutputSummary(
                files.Length,
                files.Sum(file => file.Length));
        }
        finally
        {
            _testOutputGate.Release();
        }
    }

    public async Task<int> ClearApiTestOutputAsync(
        CancellationToken cancellationToken = default)
    {
        await _testOutputGate.WaitAsync(cancellationToken);
        try
        {
            if (HasActiveApiTestTraces)
            {
                throw new ApiTestOutputBusyException();
            }

            EnsureExpectedTestOutputPath();
            Directory.CreateDirectory(ApiTestOutputDirectory);
            var deletedEntries = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         ApiTestOutputDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDirectChildOfTestOutput(entry);
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(
                        entry,
                        recursive: !attributes.HasFlag(FileAttributes.ReparsePoint));
                }
                else
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                    File.Delete(entry);
                }

                deletedEntries++;
            }

            return deletedEntries;
        }
        finally
        {
            _testOutputGate.Release();
        }
    }

    public static string ResolveApplicationRoot(string? startDirectory = null)
    {
        var initial = Path.GetFullPath(
            string.IsNullOrWhiteSpace(startDirectory)
                ? AppContext.BaseDirectory
                : startDirectory);
        var current = new DirectoryInfo(initial);
        for (var depth = 0; current is not null && depth < 12; depth++)
        {
            if (File.Exists(Path.Combine(current.FullName, "TavernDesk.sln")))
            {
                return current.FullName;
            }

            if (File.Exists(Path.Combine(current.FullName, "TavernDesk.exe"))
                && Directory.Exists(Path.Combine(current.FullName, "app")))
            {
                return current.FullName;
            }

            if (string.Equals(
                    current.Name,
                    "app",
                    StringComparison.OrdinalIgnoreCase)
                && current.Parent is { } parent
                && File.Exists(Path.Combine(parent.FullName, "TavernDesk.exe")))
            {
                return parent.FullName;
            }

            current = current.Parent;
        }

        var initialDirectory = new DirectoryInfo(initial);
        return string.Equals(
                   initialDirectory.Name,
                   "app",
                   StringComparison.OrdinalIgnoreCase)
               && initialDirectory.Parent is not null
            ? initialDirectory.Parent.FullName
            : initialDirectory.FullName;
    }

    private JsonArray ReadInnerExceptions(
        Exception exception,
        bool includeExceptionMessage)
    {
        var result = new JsonArray();
        for (var current = exception.InnerException;
             current is not null;
             current = current.InnerException)
        {
            result.Add(new JsonObject
            {
                ["exception_type"] = current.GetType().FullName,
                ["message"] = includeExceptionMessage
                    ? _redactor.Redact(current.Message)
                    : "[OMITTED_FOR_PRIVACY]"
            });
        }

        return result;
    }

    private JsonObject SanitizeContext(
        IReadOnlyDictionary<string, object?>? context)
    {
        var result = new JsonObject();
        if (context is null)
        {
            return result;
        }

        foreach (var pair in context)
        {
            result[pair.Key] = SensitiveDataRedactor.IsSensitiveName(pair.Key)
                ? "[REDACTED]"
                : _redactor.SanitizeObject(pair.Value);
        }

        return result;
    }

    private void RotateErrorLogs(string currentPath)
    {
        var maximumArchiveCount = _retainedErrorLogFiles - 1;
        if (maximumArchiveCount == 0)
        {
            File.Delete(currentPath);
            return;
        }

        for (var index = maximumArchiveCount; index >= 1; index--)
        {
            var destination = ErrorLogArchivePath(index);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            var source = index == 1
                ? currentPath
                : ErrorLogArchivePath(index - 1);
            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
        }
    }

    private string ErrorLogArchivePath(int index) =>
        Path.Combine(
            ErrorLogDirectory,
            $"taverndesk-errors.{index}.jsonl");

    private async Task CompleteTraceAsync(
        FileApiTestTraceSession session,
        string status,
        object? responseBody,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        if (!session.TryBeginCompletion())
        {
            return;
        }

        await _testOutputGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = session.ReadSnapshot();
            var record = new JsonObject
            {
                ["event"] = "response",
                ["trace_id"] = session.TraceId,
                ["timestamp"] = DateTimeOffset.Now.ToString("O"),
                ["status"] = status,
                ["duration_ms"] = snapshot.DurationMilliseconds,
                ["first_response_ms"] = snapshot.FirstResponseMilliseconds,
                ["visible_response"] = _redactor.Redact(snapshot.VisibleResponse),
                ["visible_response_characters"] = snapshot.VisibleResponseCharacters,
                ["visible_response_truncated"] = snapshot.VisibleResponseTruncated,
                ["reasoning_content_omitted"] = snapshot.ReasoningObserved,
                ["finish_reason"] = snapshot.FinishReason,
                ["usage"] = _redactor.SanitizeObject(snapshot.Usage),
                ["body"] = _redactor.SanitizeObject(responseBody),
                ["error"] = exception is null
                    ? null
                    : new JsonObject
                    {
                        ["exception_type"] = exception.GetType().FullName,
                        ["message"] = _redactor.Redact(exception.Message),
                        ["http_status"] = exception is HttpRequestException http
                            ? (int?)http.StatusCode
                            : null
                    }
            };
            await AppendTraceRecordUnsafeAsync(
                session.FilePath,
                record,
                cancellationToken);
        }
        catch (Exception writeException)
        {
            LogError(
                "diagnostics.api-test.write",
                writeException,
                new Dictionary<string, object?>
                {
                    ["operation"] = session.Metadata.Operation,
                    ["output_file"] = session.FilePath
                });
        }
        finally
        {
            _activeTraceFiles.Remove(session.FilePath);
            Interlocked.Decrement(ref _activeApiTestTraces);
            try
            {
                PruneApiTestOutput();
            }
            catch (Exception pruneException)
            {
                LogError("diagnostics.api-test.prune", pruneException);
            }

            _testOutputGate.Release();
        }
    }

    private async Task AppendTraceRecordUnsafeAsync(
        string filePath,
        JsonObject record,
        CancellationToken cancellationToken)
    {
        var line = record.ToJsonString(JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(
            filePath,
            line,
            Utf8WithoutBom,
            cancellationToken);
    }

    private void PruneApiTestOutput()
    {
        if (!Directory.Exists(ApiTestOutputDirectory))
        {
            return;
        }

        var files = Directory.EnumerateFiles(
                ApiTestOutputDirectory,
                "*.jsonl",
                SearchOption.TopDirectoryOnly)
            .Where(path => !_activeTraceFiles.Contains(path))
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var totalBytes = Directory.EnumerateFiles(
                ApiTestOutputDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).Length)
            .Sum();
        foreach (var file in files)
        {
            if (totalBytes <= _maximumApiTestOutputBytes)
            {
                break;
            }

            totalBytes -= file.Length;
            file.Delete();
        }
    }

    private void EnsureExpectedTestOutputPath()
    {
        var expected = Path.GetFullPath(
            Path.Combine(ApplicationRoot, "tests", "output"));
        if (!string.Equals(
                expected.TrimEnd(Path.DirectorySeparatorChar),
                ApiTestOutputDirectory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The API test output path is outside the expected application-relative location.");
        }
    }

    private void EnsureDirectChildOfTestOutput(string entry)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(entry));
        if (!string.Equals(
                parent?.TrimEnd(Path.DirectorySeparatorChar),
                ApiTestOutputDirectory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to clear an entry outside the API test output directory.");
        }
    }

    private static string ResolveLocalApplicationData()
    {
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local)
            ? AppContext.BaseDirectory
            : local;
    }

    private static string ApplicationVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value
            .Trim()
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray());
        result = Regex.Replace(result, @"\s+", "-");
        return result.Length == 0 ? "api" : result[..Math.Min(result.Length, 60)];
    }

    private sealed class FileApiTestTraceSession : ApiTestTraceSession
    {
        private readonly TavernDeskDiagnostics _owner;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly StringBuilder _visibleResponse = new();
        private readonly object _stateGate = new();
        private int _completionStarted;
        private long? _firstResponseMilliseconds;
        private long _visibleResponseCharacters;
        private bool _visibleResponseTruncated;
        private bool _reasoningObserved;
        private string? _finishReason;
        private ProviderTokenUsage? _usage;

        public FileApiTestTraceSession(
            TavernDeskDiagnostics owner,
            string traceId,
            string filePath,
            ApiTestTraceMetadata metadata)
        {
            _owner = owner;
            TraceId = traceId;
            FilePath = filePath;
            Metadata = metadata;
        }

        public string TraceId { get; }

        public string FilePath { get; }

        public ApiTestTraceMetadata Metadata { get; }

        public override void Observe(ProviderStreamEvent streamEvent)
        {
            lock (_stateGate)
            {
                if (streamEvent.Kind == ProviderStreamEventKind.Reasoning)
                {
                    _reasoningObserved = true;
                    return;
                }

                if (streamEvent.Kind == ProviderStreamEventKind.Completed)
                {
                    _finishReason = streamEvent.FinishReason;
                    _usage = streamEvent.Usage;
                    return;
                }

                if (streamEvent.Content.Length == 0)
                {
                    return;
                }

                _firstResponseMilliseconds ??= _stopwatch.ElapsedMilliseconds;
                _visibleResponseCharacters += streamEvent.Content.Length;
                var remaining = MaximumVisibleResponseCharacters
                                - _visibleResponse.Length;
                if (remaining <= 0)
                {
                    _visibleResponseTruncated = true;
                    return;
                }

                _visibleResponse.Append(
                    streamEvent.Content,
                    0,
                    Math.Min(remaining, streamEvent.Content.Length));
                _visibleResponseTruncated |= streamEvent.Content.Length > remaining;
            }
        }

        public override Task CompleteAsync(
            object? responseBody = null,
            CancellationToken cancellationToken = default) =>
            _owner.CompleteTraceAsync(
                this,
                "completed",
                responseBody,
                exception: null,
                cancellationToken);

        public override Task FailAsync(
            Exception exception,
            CancellationToken cancellationToken = default) =>
            _owner.CompleteTraceAsync(
                this,
                exception is OperationCanceledException
                    ? "cancelled"
                    : "failed",
                responseBody: null,
                exception,
                cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _completionStarted) == 0)
            {
                await _owner.CompleteTraceAsync(
                    this,
                    "interrupted",
                    responseBody: null,
                    exception: null,
                    CancellationToken.None);
            }
        }

        public bool TryBeginCompletion() =>
            Interlocked.CompareExchange(ref _completionStarted, 1, 0) == 0;

        public TraceSnapshot ReadSnapshot()
        {
            lock (_stateGate)
            {
                return new TraceSnapshot(
                    _stopwatch.ElapsedMilliseconds,
                    _firstResponseMilliseconds,
                    _visibleResponse.ToString(),
                    _visibleResponseCharacters,
                    _visibleResponseTruncated,
                    _reasoningObserved,
                    _finishReason,
                    _usage);
            }
        }
    }

    private sealed record TraceSnapshot(
        long DurationMilliseconds,
        long? FirstResponseMilliseconds,
        string VisibleResponse,
        long VisibleResponseCharacters,
        bool VisibleResponseTruncated,
        bool ReasoningObserved,
        string? FinishReason,
        ProviderTokenUsage? Usage);

    private sealed partial class SensitiveDataRedactor
    {
        private readonly IReadOnlyList<(string Value, string Replacement)> _paths;

        public SensitiveDataRedactor(
            IEnumerable<(string Value, string Replacement)> paths)
        {
            _paths = paths
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .OrderByDescending(item => item.Value.Length)
                .ToArray();
        }

        public string? Redact(string? value)
        {
            if (value is null)
            {
                return null;
            }

            var result = value;
            foreach (var path in _paths)
            {
                result = result.Replace(
                    path.Value,
                    path.Replacement,
                    StringComparison.OrdinalIgnoreCase);
            }

            result = AuthorizationRegex().Replace(
                result,
                "$1[REDACTED]");
            result = NamedSecretRegex().Replace(
                result,
                "$1$2[REDACTED]");
            result = QuerySecretRegex().Replace(
                result,
                "$1[REDACTED]");
            result = KnownTokenRegex().Replace(
                result,
                "[REDACTED]");
            result = WindowsAbsolutePathRegex().Replace(
                result,
                "[REDACTED_PATH]");
            return result;
        }

        public string RedactEndpoint(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                return Redact(endpoint) ?? string.Empty;
            }

            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };
            return Redact(builder.Uri.ToString()) ?? string.Empty;
        }

        public JsonNode? SanitizeObject(object? value)
        {
            if (value is null)
            {
                return null;
            }

            JsonNode? node;
            try
            {
                node = JsonSerializer.SerializeToNode(value, JsonOptions);
            }
            catch
            {
                return JsonValue.Create(Redact(value.ToString()));
            }

            return SanitizeNode(node);
        }

        public static bool IsSensitiveName(string name)
        {
            var normalized = new string(name
                .Where(char.IsLetterOrDigit)
                .ToArray())
                .ToLowerInvariant();
            return normalized.Contains("authorization", StringComparison.Ordinal)
                   || normalized.Contains("apikey", StringComparison.Ordinal)
                   || normalized.Contains("accesstoken", StringComparison.Ordinal)
                   || normalized.Contains("refreshtoken", StringComparison.Ordinal)
                   || normalized.Contains("password", StringComparison.Ordinal)
                   || normalized.Contains("secret", StringComparison.Ordinal)
                   || normalized.Contains("cookie", StringComparison.Ordinal);
        }

        private JsonNode? SanitizeNode(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    foreach (var property in jsonObject.ToArray())
                    {
                        if (IsSensitiveName(property.Key))
                        {
                            jsonObject[property.Key] =
                                JsonValue.Create("[REDACTED]");
                            continue;
                        }

                        var sanitized = SanitizeNode(property.Value);
                        if (!ReferenceEquals(sanitized, property.Value))
                        {
                            jsonObject[property.Key] = sanitized;
                        }
                    }

                    return jsonObject;

                case JsonArray jsonArray:
                    for (var index = 0; index < jsonArray.Count; index++)
                    {
                        var existing = jsonArray[index];
                        var sanitized = SanitizeNode(existing);
                        if (!ReferenceEquals(sanitized, existing))
                        {
                            jsonArray[index] = sanitized;
                        }
                    }

                    return jsonArray;

                case JsonValue jsonValue
                    when jsonValue.TryGetValue<string>(out var text):
                    return JsonValue.Create(Redact(text));

                default:
                    return node;
            }
        }

        [GeneratedRegex(
            @"(?i)(authorization\s*[:=]\s*(?:bearer|basic)\s+)[^\s,;]+",
            RegexOptions.CultureInvariant)]
        private static partial Regex AuthorizationRegex();

        [GeneratedRegex(
            """(?i)\b(api[\s_-]?key|access[\s_-]?token|refresh[\s_-]?token|password|secret|cookie)(\s*[:=]\s*)["']?[^"'\s,;&]+""",
            RegexOptions.CultureInvariant)]
        private static partial Regex NamedSecretRegex();

        [GeneratedRegex(
            @"(?i)([?&](?:api[_-]?key|key|token|access[_-]?token|password|secret|signature|sig)=)[^&#\s]+",
            RegexOptions.CultureInvariant)]
        private static partial Regex QuerySecretRegex();

        [GeneratedRegex(
            @"(?i)\b(?:sk-|gsk_|xox[baprs]-|gh[pousr]_)[A-Za-z0-9_\-]{8,}",
            RegexOptions.CultureInvariant)]
        private static partial Regex KnownTokenRegex();

        [GeneratedRegex(
            """(?i)(?<![A-Za-z0-9])(?:[A-Za-z]:\\|\\\\)[^\"'\r\n\t<>|?*]+""",
            RegexOptions.CultureInvariant)]
        private static partial Regex WindowsAbsolutePathRegex();
    }
}
