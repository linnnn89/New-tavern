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
        set => SetEditable(ref _adapterKind, value);
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
            AdapterKind = profile.AdapterKind;
            BaseUrl = profile.BaseUrl;
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
            error = "编辑缓冲区与接入商不匹配。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "接入商名称不能为空。";
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
                error = "Grok CLI 本机后端地址固定为 grok://local，不执行任意程序路径。";
                return false;
            }
        }
        else if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var baseUri)
                 || baseUri.Scheme is not ("http" or "https"))
        {
            error = "API 地址必须是完整的 http 或 https 地址。";
            return false;
        }

        if (!double.TryParse(RequestTimeoutSeconds, out var timeout)
            || timeout is < 1 or > 3600)
        {
            error = "等待上限必须在 1–3600 秒之间。";
            return false;
        }

        profile.Name = Name.Trim();
        profile.AdapterKind = AdapterKind;
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
