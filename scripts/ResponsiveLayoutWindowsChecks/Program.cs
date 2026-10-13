using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TavernDesk.App;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;
using TavernDesk.App.Views;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Storage;

internal static class Program
{
    private static int _checks;
    private static string _output = "";
    [STAThread]
    public static int Main(string[] args)
    {
        _output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/responsive-layout");
        Directory.CreateDirectory(_output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var path in new[] { "Localization/Strings.zh-CN.xaml", "Themes/Light.xaml", "Themes/AppicaPrimary.xaml", "Themes/AppicaShell.xaml", "Themes/Tokens.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/TavernDesk.App;component/{path}", UriKind.Relative) });
        app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        app.Resources["InverseBooleanToVisibilityConverter"] = new InverseBooleanToVisibilityConverter();
        app.Resources["AvatarImageConverter"] = new AvatarImageConverter();
        app.Resources["RecentCharactersConverter"] = new RecentCharactersConverter();
        Template<ChatViewModel, ChatView>();
        Template<WorldbookViewModel, WorldbookView>();
        Template<CampaignsViewModel, CampaignsView>();
        Template<ProviderSettingsViewModel, ProviderSettingsView>();
        Template<CharactersViewModel, CharactersView>();
        Template<DashboardViewModel, DashboardView>();
        app.Startup += async (_, _) =>
        {
            try
            {
                CheckMeasureBoundary();
                CheckPersistentPanes();
                await CheckWindows();
                Console.WriteLine($"Windows WPF layout: {_checks} checks passed. Rendered evidence: {_output}");
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static void Template<TModel, TView>() where TView : FrameworkElement
    {
        Application.Current.Resources[new DataTemplateKey(typeof(TModel))] =
            new DataTemplate(typeof(TModel)) { VisualTree = new FrameworkElementFactory(typeof(TView)) };
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Layout(FrameworkElement view, double width, double height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
    }

    private static void CheckMeasureBoundary()
    {
        var viewport = new ResponsiveViewport { Child = new Border { MinWidth = 1800, MinHeight = 1200 } };
        Layout(viewport, 640, 400);
        Check(viewport.AvailableSize == new Size(640, 400), "Child minima must not become available space");
        Check(viewport.DesiredSize.Width <= 640, "Child minima must not enlarge the parent");
        viewport.Child = new ScrollViewer { Content = new Border { Width = 2500, Height = 5000 } };
        Layout(viewport, 640, 400);
        Check(viewport.AvailableSize == new Size(640, 400), "Scroll extent must not become available space");
        var inner = new ResponsiveViewport { Child = new Grid() };
        var scaled = new ResponsiveViewport { LayoutTransform = new ScaleTransform(1.5, 1.5), Child = inner };
        Layout(scaled, 900, 600);
        Check(Math.Abs(inner.AvailableSize.Width - 600) < 1, "LayoutTransform is applied exactly once");
    }

    private static void CheckPersistentPanes()
    {
        var list = new ListBox { ItemsSource = Enumerable.Range(0, 1000), SelectedIndex = 42 };
        var editor = new TextBox { Text = "unsaved 日本語 繁體中文 long English draft" };
        var panes = new ResponsivePanes { First = list, Second = editor, ReferenceWidth = 800, StackedReferenceHeight = 650 };
        Layout(panes, 1000, 800);
        Check(panes.LayoutMode == "Columns", "Wide panes");
        Layout(panes, 600, 800);
        Check(panes.LayoutMode == "Rows", "Tall panes");
        Layout(panes, 600, 400);
        Check(panes.LayoutMode == "Tabs", "Short panes");
        panes.SelectPane(true);
        var parent = VisualTreeHelper.GetParent(editor);
        for (var i = 0; i < 30; i++)
        {
            Layout(panes, 1000, 800);
            Layout(panes, 600, 400);
        }
        Check(ReferenceEquals(parent, VisualTreeHelper.GetParent(editor)), "Panes must not reparent");
        Check(list.SelectedIndex == 42 && editor.Text.StartsWith("unsaved"), "Selection and draft must survive");
        Check(editor.Visibility == Visibility.Visible, "Selected compact pane must survive wide/narrow cycles");
        Check(VirtualizingPanel.GetIsVirtualizing(list), "List virtualization remains enabled");
    }

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindOrNull<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        throw new InvalidOperationException($"Missing {typeof(T).Name}");
    }
    private static T? FindOrNull<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindOrNull<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private static T Named<T>(FrameworkElement view, string name) where T : class =>
        (T)(view.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(view)
            ?? view.FindName(name) ?? throw new InvalidOperationException($"Missing named element {name}"));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static async Task CheckWindows()
    {
        var profile = Path.Combine(_output, "profile");
        var services = new InfrastructureServices(Path.Combine(profile, "data"),
            dataConfiguration: new AppDataConfiguration(Path.Combine(profile, "config"), Path.Combine(profile, "data")));
        await services.InitializeAsync();
        var placement = new WindowPlacementService(services.Settings);
        var interaction = new UserInteractionService(placement);
        var dialogs = new FileDialogService();
        var factory = new ChatViewModelFactory(services, interaction, dialogs);
        var vm = new MainWindowViewModel(services, dialogs, interaction, factory.Create());
        await vm.InitializeAsync();
        await vm.Settings.LoadAsync();
        for (var i = 0; i < 100; i++)
        {
            vm.Worldbooks.Entries.Add(new WorldbookEntry { Id = $"entry-{i}", Title = $"Entry {i}: a long multilingual title 日本語 繁體中文", Content = "Fictional layout sample. Unsaved text stays in the same editor." });
            vm.Campaigns.Scenarios.Add(new CampaignScenario { Title = $"Scenario {i}: The expedition beyond the northern mountains", Summary = "Fictional sample for wrapping action labels and list virtualization." });
        }
        vm.Worldbooks.SelectedEntry = vm.Worldbooks.Entries[0];
        vm.Worldbooks.EntryTitle = "Unsaved worldbook title";
        vm.Campaigns.Campaigns.Add(new CampaignSummaryItemViewModel(new CampaignSummary("sample", "sample", null,
            "An ongoing fictional expedition", CampaignStatus.Active, CampaignPhase.AwaitingActions,
            CampaignFlowPreset.CollaborativeTable, 8, 4, DateTimeOffset.UtcNow)));
        vm.Campaigns.SelectedCampaign = vm.Campaigns.Campaigns[0];
        vm.Campaigns.SelectedScenario = vm.Campaigns.Scenarios[0];
        var main = new MainWindow(vm, placement);
        Application.Current.MainWindow = main;
        // Change only presentation selection; no generation commands or network requests are issued.
        typeof(MainWindowViewModel).GetProperty(nameof(vm.CurrentPage))!.SetValue(vm, vm.Chat);
        main.Show();
        var independent = new ConversationWindow(factory.Create(), placement) { Width = 1500, Height = 850 };
        independent.Show();
        await Settle(main);
        await Settle(independent);
        var chat = Find<ChatView>(main);
        var otherChat = Find<ChatView>(independent);
        var panel = Named<Border>(chat, "RightPanel");
        var column = Named<ColumnDefinition>(chat, "RightPanelColumn");
        var inspector = Named<TabControl>(chat, "InspectorTabs");
        inspector.SelectedIndex = 3;
        vm.Chat.ComposerText = "Unsaved chat input 日本語";
        column.Width = new GridLength(390);
        (Named<GridSplitter>(chat, "RightPanelSplitter")).RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        main.Width = 1080;
        await Settle(main);
        Check(panel.Visibility == Visibility.Collapsed, "Main window automatically collapses");
        Check((Named<Border>(otherChat, "RightPanel")).Visibility == Visibility.Visible, "Independent window uses its own available space");
        Click(Named<Button>(chat, "RightPanelToggleButton"));
        await Settle(main);
        Check((Named<Border>(chat, "RightPanelOverlayHeader")).Visibility == Visibility.Visible, "Compact inspector opens as overlay");
        main.Width = 1600;
        await Settle(main);
        Check(panel.Visibility == Visibility.Visible && Math.Abs(column.Width.Value - 390) < 1, "Overlay docks with the user's width");
        Click(Named<Button>(chat, "RightPanelToggleButton"));
        main.Width = 1080; await Settle(main);
        main.Width = 1600; await Settle(main);
        Check(panel.Visibility == Visibility.Collapsed, "Manual close survives resize");
        Check(inspector.SelectedIndex == 3 && vm.Chat.ComposerText == "Unsaved chat input 日本語", "Inspector selection and composer survive");
        Click(Named<Button>(chat, "RightPanelToggleButton"));
        foreach (var culture in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP" })
        {
            LanguageRuntime.Apply(culture);
            foreach (var (scale, font) in new[] { (100, 14d), (125, 20d), (150, 30d) })
            {
                InterfaceSettingsRuntime.Apply(null, font, true, scale);
                main.Width = 1080; main.Height = 500;
                await Settle(main);
                var space = Named<ResponsiveViewport>(chat, "ChatViewport");
                Check(space.AvailableSize.Width < 1080 / (scale / 100d), "Main viewport accounts for navigation and scale");
                Click(Named<Button>(chat, "RightPanelToggleButton"));
                await Settle(main);
                Check(panel.Width <= space.AvailableSize.Width, "Overlay fits viewport");
                Click(Named<Button>(chat, "RightPanelOverlayCloseButton"));
                Check(panel.Visibility == Visibility.Collapsed, "Overlay closes");
            }
        }
        InterfaceSettingsRuntime.Apply(null, 14, true, 100);
        LanguageRuntime.Apply("en-US");
        foreach (var page in new object[] { vm.Chat, vm.Settings, vm.Worldbooks, vm.Campaigns })
        {
            typeof(MainWindowViewModel).GetProperty(nameof(vm.CurrentPage))!.SetValue(vm, page);
            foreach (var (width, height, suffix) in new[] { (1600d, 900d, "wide"), (1080d, 620d, "narrow") })
            {
                main.Width = width; main.Height = height;
                await Settle(main);
                Capture((FrameworkElement)main.Content, $"{page.GetType().Name}-{suffix}");
                if (FindOrNull<ResponsivePanes>(main) is { } panes)
                {
                    panes.SelectPane(true);
                    await Settle(main);
                    Capture((FrameworkElement)main.Content, $"{page.GetType().Name}-{suffix}-detail");
                }
            }
        }
        independent.Width = 960; independent.Height = 620;
        await Settle(independent);
        Capture((FrameworkElement)independent.Content, "independent-chat-narrow");
        independent.Width = 1500; independent.Height = 850;
        await Settle(independent);
        Capture((FrameworkElement)independent.Content, "independent-chat-wide");
        // MainWindow owns application shutdown; do not close it while assertions are running.
    }

    private static void Capture(FrameworkElement visual, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_output, name + ".png"));
        png.Save(output);
    }
}
