using System.ComponentModel;
using System.Windows;
using TavernDesk.App.Localization;
using TavernDesk.App.ViewModels;
using TavernDesk.App.Views;
using TavernDesk.Infrastructure.Speech;

namespace TavernDesk.App;

public sealed class SpeechSettingsDialog : Window
{
    private readonly SpeechSettingsViewModel _viewModel;
    private bool _closeConfirmed;
    private bool _confirmingClose;
    private bool _closed;

    public SpeechSettingsDialog(SpeechSettingsService service, SpeechSettings settings, string characterId, string characterName)
    {
        Title = LanguageRuntime.GetString("Speech.Title"); Width = 620; Height = 740; MinHeight = 480; MinWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        _viewModel = new SpeechSettingsViewModel(service, characterId, characterName);
        _viewModel.SetForm(settings);
        Content = new SpeechSettingsView { DataContext = _viewModel };
        _viewModel.Saved += (_, _) =>
        {
            if (_closed || _viewModel.HasSaveWarning) return;
            // Saved is raised before IsSaving resets. Only a committed save may bypass the close guard.
            _closeConfirmed = true;
            DialogResult = true;
        };
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _viewModel.PendingApiKey = ""; };
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;
        if (_viewModel.IsSaving || _confirmingClose)
        {
            e.Cancel = true;
            return;
        }
        if (!_viewModel.HasUnsavedChanges) return;
        e.Cancel = true;
        _confirmingClose = true;
        // Finish the original Closing event before opening a modal prompt or closing after save.
        Dispatcher.BeginInvoke(async () => await ConfirmCloseAsync());
    }

    private async Task ConfirmCloseAsync()
    {
        try
        {
            if (_closed) return;
            var result = LocalizedMessageBox.Show(this,
                LanguageRuntime.GetString("Speech.UnsavedMessage"),
                LanguageRuntime.GetString("Speech.UnsavedTitle"),
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (_closed) return;
            if (result == MessageBoxResult.Yes)
                await _viewModel.SaveAsync();
            else if (result == MessageBoxResult.No)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally { _confirmingClose = false; }
    }
}
