using System.Windows;
using TavernDesk.App.Localization;
using TavernDesk.App.ViewModels;
using TavernDesk.App.Views;
using TavernDesk.Infrastructure.Speech;

namespace TavernDesk.App;

public sealed class SpeechSettingsDialog : Window
{
    public SpeechSettingsDialog(SpeechSettingsService service, SpeechSettings settings, string characterId, string characterName)
    {
        Title = LanguageRuntime.GetString("Speech.Title"); Width = 620; Height = 740; MinHeight = 480; MinWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var vm = new SpeechSettingsViewModel(service, characterId, characterName);
        vm.SetForm(settings);
        Content = new SpeechSettingsView { DataContext = vm };
        var closed = false;
        vm.Saved += (_, _) => { if (!closed && !vm.HasSaveWarning) DialogResult = true; };
        Closed += (_, _) => { closed = true; vm.PendingApiKey = ""; };
    }
}
