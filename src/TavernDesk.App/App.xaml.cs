using System.Windows;
using System.Windows.Threading;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;
using TavernDesk.Infrastructure;

namespace TavernDesk.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName =
        @"Local\TavernDesk.App.SingleInstance.v1";
    private SingleInstanceGate? _singleInstanceGate;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ScrollViewerWheelRouter.Register();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            _singleInstanceGate =
                SingleInstanceGate.TryAcquire(SingleInstanceMutexName);
            if (!_singleInstanceGate.IsPrimaryInstance)
            {
                _singleInstanceGate.Dispose();
                _singleInstanceGate = null;
                MessageBox.Show(
                    "TavernDesk 已经在运行。\n\n"
                    + "请使用现有主窗口，或在其中打开独立聊天等子窗口。",
                    "TavernDesk",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var services = new InfrastructureServices(ParseDataRoot(e.Args));
            await services.InitializeAsync();

            var windowPlacement = new WindowPlacementService(services.Settings);
            var interaction = new UserInteractionService(windowPlacement);
            var fileDialog = new FileDialogService();
            var chatViewModels = new ChatViewModelFactory(
                services,
                interaction,
                fileDialog);
            var conversationWindows = new ConversationWindowService(
                chatViewModels,
                windowPlacement);
            chatViewModels.OpenConversationWindow =
                conversationWindows.OpenAsync;
            var viewModel = new MainWindowViewModel(
                services,
                fileDialog,
                interaction,
                chatViewModels.Create());
            chatViewModels.OpenPromptSettings = viewModel.OpenPromptSettingsAsync;
            await viewModel.InitializeAsync();

            var window = new MainWindow(viewModel, windowPlacement);
            await windowPlacement.RestoreAsync(window, "window.main", 1440, 900);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"TavernDesk 启动失败。\n\n{exception.Message}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceGate?.Dispose();
        _singleInstanceGate = null;
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            Application.Current.MainWindow,
            $"操作未完成。\n\n{e.Exception.Message}",
            "TavernDesk",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static string? ParseDataRoot(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--data-root", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException("--data-root 后必须提供数据目录路径。");
            }

            return args[index + 1];
        }

        return null;
    }
}
