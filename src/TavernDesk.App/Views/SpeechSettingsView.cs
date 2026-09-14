using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using TavernDesk.App.Localization;
using TavernDesk.App.ViewModels;

namespace TavernDesk.App.Views;

public sealed class SpeechSettingsView : UserControl
{
    private readonly PasswordBox _password = new();
    public SpeechSettingsView()
    {
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Stretch };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(Note("Title", 22));
        panel.Children.Add(Note("ManualOnly"));
        var fields = new StackPanel();
        fields.SetBinding(IsEnabledProperty, new Binding("CanEdit"));
        panel.Children.Add(fields);
        Text(fields, "ApiUrl", "ApiUrl", 2048);
        fields.Children.Add(Note("ApiUrlHint"));
        Label(fields, "ApiKey");
        _password.Padding = new Thickness(6);
        AutomationProperties.SetAutomationId(_password, "SpeechApiKey");
        fields.Children.Add(_password);
        var keyStatus = Note("KeepKey");
        keyStatus.SetBinding(TextBlock.TextProperty, new Binding("KeyStatus"));
        fields.Children.Add(keyStatus);
        fields.Children.Add(Note("KeepKey"));
        Check(fields, "ClearKey", "ClearKey");
        Combo(fields, "Model", "Model", "Models");
        var customModel = new StackPanel();
        customModel.SetBinding(VisibilityProperty, new Binding("IsCustomModel") { Converter = new BooleanToVisibilityConverter() });
        Text(customModel, "CustomModelId", "Model", 128); fields.Children.Add(customModel);
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
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,4) };
        fields.Children.Add(buttons);
        AddButton(buttons, L("Recommended"), "RecommendedCommand", "SpeechRecommended");
        AddButton(buttons, L("Reload"), "ReloadCommand", "SpeechReload");
        AddButton(buttons, LanguageRuntime.GetString("Common.SaveChanges"), "SaveCommand", "SpeechSave");
        var status = Note("ManualOnly"); status.SetBinding(TextBlock.TextProperty, new Binding("Status")); panel.Children.Add(status);
        _password.PasswordChanged += (_, _) => { if (DataContext is SpeechSettingsViewModel vm) vm.PendingApiKey = _password.Password; };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SpeechSettingsViewModel old) old.PropertyChanged -= OnChanged;
            if (e.NewValue is SpeechSettingsViewModel current) current.PropertyChanged += OnChanged;
            SyncPassword();
        };
        Loaded += async (_, _) => { if (DataContext is SpeechSettingsViewModel vm) { vm.PropertyChanged -= OnChanged; vm.PropertyChanged += OnChanged; await vm.LoadAsync(); SyncPassword(); } };
        Unloaded += (_, _) => { if (DataContext is SpeechSettingsViewModel vm) vm.PropertyChanged -= OnChanged; };
    }
    private static void AddButton(Panel panel, string text, string command, string id)
    {
        var button = new Button { Content = text, Margin = new Thickness(5,3,0,3), Padding = new Thickness(14,7,14,7) };
        button.SetBinding(Button.CommandProperty, new Binding(command)); AutomationProperties.SetAutomationId(button, id); panel.Children.Add(button);
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == "PendingApiKey") SyncPassword(); }
    private void SyncPassword()
    {
        var value = (DataContext as SpeechSettingsViewModel)?.PendingApiKey ?? "";
        if (_password.Password != value) _password.Password = value;
    }
    private static string L(string key) => LanguageRuntime.GetString("Speech." + key);
    private static TextBlock Note(string key, double size = 12) => new() { Text = L(key), TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0,5,0,5) };
    private static void Label(Panel panel, string key) => panel.Children.Add(new TextBlock { Text = L(key), Margin = new Thickness(0,12,0,5) });
    private static Binding Edit(string property) => new("Form." + property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
    private static void Text(Panel panel, string key, string property, int limit = 32)
    {
        Label(panel, key); var input = new TextBox { MaxLength = limit };
        input.SetBinding(TextBox.TextProperty, Edit(property)); AutomationProperties.SetAutomationId(input, "Speech" + property); panel.Children.Add(input);
    }
    private static void Combo(Panel panel, string key, string property, string items)
    {
        Label(panel, key); var input = new ComboBox(); input.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(items));
        input.SetBinding(ComboBox.SelectedItemProperty, property == "Model" ? new Binding("ModelSelection") { Mode = BindingMode.TwoWay } : Edit(property));
        AutomationProperties.SetAutomationId(input, "Speech" + property + "Choice"); panel.Children.Add(input);
    }
    private static void Check(Panel panel, string key, string property)
    {
        var input = new CheckBox { Content = Note(key), Margin = new Thickness(0,4,0,0) };
        input.SetBinding(CheckBox.IsCheckedProperty, Edit(property)); AutomationProperties.SetAutomationId(input, "Speech" + property); panel.Children.Add(input);
    }
}
