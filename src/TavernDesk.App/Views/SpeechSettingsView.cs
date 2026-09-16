using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using TavernDesk.App.Localization;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views;

public sealed class SpeechSettingsView : UserControl
{
    private readonly PasswordBox _password = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, List<Control>> _inputs = new();
    private readonly Dictionary<string, TextBlock> _errors = new();
    private readonly Dictionary<Control, string> _helpText = new();
    private SpeechSettingsViewModel? _observedViewModel;

    public SpeechSettingsView()
    {
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Stretch };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _scroll.Content = panel;
        layout.Children.Add(_scroll);
        Content = layout;
        panel.Children.Add(Note("Title", 22));
        panel.Children.Add(Note("ManualOnly"));
        var fields = new StackPanel();
        fields.SetBinding(IsEnabledProperty, new Binding("CanEdit"));
        panel.Children.Add(fields);
        Text(fields, "ApiUrl", "ApiUrl", 2048);
        fields.Children.Add(Note("ApiUrlHint"));
        var keyLabel = Label(fields, "ApiKey");
        _password.Padding = new Thickness(6);
        AutomationProperties.SetAutomationId(_password, "SpeechApiKey");
        RegisterInput(_password, keyLabel, "ApiKey", "KeepKey");
        fields.Children.Add(_password);
        AddError(fields, "ApiKey");
        var keyStatus = Note("KeepKey");
        keyStatus.SetBinding(TextBlock.TextProperty, new Binding("KeyStatus"));
        fields.Children.Add(keyStatus);
        fields.Children.Add(Note("KeepKey"));
        Check(fields, "ClearKey", "ClearKey");
        Combo(fields, "Model", "Model", "Models", false);
        var customModel = new StackPanel();
        customModel.SetBinding(VisibilityProperty, new Binding("IsCustomModel") { Converter = new BooleanToVisibilityConverter() });
        Text(customModel, "CustomModelId", "Model", 128, false); fields.Children.Add(customModel);
        AddError(fields, "Model");
        fields.Children.Add(Note("ModelHint"));
        Text(fields, "DefaultVoice", "DefaultVoiceId", 128);
        var role = new StackPanel();
        role.SetBinding(VisibilityProperty, new Binding("IsCharacterEditor") { Converter = new BooleanToVisibilityConverter() });
        fields.Children.Add(role);
        var roleName = new TextBlock(); roleName.SetBinding(TextBlock.TextProperty, new Binding("CharacterName")); role.Children.Add(roleName);
        Text(role, "CharacterVoice", "CharacterVoiceId", 128);
        fields.Children.Add(Note("VoiceHint"));
        Text(fields, "Speed", "Speed");
        Text(fields, "Volume", "Volume");
        Combo(fields, "Latency", "Latency", "Latencies");
        fields.Children.Add(Note("LatencyHint"));
        var advanced = new StackPanel();
        fields.Children.Add(new Expander { Header = L("Advanced"), Content = advanced, Margin = new Thickness(0,18,0,8) });
        Text(advanced, "Temperature", "Temperature"); Text(advanced, "TopP", "TopP");
        Text(advanced, "ChunkLength", "ChunkLength"); Text(advanced, "MinChunkLength", "MinChunkLength");
        Text(advanced, "MaxNewTokens", "MaxNewTokens"); Text(advanced, "RepetitionPenalty", "RepetitionPenalty");
        Text(advanced, "EarlyStopThreshold", "EarlyStopThreshold");
        Check(advanced, "Normalize", "Normalize"); Check(advanced, "NormalizeLoudness", "NormalizeLoudness");
        Check(advanced, "ConditionOnPreviousChunks", "ConditionOnPreviousChunks"); Check(advanced, "QualityGuard", "QualityGuard");
        fields.Children.Add(Note("FormatHint"));
        var cachePanel = new StackPanel { Margin = new Thickness(0,14,0,0) };
        panel.Children.Add(cachePanel);
        cachePanel.Children.Add(Note("CacheTitle", 16));
        var cachePath = new TextBlock { TextWrapping = TextWrapping.Wrap };
        cachePath.SetBinding(TextBlock.TextProperty, new Binding("CacheDirectory"));
        cachePanel.Children.Add(cachePath);
        var cacheUsage = new TextBlock { Margin = new Thickness(0,5,0,5) };
        cacheUsage.SetBinding(TextBlock.TextProperty, new Binding("CacheUsage"));
        cachePanel.Children.Add(cacheUsage);
        cachePanel.Children.Add(Note("CacheHint"));
        AddButton(cachePanel, L("RefreshCache"), "RefreshCacheCommand", "SpeechRefreshCache");
        var footer = new StackPanel { Margin = new Thickness(20, 0, 20, 12), MaxWidth = 780 };
        Grid.SetRow(footer, 1);
        layout.Children.Add(footer);
        footer.Children.Add(Note("TestHint"));
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,8,0,4) };
        buttons.SetBinding(IsEnabledProperty, new Binding("CanEdit"));
        footer.Children.Add(buttons);
        AddButton(buttons, L("TestConnection"), "TestCommand", "SpeechTestConnection");
        AddButton(buttons, L("Recommended"), "RecommendedCommand", "SpeechRecommended");
        AddButton(buttons, L("Reload"), "ReloadCommand", "SpeechReload");
        AddButton(buttons, LanguageRuntime.GetString("Common.SaveChanges"), "SaveCommand", "SpeechSave");
        var cancelTest = new Button { Content = L("CancelTest"), HorizontalAlignment = HorizontalAlignment.Right };
        cancelTest.SetBinding(Button.CommandProperty, new Binding("CancelTestCommand"));
        cancelTest.SetBinding(VisibilityProperty, new Binding("IsTesting") { Converter = new BooleanToVisibilityConverter() });
        AutomationProperties.SetAutomationId(cancelTest, "SpeechCancelTest");
        footer.Children.Add(cancelTest);
        var status = Note("ManualOnly"); status.SetBinding(TextBlock.TextProperty, new Binding("Status")); footer.Children.Add(status);
        AutomationProperties.SetAutomationId(status, "SpeechStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        _password.PasswordChanged += (_, _) => { if (DataContext is SpeechSettingsViewModel vm) vm.PendingApiKey = _password.Password; };
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded) Observe(e.NewValue as SpeechSettingsViewModel);
            SyncPassword();
            SyncValidation();
        };
        Loaded += async (_, _) =>
        {
            Observe(DataContext as SpeechSettingsViewModel);
            if (DataContext is SpeechSettingsViewModel vm)
            {
                await vm.LoadAsync();
                if (IsLoaded && ReferenceEquals(DataContext, vm)) { SyncPassword(); SyncValidation(); }
            }
        };
        Unloaded += (_, _) => { _observedViewModel?.CancelTest(); Observe(null); };
    }
    private static void AddButton(Panel panel, string text, string command, string id)
    {
        var button = new Button { Content = text, Margin = new Thickness(5,3,0,3), Padding = new Thickness(14,7,14,7) };
        button.SetBinding(Button.CommandProperty, new Binding(command)); AutomationProperties.SetAutomationId(button, id); panel.Children.Add(button);
    }
    private void Observe(SpeechSettingsViewModel? viewModel)
    {
        if (ReferenceEquals(_observedViewModel, viewModel)) return;
        if (_observedViewModel is not null)
        {
            _observedViewModel.CancelTest();
            _observedViewModel.PropertyChanged -= OnChanged;
            _observedViewModel.ValidationFailed -= OnValidationFailed;
        }
        _observedViewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnChanged;
            viewModel.ValidationFailed += OnValidationFailed;
        }
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpeechSettingsViewModel.PendingApiKey)) SyncPassword();
        if (e.PropertyName is nameof(SpeechSettingsViewModel.ValidationField) or nameof(SpeechSettingsViewModel.ValidationMessage) or nameof(SpeechSettingsViewModel.IsCustomModel)) SyncValidation();
    }
    private void SyncValidation()
    {
        var vm = DataContext as SpeechSettingsViewModel;
        foreach (var (field, error) in _errors)
        {
            var message = vm?.ValidationField == field ? vm.ValidationMessage : "";
            error.Text = message;
            error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            foreach (var input in _inputs[field])
            {
                if (string.IsNullOrEmpty(message))
                {
                    input.ClearValue(Control.BorderBrushProperty);
                    input.ClearValue(Control.BorderThicknessProperty);
                }
                else
                {
                    input.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
                    input.BorderThickness = new Thickness(2);
                }
                AutomationProperties.SetHelpText(input, string.IsNullOrEmpty(message) ? _helpText[input] : _helpText[input] + "\n" + message);
            }
        }
    }
    private void OnValidationFailed(object? sender, EventArgs e)
    {
        if (sender is not SpeechSettingsViewModel vm || vm.ValidationField is not { } field) return;
        // Wait for CanEdit bindings to re-enable the field before moving keyboard focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsLoaded || !ReferenceEquals(DataContext, vm) || vm.ValidationField != field || !_inputs.TryGetValue(field, out var inputs)) return;
            var input = field == "Model" && vm.IsCustomModel ? inputs.OfType<TextBox>().First() : inputs[0];
            for (DependencyObject? ancestor = input; ancestor is not null; ancestor = LogicalTreeHelper.GetParent(ancestor))
                if (ancestor is Expander expander) expander.IsExpanded = true;
            UpdateLayout();
            input.BringIntoView();
            input.Focus();
            if (input is TextBox text) text.SelectAll();
            // WPF may scroll again while applying focus. Keep the input and its error together
            // after that work, using the body viewport rather than the fixed footer's bounds.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!IsLoaded || !ReferenceEquals(DataContext, vm) || vm.ValidationField != field || !input.IsKeyboardFocused) return;
                UpdateLayout();
                var bounds = input.TransformToAncestor(_scroll).TransformBounds(new Rect(input.RenderSize));
                var error = _errors[field];
                bounds.Union(error.TransformToAncestor(_scroll).TransformBounds(new Rect(error.RenderSize)));
                const double margin = 8;
                var bottom = _scroll.ViewportHeight - margin;
                var delta = bounds.Height > bottom - margin || bounds.Top < margin ? bounds.Top - margin
                    : bounds.Bottom > bottom ? bounds.Bottom - bottom : 0;
                if (delta != 0) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + delta);
            }));
        }));
    }
    private void SyncPassword()
    {
        var value = (DataContext as SpeechSettingsViewModel)?.PendingApiKey ?? "";
        if (_password.Password != value) _password.Password = value;
    }
    private static string L(string key) => LanguageRuntime.GetString("Speech." + key);
    private static TextBlock Note(string key, double size = 12) => new() { Text = L(key), TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0,5,0,5) };
    private static TextBlock Label(Panel panel, string key)
    {
        var label = new TextBlock { Text = L(key), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,5) };
        panel.Children.Add(label);
        return label;
    }
    private void RegisterInput(Control input, TextBlock label, string property, string? hint = null)
    {
        AutomationProperties.SetName(input, label.Text);
        AutomationProperties.SetLabeledBy(input, label);
        var help = hint is null ? label.Text : L(hint);
        _helpText.Add(input, help);
        AutomationProperties.SetHelpText(input, help);
        if (!_inputs.TryGetValue(property, out var inputs)) _inputs.Add(property, inputs = new List<Control>());
        inputs.Add(input);
    }
    private void AddError(Panel panel, string property)
    {
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        AutomationProperties.SetAutomationId(error, "Speech" + property + "Error");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        _errors.Add(property, error);
        panel.Children.Add(error);
    }
    private static Binding Edit(string property) => new("Form." + property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
    private void Text(Panel panel, string key, string property, int limit = 32, bool addError = true)
    {
        var label = Label(panel, key); var input = new TextBox { MaxLength = limit };
        RegisterInput(input, label, property, property switch { "ApiUrl" => "ApiUrlHint", "Model" => "ModelHint", "DefaultVoiceId" or "CharacterVoiceId" => "VoiceHint", _ => null });
        input.SetBinding(TextBox.TextProperty, Edit(property)); AutomationProperties.SetAutomationId(input, "Speech" + property); panel.Children.Add(input);
        if (addError) AddError(panel, property);
    }
    private void Combo(Panel panel, string key, string property, string items, bool addError = true)
    {
        var label = Label(panel, key); var input = new ComboBox(); input.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(items));
        RegisterInput(input, label, property, property == "Model" ? "ModelHint" : "LatencyHint");
        input.SetBinding(ComboBox.SelectedItemProperty, property == "Model" ? new Binding("ModelSelection") { Mode = BindingMode.TwoWay } : Edit(property));
        AutomationProperties.SetAutomationId(input, "Speech" + property + "Choice"); panel.Children.Add(input);
        if (addError) AddError(panel, property);
    }
    private static void Check(Panel panel, string key, string property)
    {
        var input = new CheckBox { Content = Note(key), Margin = new Thickness(0,4,0,0) };
        AutomationProperties.SetName(input, L(key));
        input.SetBinding(CheckBox.IsCheckedProperty, Edit(property)); AutomationProperties.SetAutomationId(input, "Speech" + property); panel.Children.Add(input);
    }
}
