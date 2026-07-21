using System.Windows;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NextRdp.Infrastructure;
using NextRdp.Platform.Client;
using MessageBox = System.Windows.MessageBox;

namespace NextRdp.Desktop;

public partial class App : System.Windows.Application
{
    static App()
    {
        // The generated WPF entry point does not invoke the WinForms SDK's
        // ApplicationHighDpiMode bootstrap. Set the mixed WPF/WinForms process
        // context before the WPF Application instance or any HWND is created.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
    }

    private ServiceProvider? services;
    private bool darkTheme = true;
    private string? settingsPath;

    public App()
    {
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            TryWriteUnhandledExceptionMarker(eventArgs.Exception);
            if (MainWindow is NextRdp.Desktop.MainWindow { IsControlledShutdownInProgress: true })
            {
                Shutdown(0);
                return;
            }
            MessageBox.Show(
                "NextRDP stopped the current operation after an unexpected local error. Connection passwords were not stored. Restart the application; if the problem repeats, create a redacted support package.",
                "NextRDP stopped safely", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => eventArgs.SetObserved();
    }

    internal static UnhandledExceptionSummary CreateUnhandledExceptionSummary(Exception error)
    {
        var chain = new List<UnhandledExceptionNode>();
        for (Exception? current = error; current is not null && chain.Count < 8; current = current.InnerException)
        {
            chain.Add(new UnhandledExceptionNode(
                current.GetType().FullName ?? current.GetType().Name,
                current.HResult,
                current.TargetSite?.DeclaringType?.FullName,
                current.TargetSite?.Name));
        }

        return new UnhandledExceptionSummary(
            DateTimeOffset.UtcNow,
            error.GetType().FullName ?? error.GetType().Name,
            error.HResult,
            error.TargetSite?.DeclaringType?.FullName,
            error.TargetSite?.Name,
            error is System.Windows.Markup.XamlParseException xamlError ? xamlError.LineNumber : null,
            error is System.Windows.Markup.XamlParseException xamlPositionError ? xamlPositionError.LinePosition : null,
            chain);
    }

    private static void TryWriteUnhandledExceptionMarker(Exception error)
    {
        try
        {
            var appDataDirectory = AppDataDirectoryResolver.Resolve(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable(AppDataDirectoryResolver.TestOverrideEnvironmentVariable));
            Directory.CreateDirectory(appDataDirectory);
            File.WriteAllText(
                Path.Combine(appDataDirectory, "last-unhandled.json"),
                JsonSerializer.Serialize(CreateUnhandledExceptionSummary(error)));
        }
        catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Failure to write a privacy-safe diagnostic marker must never replace the original error path.
        }
    }

    public void ToggleTheme()
    {
        darkTheme = !darkTheme;
        ApplyCurrentTheme();
        if (settingsPath is not null)
        {
            try { File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppPreferences(darkTheme))); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void ApplyCurrentTheme()
    {
        if (SystemParameters.HighContrast) ThemeResourceManager.ApplyHighContrast(Resources);
        else ThemeResourceManager.ApplyStandard(Resources, darkTheme);
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
        if (Dispatcher.CheckAccess()) ApplyCurrentTheme();
        else Dispatcher.Invoke(ApplyCurrentTheme);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            SQLitePCL.Batteries_V2.Init();
            var appDataDirectory = AppDataDirectoryResolver.Resolve(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable(AppDataDirectoryResolver.TestOverrideEnvironmentVariable));
            Directory.CreateDirectory(appDataDirectory);
            settingsPath = Path.Combine(appDataDirectory, "settings.json");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var preferences = JsonSerializer.Deserialize<AppPreferences>(await File.ReadAllTextAsync(settingsPath));
                    if (preferences is not null) darkTheme = preferences.DarkTheme;
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
            ApplyCurrentTheme();
            var dataPath = Path.Combine(appDataDirectory, "nextrdp.db");
            var collection = new ServiceCollection();
            collection.AddNextRdpLocalInfrastructure(dataPath);
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            var controlPlaneText = Environment.GetEnvironmentVariable("NEXTRDP_CONTROL_PLANE_URL") ?? "http://127.0.0.1:5080/";
            var managedOptions = new ManagedClientOptions(new Uri(controlPlaneText, UriKind.Absolute), "nextrdp-desktop", new Uri("http://127.0.0.1:43876/callback"), version);
            managedOptions.Validate();
            var managedCache = new ManagedCacheStore(Path.Combine(appDataDirectory, "managed-cache.db"));
            await managedCache.InitializeAsync();
            collection.AddSingleton(managedOptions);
            collection.AddSingleton(managedCache);
            collection.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
            collection.AddSingleton<IInteractiveTokenBroker, SystemBrowserOidcBroker>();
            collection.AddSingleton<IManagedWorkspaceClient, ManagedWorkspaceClient>();
            collection.AddSingleton<MainViewModel>();
            services = collection.BuildServiceProvider(validateScopes: true);
            await LocalDatabaseInitializer.InitializeAsync(services.GetRequiredService<IDbContextFactory<NextRdpDbContext>>());
            var window = new MainWindow(services.GetRequiredService<MainViewModel>());
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            TryWriteUnhandledExceptionMarker(ex);
            MessageBox.Show(
                $"NextRDP could not initialize its local application data. No connection was started.\n\n{ex.Message}",
                "NextRDP could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private sealed record AppPreferences(bool DarkTheme);

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        services?.Dispose();
        base.OnExit(e);
    }
}

internal sealed record UnhandledExceptionSummary(
    DateTimeOffset OccurredAtUtc,
    string ExceptionType,
    int HResult,
    string? TargetType,
    string? TargetMethod,
    int? XamlLineNumber,
    int? XamlLinePosition,
    IReadOnlyList<UnhandledExceptionNode> ExceptionChain);

internal sealed record UnhandledExceptionNode(
    string ExceptionType,
    int HResult,
    string? TargetType,
    string? TargetMethod);
