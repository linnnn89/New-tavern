using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Diagnostics;

namespace TavernDesk.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName =
        @"Local\TavernDesk.App.SingleInstance.v1";
    private const string FirstRunLanguagePendingFileName =
        ".first-run-language.pending";
    private static readonly TimeSpan UnhandledExceptionDuplicateWindow =
        TimeSpan.FromSeconds(3);
    private static string? _lastReportedUnhandledExceptionSignature;
    private static DateTimeOffset _lastReportedUnhandledExceptionAt;
    private static bool _isShowingUnhandledException;
    private SingleInstanceGate? _singleInstanceGate;
    private readonly ITavernDeskDiagnostics _diagnostics =
        new TavernDeskDiagnostics();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LanguageRuntime.Apply(System.Globalization.CultureInfo.CurrentUICulture.Name);
        ScrollViewerWheelRouter.Register();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnNonUiUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        LanguageRuntime.ErrorReporter = exception =>
            _diagnostics.LogError("ui.operation", exception);

        try
        {
            _singleInstanceGate =
                SingleInstanceGate.TryAcquire(SingleInstanceMutexName);
            if (!_singleInstanceGate.IsPrimaryInstance)
            {
                _singleInstanceGate.Dispose();
                _singleInstanceGate = null;
                LocalizedMessageBox.Show(
                    LanguageRuntime.GetString("Startup.AlreadyRunning"),
                    "TavernDesk",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var services = new InfrastructureServices(
                ParseDataRoot(e.Args),
                _diagnostics);
            var databaseExistedAtStartup = File.Exists(services.Paths.DatabasePath);
            var pendingLanguagePath = Path.Combine(
                services.Paths.RootDirectory,
                FirstRunLanguagePendingFileName);
            if (!databaseExistedAtStartup)
            {
                Directory.CreateDirectory(services.Paths.RootDirectory);
                await File.WriteAllTextAsync(pendingLanguagePath, string.Empty);
            }

            await services.InitializeAsync();
            await ConfigureLanguageAsync(
                services,
                databaseExistedAtStartup,
                pendingLanguagePath);

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
            LocalizedMessageBox.Show(
                LanguageRuntime.Format("Startup.Failed.Message", LanguageRuntime.ErrorMessage(exception)),
                LanguageRuntime.GetString("Startup.Failed.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceGate?.Dispose();
        _singleInstanceGate = null;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnNonUiUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        LanguageRuntime.ErrorReporter = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var rootException = e.Exception.GetBaseException();
        var signature = $"{rootException.GetType().FullName}|{rootException.Message}";
        var reportedAt = DateTimeOffset.UtcNow;
        if (_isShowingUnhandledException
            || (string.Equals(
                    signature,
                    _lastReportedUnhandledExceptionSignature,
                    StringComparison.Ordinal)
                && reportedAt - _lastReportedUnhandledExceptionAt
                < UnhandledExceptionDuplicateWindow))
        {
            return;
        }

        _lastReportedUnhandledExceptionSignature = signature;
        _lastReportedUnhandledExceptionAt = reportedAt;
        Trace.TraceError(e.Exception.ToString());
        _diagnostics.LogError("application.dispatcher-unhandled", e.Exception);
        _isShowingUnhandledException = true;
        try
        {
            LocalizedMessageBox.Show(
                Application.Current.MainWindow,
                LanguageRuntime.Format(
                    "Startup.Unhandled.Message",
                    LanguageRuntime.ErrorMessage(rootException)),
                "TavernDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isShowingUnhandledException = false;
        }
    }

    private void OnNonUiUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _diagnostics.LogError(
                "application.non-ui-unhandled",
                exception,
                new Dictionary<string, object?>
                {
                    ["is_terminating"] = e.IsTerminating
                });
        }
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        _diagnostics.LogError("application.unobserved-task", e.Exception);
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
                throw new ArgumentException(
                    LanguageRuntime.GetString("Startup.DataRootArgumentMissing"));
            }

            return args[index + 1];
        }

        return null;
    }

    private static async Task ConfigureLanguageAsync(
        InfrastructureServices services,
        bool databaseExistedAtStartup,
        string pendingLanguagePath)
    {
        var savedCultureName = await services.Settings.GetAsync(
            LanguageRuntime.SettingKey);
        if (!string.IsNullOrWhiteSpace(savedCultureName))
        {
            LanguageRuntime.Apply(savedCultureName);
            TryDeletePendingLanguageMarker(pendingLanguagePath);
            return;
        }

        var selectedCultureName = LanguageRuntime.DefaultCultureName;
        if (!databaseExistedAtStartup || File.Exists(pendingLanguagePath))
        {
            var dialog = new FirstRunLanguageDialog();
            if (dialog.ShowDialog() == true
                && !string.IsNullOrWhiteSpace(dialog.SelectedCultureName))
            {
                selectedCultureName = dialog.SelectedCultureName;
            }
        }

        selectedCultureName = LanguageRuntime.NormalizeCultureName(selectedCultureName);
        await services.Settings.SetAsync(
            LanguageRuntime.SettingKey,
            selectedCultureName);
        LanguageRuntime.Apply(selectedCultureName);
        TryDeletePendingLanguageMarker(pendingLanguagePath);
    }

    private static void TryDeletePendingLanguageMarker(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A saved database setting is authoritative. A stale marker is
            // harmless and will be retried on the next launch.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }
}
