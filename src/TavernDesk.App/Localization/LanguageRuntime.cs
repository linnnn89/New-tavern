using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;

namespace TavernDesk.App.Localization;

public static class LanguageRuntime
{
    public const string SettingKey = "ui.language";
    public const string DefaultCultureName = "zh-CN";

    private const string DictionaryPathPrefix = "Localization/Strings.";

    private static readonly SupportedLanguage[] Languages =
    [
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁體中文"),
        new("en-US", "English"),
        new("ja-JP", "日本語")
    ];

    private static ResourceDictionary? _currentDictionary;
    private static readonly ConcurrentDictionary<
        string,
        IReadOnlyDictionary<string, string>> PlainDictionaries = new(
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SupportedLanguage> SupportedLanguages => Languages;

    public static Action<Exception>? ErrorReporter { get; set; }

    public static string CurrentCultureName { get; private set; } = DefaultCultureName;

    public static void Apply(string? cultureName)
    {
        var normalized = NormalizeCultureName(cultureName);
        var culture = CultureInfo.GetCultureInfo(normalized);
        var application = Application.Current;
        if (application is not null)
        {
            var dictionary = LoadDictionary(normalized);
            var merged = application.Resources.MergedDictionaries;
            var existingIndex = FindLanguageDictionaryIndex(merged);
            if (existingIndex >= 0)
            {
                merged[existingIndex] = dictionary;
            }
            else
            {
                merged.Insert(0, dictionary);
            }

            _currentDictionary = dictionary;
        }
        else
        {
            _currentDictionary = null;
        }

        CurrentCultureName = normalized;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (TryGetString(_currentDictionary, key, out var localized)
            || TryGetString(Application.Current?.Resources, key, out localized)
            || TryGetPlainString(CurrentCultureName, key, out localized)
            || TryGetPlainString(DefaultCultureName, key, out localized))
        {
            return localized;
        }

        return $"⟦{key}⟧";
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentUICulture, GetString(key), arguments);

    public static string ErrorMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            ErrorReporter?.Invoke(exception);
        }
        catch
        {
            // Diagnostics must never replace the original user-facing error.
        }

        for (var candidate = exception;
             candidate is not null;
             candidate = candidate.InnerException)
        {
            var localizedMessage = candidate.Message.Trim();
            if (localizedMessage.Length > 0 && IsAppException(candidate))
            {
                return localizedMessage;
            }
        }

        var rootException = exception.GetBaseException();
        var message = rootException.Message.Trim();
        if (message.Length > 0 && IsCompatibleWithCurrentLanguage(message))
        {
            return message;
        }

        return LanguageRuntime.Format(
            "Common.InternalErrorFormat",
            rootException.GetType().Name);
    }

    public static string BackendMessage(string? message, string fallbackKey)
    {
        var normalized = message?.Trim() ?? string.Empty;
        return normalized.Length > 0 && IsCompatibleWithCurrentLanguage(normalized)
            ? normalized
            : GetString(fallbackKey);
    }

    public static IReadOnlyList<string> LocalizeDiagnostics(
        IReadOnlyCollection<string>? diagnostics,
        string summaryKey)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return [];
        }

        var normalized = diagnostics
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
            .Select(diagnostic => diagnostic.Trim())
            .ToArray();
        if (normalized.Length == 0)
        {
            return [];
        }

        Trace.TraceInformation(
            "TavernDesk produced {0} backend diagnostic item(s).",
            normalized.Length);

        return normalized.All(IsCompatibleWithCurrentLanguage)
            ? normalized
            : [Format(summaryKey, normalized.Length)];
    }

    public static string GroupRelayReason(string? reason)
    {
        var normalized = reason?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return GetString("GroupRelay.Reason.Unknown");
        }

        var currentKey = normalized switch
        {
            "group-force-selected" => "GroupRelay.Reason.ForceSelected",
            "group-no-enabled" => "GroupRelay.Reason.NoEnabledCharacters",
            "group-fixed-order" => "GroupRelay.Reason.FixedOrder",
            _ => null
        };
        if (currentKey is not null)
        {
            return GetString(currentKey);
        }

        if (normalized == GetString("GroupChat.PauseReasonManual")
            || normalized == GetString("Chat.Group.AutoRelayLimit")
            || normalized == GetString("Chat.Group.InvalidReply"))
        {
            return normalized;
        }

        return BackendMessage(normalized, "GroupRelay.Reason.Unknown");
    }

    public static string NormalizeCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return DefaultCultureName;
        }

        var requested = cultureName.Trim();
        if (requested.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || requested.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-TW";
        }

        if (requested.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        if (requested.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        if (requested.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "ja-JP";
        }

        return DefaultCultureName;
    }

    public static SupportedLanguage Resolve(string? cultureName)
    {
        var normalized = NormalizeCultureName(cultureName);
        return Languages.Single(language => language.CultureName == normalized);
    }

    private static ResourceDictionary LoadDictionary(string cultureName) =>
        new()
        {
            Source = new Uri(
                $"pack://application:,,,/TavernDesk.App;component/{DictionaryPathPrefix}{cultureName}.xaml",
                UriKind.Absolute)
        };

    private static bool TryGetPlainString(
        string cultureName,
        string key,
        out string value)
    {
        var dictionary = PlainDictionaries.GetOrAdd(
            cultureName,
            LoadPlainDictionary);
        return dictionary.TryGetValue(key, out value!);
    }

    private static IReadOnlyDictionary<string, string> LoadPlainDictionary(
        string cultureName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Localization",
            $"Strings.{cultureName}.xaml");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var keyNamespace = XNamespace.Get(
            "http://schemas.microsoft.com/winfx/2006/xaml");
        return XDocument.Load(path, LoadOptions.PreserveWhitespace)
            .Descendants()
            .Where(element => element.Name.LocalName == "String")
            .Select(element => new
            {
                Key = (string?)element.Attribute(keyNamespace + "Key"),
                Value = element.Value
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(
                item => item.Key!,
                item => item.Value,
                StringComparer.Ordinal);
    }

    private static int FindLanguageDictionaryIndex(
        ICollection<ResourceDictionary> dictionaries)
    {
        var index = 0;
        foreach (var dictionary in dictionaries)
        {
            var source = dictionary.Source?.OriginalString;
            if (!string.IsNullOrWhiteSpace(source)
                && source.Contains(DictionaryPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static bool TryGetString(
        ResourceDictionary? dictionary,
        string key,
        out string value)
    {
        if (dictionary?.Contains(key) == true
            && dictionary[key] is string text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsAppException(Exception exception) =>
        exception.TargetSite?.DeclaringType?.Assembly == typeof(LanguageRuntime).Assembly;

    private static bool IsCompatibleWithCurrentLanguage(string message)
    {
        var containsHan = message.Any(character =>
            character is >= '\u3400' and <= '\u4DBF'
                or >= '\u4E00' and <= '\u9FFF');
        var containsKana = message.Any(character =>
            character is >= '\u3040' and <= '\u30FF');

        return CurrentCultureName switch
        {
            "en-US" => !containsHan && !containsKana,
            "ja-JP" => !containsHan || containsKana,
            "zh-TW" => !containsHan,
            _ => true
        };
    }
}
