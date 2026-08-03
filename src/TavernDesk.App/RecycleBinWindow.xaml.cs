using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App;

public partial class RecycleBinWindow : Window
{
    private readonly IConversationRepository _repository;
    private readonly List<DeletedMessageSummary> _allItems = [];
    private readonly ObservableCollection<DeletedMessageSummary> _visibleItems = [];

    public RecycleBinWindow(IConversationRepository repository)
    {
        _repository = repository;
        InitializeComponent();
        DeletedMessagesGrid.ItemsSource = _visibleItems;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _allItems.Clear();
        _allItems.AddRange(await _repository.ListDeletedMessagesAsync());
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = FilterBox.Text.Trim();
        _visibleItems.Clear();
        foreach (var item in _allItems.Where(item =>
                     query.Length == 0
                     || item.ConversationTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.SenderKind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
                     || item.Content.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            _visibleItems.Add(item);
        }
    }

    private void FilterBox_OnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void DeleteSelected_OnClick(object sender, RoutedEventArgs e)
    {
        if (DeletedMessagesGrid.SelectedItem is not DeletedMessageSummary selected)
        {
            MessageBox.Show(this, "请先选择一条消息。", "回收箱");
            return;
        }

        if (MessageBox.Show(
                this,
                "该消息将被永久删除，无法恢复。确定继续吗？",
                "永久删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _repository.PurgeDeletedMessageAsync(selected.Id);
        await ReloadAsync();
    }

    private async void ClearAll_OnClick(object sender, RoutedEventArgs e)
    {
        if (_allItems.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"将永久删除回收箱中的 {_allItems.Count} 条消息，无法恢复。确定继续吗？",
                "清空回收箱",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await _repository.PurgeAllDeletedMessagesAsync();
        await ReloadAsync();
    }
}
