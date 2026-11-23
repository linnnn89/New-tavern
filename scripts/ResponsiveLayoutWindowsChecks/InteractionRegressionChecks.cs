using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TavernDesk.App;
using TavernDesk.App.ViewModels;
using TavernDesk.App.Views;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;

internal static partial class Program
{
    private static async Task CheckGroupMemberFocus()
    {
        var dialog = new GroupChatDialog([new Character { Name = "A" }, new Character { Name = "B" }]);
        dialog.Show();
        await Settle(dialog);
        var title = Named<TextBox>(dialog, "TitleBox");
        var rows = Descendants<ListBoxItem>(dialog).ToArray();
        var first = (Border)rows[0].Template.FindName("RowChrome", rows[0]);
        var second = (Border)rows[1].Template.FindName("RowChrome", rows[1]);
        dialog.Choices[0].IsSelected = true;
        title.Focus();
        title.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        await Settle(dialog);
        var focusBrush = dialog.FindResource("FocusRingBrush");
        Check(rows[0].IsKeyboardFocused && Equals(first.BorderBrush, focusBrush), "Tab shows focus on a checked group member");
        PressKey(rows[0], Key.Down);
        await Settle(dialog);
        Check(rows[1].IsKeyboardFocused && Equals(second.BorderBrush, focusBrush) && !dialog.Choices[1].IsSelected,
            "Down moves the focus ring without checking the next member");
        Check(Equals(first.Background, dialog.FindResource("AccentSoftBrush")) && !Equals(first.BorderBrush, focusBrush),
            "The previous member keeps its checked highlight after focus moves");
        dialog.Close();
    }

    private static async Task CheckAvatarPlaceholders(InfrastructureServices services, CharactersViewModel model)
    {
        var outline = new Border { BorderBrush = Brushes.Red, BorderThickness = new Thickness(1) };
        Layout(outline, 40, 40);
        Capture(outline, "transparent-avatar");
        Character[] characters =
        [
            new() { Id = "wpf-avatar-a", Name = "Aster" },
            new() { Id = "wpf-avatar-b", Name = "Birch", AvatarPath = Path.Combine(_output, "missing-avatar.png") },
            new() { Id = "wpf-avatar-c", Name = "Cedar" }
        ];
        await services.CharacterCards.ReplaceAvatarAsync(characters[0], Path.Combine(_output, "transparent-avatar.png"));
        await services.Characters.UpsertAsync(characters[1]);
        await services.Characters.UpsertAsync(characters[2]);
        var dialog = new GroupChatDialog(characters);
        dialog.Show();
        await Settle(dialog);
        var rows = Descendants<ListBoxItem>(dialog).ToArray();
        for (int i = 0; i < characters.Length; i++)
        {
            CheckAvatar(Find<Image>(rows[i]), hasImage: i == 0, "group member " + characters[i].Name);
        }
        dialog.Close();

        await model.LoadAsync();
        var view = new CharactersView { DataContext = model };
        var window = new Window { Content = view, Width = 1080, Height = 760 };
        window.Show();
        await Settle(window);
        foreach (var character in characters)
        {
            var cover = Descendants<Image>(view).Single(image => image.DataContext is Character card && card.Id == character.Id);
            CheckAvatar(cover, character == characters[0], "character shelf " + character.Name);
        }
        foreach (var character in characters)
        {
            await model.OpenCharacterCardAsync(character);
            await Settle(window);
            CheckAvatar(Named<Image>(view, "AvatarImage"), character == characters[0], "character detail " + character.Name);
        }
        window.Close();
    }

    private static void CheckAvatar(Image image, bool hasImage, string location)
    {
        var grid = (Grid)VisualTreeHelper.GetParent(image);
        var placeholder = grid.Children.OfType<TextBlock>().Single();
        Check(image.IsVisible && (image.Source != null) == hasImage && placeholder.IsVisible == !hasImage,
            $"Placeholder follows the decoded image in {location}");
    }

    private static async Task CheckCampaignShortcuts(InfrastructureServices services, CampaignsViewModel model)
    {
        var scenario = new CampaignScenario { Title = "Keyboard check" };
        await services.CampaignScenarios.UpsertAsync(scenario);
        var campaign = new Campaign { StoryId = scenario.Id, Title = scenario.Title, WorldSetting = "Forest",
            Rules = "Take turns", OpeningPrompt = "At a gate", GmKind = CampaignGmKind.User, MemoryEnabled = false };
        await services.Campaigns.SaveDraftAsync(campaign,
            [new CampaignParticipant { CampaignId = campaign.Id, Kind = CampaignParticipantKind.User, DisplayName = "Player" }]);
        await services.CampaignRunner.StartAsync(campaign.Id);
        var view = new CampaignsView { DataContext = model };
        var window = new Window { Content = view, Width = 1400, Height = 780 };
        window.Show();
        model.SelectedCampaign = new CampaignSummaryItemViewModel((await services.Campaigns.ListAsync()).Single(c => c.Id == campaign.Id));
        model.ContinueCampaignCommand.Execute(null);
        foreach (var (command, kind, text) in new[] {
            (model.SubmitUserActionCommand, CampaignEventKind.PlayerIntent, "Inspect the gate"),
            (model.ResolveRoundCommand, CampaignEventKind.GmResolution, "The gate opens") })
        {
            while (model.IsBusy) await Task.Delay(10);
            await Settle(window);
            var input = Descendants<TextBox>(view).Single(box => box.InputBindings.OfType<KeyBinding>().Any(k => k.Command == command));
            var button = Descendants<Button>(view).Single(b => b.Command == command);
            input.BringIntoView();
            input.Focus();
            var initialStatus = model.StatusText;
            Check(input.IsKeyboardFocused && !button.IsEnabled && !command.CanExecute(null), $"{kind} is disabled for empty input");
            PressKey(input, Key.Enter, control: true);
            await Settle(window);
            var game = await services.Campaigns.GetAsync(campaign.Id);
            Check(model.StatusText == initialStatus && game!.Events.All(e => e.Kind != kind), $"Empty Ctrl+Enter leaves {kind} untouched");
            input.SetCurrentValue(TextBox.TextProperty, text);
            await Settle(window);
            Check(button.IsEnabled && command.CanExecute(null), $"{kind} enables when text is entered");
            PressKey(input, Key.Enter, control: true);
            while (model.IsBusy) await Task.Delay(10);
            await Settle(window);
            game = await services.Campaigns.GetAsync(campaign.Id);
            Check(game!.Events.Count(e => e.Kind == kind && e.Content.Contains(text, StringComparison.Ordinal)) == 1
                && input.Text.Length == 0, $"Ctrl+Enter saves {kind} once and clears the editor");
        }
        window.Close();
    }

    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);

    private static void PressKey(UIElement target, Key key, bool control = false)
    {
        // WPF reads Ctrl from the dispatcher thread's key state when matching a KeyBinding.
        var saved = new byte[256];
        if (!GetKeyboardState(saved)) throw new InvalidOperationException("Cannot read keyboard state");
        var keys = new byte[256];
        if (control) keys[0x11] = keys[0xA2] = 0x80;
        if (!SetKeyboardState(keys)) throw new InvalidOperationException("Cannot set keyboard modifiers");
        try
        {
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, Environment.TickCount, key)
                { RoutedEvent = Keyboard.KeyDownEvent });
        }
        finally { SetKeyboardState(saved); }
    }
}
