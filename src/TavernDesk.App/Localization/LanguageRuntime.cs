using System.Diagnostics;
using System.Globalization;
using System.Windows;

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
    private static ResourceDictionary? _fallbackDictionary;

    public static IReadOnlyList<SupportedLanguage> SupportedLanguages => Languages;

    public static Action<Exception>? ErrorReporter { get; set; }

    public static string CurrentCultureName { get; private set; } = DefaultCultureName;

    public static void Apply(string? cultureName)
    {
        var normalized = NormalizeCultureName(cultureName);
        var culture = CultureInfo.GetCultureInfo(normalized);
        var dictionary = LoadDictionary(normalized);

        var application = Application.Current;
        if (application is not null)
        {
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
        }

        _currentDictionary = dictionary;
        CurrentCultureName = normalized;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (TryGetString(_currentDictionary, key, out var localized)
            || TryGetString(Application.Current?.Resources, key, out localized)
            || TryGetString(GetFallbackDictionary(), key, out localized))
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

        foreach (var diagnostic in normalized)
        {
            Trace.TraceInformation("TavernDesk diagnostic: {0}", diagnostic);
        }

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

        if (normalized == GetString("GroupChat.PauseReasonManual")
            || normalized == GetString("Chat.Group.AutoRelayLimit"))
        {
            return normalized;
        }

        const string userMentionPrefix = "检测到最后一句 @USER / @";
        const string userMentionSuffix = "，已等待用户回复。";
        if (normalized.StartsWith(userMentionPrefix, StringComparison.Ordinal)
            && normalized.EndsWith(userMentionSuffix, StringComparison.Ordinal))
        {
            var personaName = normalized[
                userMentionPrefix.Length..^userMentionSuffix.Length];
            return Format("GroupRelay.Reason.UserMentionFormat", personaName);
        }

        const string memberMentionPrefix = "最后一句指定 @";
        const string memberMentionSuffix = "。";
        if (normalized.StartsWith(memberMentionPrefix, StringComparison.Ordinal)
            && normalized.EndsWith(memberMentionSuffix, StringComparison.Ordinal))
        {
            var memberName = normalized[
                memberMentionPrefix.Length..^memberMentionSuffix.Length];
            return Format("GroupRelay.Reason.MemberMentionFormat", memberName);
        }

        var key = normalized switch
        {
            "群聊没有启用的角色。" => "GroupRelay.Reason.NoEnabledCharacters",
            "使用手动选择的发言角色。" => "GroupRelay.Reason.ManualSelected",
            "手动模式需要先选择下一位发言角色。" => "GroupRelay.Reason.ManualRequired",
            "接力模式要求上一位角色在最后一句 @下一位角色，但没有识别到有效成员。" =>
                "GroupRelay.Reason.MentionRequired",
            "用户消息后使用手动选择的首位发言角色。" =>
                "GroupRelay.Reason.UserManualFirst",
            "用户消息后从群聊首位启用角色开始。" =>
                "GroupRelay.Reason.UserFirstEnabled",
            "按固定成员顺序接力。" => "GroupRelay.Reason.FixedOrder",
            "从启用成员中随机选择下一位。" => "GroupRelay.Reason.Random",
            _ => null
        };
        return key is not null
            ? GetString(key)
            : BackendMessage(normalized, "GroupRelay.Reason.Unknown");
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
                $"{DictionaryPathPrefix}{cultureName}.xaml",
                UriKind.Relative)
        };

    private static ResourceDictionary GetFallbackDictionary() =>
        _fallbackDictionary ??= LoadDictionary(DefaultCultureName);

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
