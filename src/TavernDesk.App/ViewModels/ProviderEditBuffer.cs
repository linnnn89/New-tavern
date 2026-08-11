using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class ProviderEditBuffer : ViewModelBase
{
    private bool _loading;
    private bool _isDirty;
    private string _name = string.Empty;
    private ProviderAdapterKind _adapterKind;
    private string _baseUrl = string.Empty;
    private string _requestTimeoutSeconds = "300";
    private bool _isEnabled = true;

    public string ProviderId { get; private set; } = string.Empty;

    public string Name
    {
        get => _name;
        set => SetEditable(ref _name, value);
    }

    public ProviderAdapterKind AdapterKind
    {
        get => _adapterKind;
        private set => SetEditable(ref _adapterKind, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetEditable(ref _baseUrl, value);
    }

    public string RequestTimeoutSeconds
    {
        get => _requestTimeoutSeconds;
        set => SetEditable(ref _requestTimeoutSeconds, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetEditable(ref _isEnabled, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public void Load(ProviderProfile profile)
    {
        _loading = true;
        try
        {
            ProviderId = profile.Id;
            Name = profile.Name;
            AdapterKind = ProviderProfileIds.RequiredAdapterFor(profile.Id);
            BaseUrl = AdapterKind == ProviderAdapterKind.GrokCli
                ? "grok://local"
                : profile.BaseUrl;
            RequestTimeoutSeconds = profile.RequestTimeoutSeconds.ToString("0.###");
            IsEnabled = profile.IsEnabled;
            IsDirty = false;
            OnPropertyChanged(nameof(ProviderId));
        }
        finally
        {
            _loading = false;
        }
    }

    public bool TryApplyTo(ProviderProfile profile, out string error)
    {
        error = string.Empty;
        if (!string.Equals(profile.Id, ProviderId, StringComparison.Ordinal))
        {
            error = LanguageRuntime.GetString("Validation.Provider.Mismatch");
            return false;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            error = LanguageRuntime.GetString("Validation.Provider.NameRequired");
            return false;
        }

        var requiredAdapter = ProviderProfileIds.RequiredAdapterFor(profile.Id);
        if (AdapterKind != requiredAdapter)
        {
            error = profile.Id == ProviderProfileIds.GrokCli
                ? LanguageRuntime.GetString("Validation.Provider.GrokAdapterOnly")
                : LanguageRuntime.GetString("Validation.Provider.OpenAiCompatibleOnly");
            return false;
        }

        var normalizedBaseUrl = BaseUrl.Trim().TrimEnd('/');
        if (AdapterKind == ProviderAdapterKind.GrokCli)
        {
            if (!string.Equals(
                    normalizedBaseUrl,
                    "grok://local",
                    StringComparison.OrdinalIgnoreCase))
            {
                error = LanguageRuntime.GetString("Validation.Provider.GrokEndpointFixed");
                return false;
            }
        }
        else
        {
            if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("http" or "https"))
            {
                error = LanguageRuntime.GetString("Validation.Provider.InvalidUrl");
                return false;
            }

            if (!string.IsNullOrEmpty(baseUri.Query)
                || !string.IsNullOrEmpty(baseUri.Fragment))
            {
                error = LanguageRuntime.GetString("Validation.Provider.UrlQueryNotAllowed");
                return false;
            }

            if (baseUri.AbsolutePath.EndsWith(
                    "/chat",
                    StringComparison.OrdinalIgnoreCase)
                || baseUri.AbsolutePath.EndsWith(
                    "/chat/completions",
                    StringComparison.OrdinalIgnoreCase))
            {
                error = LanguageRuntime.GetString("Validation.Provider.UrlPathHint");
                return false;
            }
        }

        if (!double.TryParse(RequestTimeoutSeconds, out var timeout)
            || timeout is < 1 or > 3600)
        {
            error = LanguageRuntime.GetString("Validation.Provider.TimeoutRange");
            return false;
        }

        profile.Name = Name.Trim();
        profile.AdapterKind = requiredAdapter;
        profile.BaseUrl = normalizedBaseUrl;
        profile.RequestTimeoutSeconds = timeout;
        profile.IsEnabled = IsEnabled;
        profile.UpdatedAt = DateTimeOffset.Now;
        return true;
    }

    public void MarkSaved() => IsDirty = false;
    public void MarkDirty() => IsDirty = true;

    private void SetEditable<T>(ref T field, T value)
    {
        if (SetProperty(ref field, value) && !_loading)
        {
            IsDirty = true;
        }
    }
}
