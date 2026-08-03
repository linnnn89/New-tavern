using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TavernDesk.App.ViewModels;
using TavernDesk.Core.Models;

namespace TavernDesk.App.Views;

public partial class ProviderSettingsView : UserControl
{
    private bool _syncingProviderSelection;
    private bool _providerSwitchInProgress;

    public ProviderSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ProviderSettingsViewModel previous)
        {
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is ProviderSettingsViewModel current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
            SyncPasswordBox(current);
            SyncProviderSelection(current);
        }
    }

    private void ApiKeyBox_OnPasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (DataContext is ProviderSettingsViewModel viewModel
            && !string.Equals(
                viewModel.PendingApiKey,
                ApiKeyBox.Password,
                StringComparison.Ordinal))
        {
            viewModel.PendingApiKey = ApiKeyBox.Password;
        }
    }

    private async void ProviderList_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingProviderSelection
            || ProviderList.SelectedItem is not ProviderProfile profile
            || DataContext is not ProviderSettingsViewModel viewModel)
        {
            return;
        }

        if (_providerSwitchInProgress)
        {
            SyncProviderSelection(viewModel);
            return;
        }

        _providerSwitchInProgress = true;
        try
        {
            await viewModel.SelectProfileAsync(profile);
        }
        finally
        {
            _providerSwitchInProgress = false;
            SyncProviderSelection(viewModel);
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProviderSettingsViewModel.PendingApiKey)
            && sender is ProviderSettingsViewModel viewModel)
        {
            SyncPasswordBox(viewModel);
        }

        if (e.PropertyName == nameof(ProviderSettingsViewModel.SelectedProfile)
            && sender is ProviderSettingsViewModel selectedViewModel)
        {
            SyncProviderSelection(selectedViewModel);
        }
    }

    private void SyncPasswordBox(ProviderSettingsViewModel viewModel)
    {
        if (!string.Equals(
                ApiKeyBox.Password,
                viewModel.PendingApiKey,
                StringComparison.Ordinal))
        {
            ApiKeyBox.Password = viewModel.PendingApiKey;
        }
    }

    private void SyncProviderSelection(ProviderSettingsViewModel viewModel)
    {
        _syncingProviderSelection = true;
        try
        {
            ProviderList.SelectedItem = viewModel.SelectedProfile;
        }
        finally
        {
            _syncingProviderSelection = false;
        }
    }
}
