using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using System.Diagnostics;
using System.Text;
using StorageLens.BackgroundTasks;
using StorageLens.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Package = Windows.ApplicationModel.Package;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace StorageLens;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan StartupIndexMaintenanceDelay =
        TimeSpan.FromSeconds(15);
    private static uint _comRegistrationToken;
    private static uint _indexComRegistrationToken;
    private AppUpdateCoordinator? _updateCoordinator;
    private Task? _startupIndexMaintenanceTask;

    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    public static string PendingLaunchArgument { get; private set; } = string.Empty;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        HighContrastAdjustment =
            ApplicationHighContrastAdjustment.None;
        UnhandledException += App_UnhandledException;
        if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(
                    argument,
                    "-RegisterForBGTaskServer",
                    StringComparison.OrdinalIgnoreCase)))
        {
            _comRegistrationToken =
                ComServer.RegisterStorageMonitorFactory();
            _indexComRegistrationToken =
                ComServer.RegisterStorageIndexFactory();
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            if (Environment.GetCommandLineArgs().Any(argument =>
                    string.Equals(argument, "-RegisterForBGTaskServer", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            DispatcherQueue =
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var currentInstance = AppInstance.GetCurrent();
            var coordinator = new AppUpdateCoordinator();
            _updateCoordinator = coordinator;
            var packageVersion = Package.Current.Id.Version;
            if (!await coordinator.TryBecomePrimaryAsync(
                    currentInstance,
                    new Version(
                        packageVersion.Major,
                        packageVersion.Minor,
                        packageVersion.Build,
                        packageVersion.Revision),
                    Package.Current.Id.Name,
                    Package.Current.Id.PublisherId,
                    RequestExitForUpdate))
            {
                coordinator.Dispose();
                _updateCoordinator = null;
                Exit();
                return;
            }

            currentInstance.Activated += MainInstance_Activated;
            PendingLaunchArgument = GetActivationArgument(currentInstance.GetActivatedEventArgs(), args);
            Window = new MainWindow();
            Window.Activate();
            _ = InitializeRuntimeAsync();
        }
        catch (Exception exception)
        {
            _updateCoordinator?.Dispose();
            _updateCoordinator = null;
            WriteCrashLog(exception);
            throw;
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
    }

    private static string GetActivationArgument(Microsoft.UI.Xaml.LaunchActivatedEventArgs fallback)
    {
        var commandLineProtocolArgument = Environment.GetCommandLineArgs()
            .FirstOrDefault(argument => argument.StartsWith("storagelens://", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(commandLineProtocolArgument))
        {
            return commandLineProtocolArgument;
        }

        return GetActivationArgument(AppInstance.GetCurrent().GetActivatedEventArgs(), fallback);
    }

    private static string GetActivationArgument(
        AppActivationArguments activation,
        Microsoft.UI.Xaml.LaunchActivatedEventArgs? fallback = null)
    {
        return activation.Kind switch
        {
            ExtendedActivationKind.Protocol when activation.Data is IProtocolActivatedEventArgs protocol =>
                protocol.Uri.ToString(),
            ExtendedActivationKind.Launch when activation.Data is ILaunchActivatedEventArgs launch =>
                launch.Arguments,
            _ => fallback?.UWPLaunchActivatedEventArgs.Arguments ?? string.Empty,
        };
    }

    private void MainInstance_Activated(object? sender, AppActivationArguments args)
    {
        var argument = GetActivationArgument(args);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Window is MainWindow mainWindow)
            {
                mainWindow.HandleActivation(argument);
            }
            else
            {
                PendingLaunchArgument = argument;
            }
        });
    }

    private void RequestExitForUpdate()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _updateCoordinator?.Dispose();
            Exit();
        });
    }

    private async Task InitializeRuntimeAsync()
    {
        try
        {
            await Task.Run(() => AppServices.InitializeAsync());
            TryDeleteStartupErrorLog();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await BackgroundTaskRegistrationService.ReconcileAsync();
                }
                catch (Exception exception)
                {
                    WriteCrashLog(exception);
                }
            });
            StartStartupIndexMaintenance();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    private void StartStartupIndexMaintenance()
    {
        if (_startupIndexMaintenanceTask is { IsCompleted: false })
        {
            return;
        }

        _startupIndexMaintenanceTask = Task.Run(
            RunStartupIndexMaintenanceAsync);
    }

    private static async Task RunStartupIndexMaintenanceAsync()
    {
        try
        {
            await Task.Delay(StartupIndexMaintenanceDelay)
                .ConfigureAwait(false);
            await AppServices.Repository.PruneHistoryWhenIdleAsync(
                    completedScansPerDrive: 1)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                $"Storage index startup maintenance failed: {exception}");
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        var localFolder = ApplicationData.Current.LocalFolder.Path;
        var logPath = Path.Combine(localFolder, "startup-error.txt");
        var contents = new StringBuilder()
            .AppendLine(DateTimeOffset.Now.ToString("O"))
            .AppendLine(exception.ToString());
        File.WriteAllText(logPath, contents.ToString());
    }

    private static void TryDeleteStartupErrorLog()
    {
        try
        {
            File.Delete(Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "startup-error.txt"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
