using FileExplorer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

namespace FileExplorer;

public partial class App : Application
{
    private Window? _window;
    private IHost? _host;

    /// The composition root's resolver, for the handful of Views that WinUI's XAML parser
    /// instantiates directly (PaneView, PreviewPane, TerminalPane) and so can't receive
    /// constructor-injected dependencies - they resolve what they need from here instead. Every
    /// other View is `new`'d explicitly in code-behind and gets real constructor injection.
    public static IServiceProvider Services => ((App)Current)._host!.Services;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception, "AppDomain");
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception, "XamlUnhandled");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(ConfigureServices)
                .Build();
            _host.Start();

            // Fully-qualified to disambiguate from the App.Services property above - these three
            // become instance resolutions from the container once NotificationService/WatchService/
            // ScheduleService are converted later in the migration.
            FileExplorer.Services.NotificationService.Register();
            FileExplorer.Services.WatchService.Start();
            FileExplorer.Services.ScheduleService.Start();
            _window = new MainWindow(
                Services.GetRequiredService<IFileSystemService>(),
                Services.GetRequiredService<ISessionService>(),
                Services.GetRequiredService<IRemoteConnectionService>());
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash(ex, "OnLaunched");
            throw;
        }
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // Descoped conversion: only the services PaneViewModel/MainViewModel/TabViewModel need
        // directly are DI-registered. Each keeps calling its own internal dependencies (other
        // still-static services) exactly as before - full app-wide DI adoption is much larger in
        // scope (see the dependency-injection migration plan) and deliberately out of scope here.
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<IRemoteConnectionService, RemoteConnectionService>();
    }

    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {source}\n{ex}\n\n");
        }
        catch
        {
            // best effort
        }
    }
}
