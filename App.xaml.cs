using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace VRSoundboard;

public partial class App : Application
{
    private ServiceProvider? _services;
    private WebServerService? _web;
    private bool _backendOnly;
    private string? _electronPortFile;
    [STAThread]
    public static void Main()
    {
        var app = new App(); app.InitializeComponent(); app.Run();
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _backendOnly = e.Args.Any(a => string.Equals(a, "--electron-backend", StringComparison.OrdinalIgnoreCase));
        _electronPortFile = e.Args.FirstOrDefault(a => a.StartsWith("--electron-port-file=", StringComparison.OrdinalIgnoreCase))?["--electron-port-file=".Length..];
        if (_backendOnly) ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var storage = new Storage(); storage.Initialize(); AppServices.Storage = storage;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.File(Path.Combine(storage.LogsPath, "app-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14).CreateLogger();
            var collection = new ServiceCollection();
            collection.AddSingleton(storage); collection.AddSingleton<SoundLibrary>(); collection.AddSingleton<AudioExecutionContext>(); collection.AddSingleton<AudioDeviceService>(); collection.AddSingleton<AudioEngine>(); collection.AddSingleton<AppCoordinator>(); collection.AddSingleton<SteamMicDiagnostic>(); collection.AddSingleton<WebSocketHub>(); collection.AddSingleton<WebServerService>();
            _services = collection.BuildServiceProvider();
            var core = _services.GetRequiredService<AppCoordinator>().Initialize();
            if (_backendOnly && !core.Settings.WebEnabled) core.UpdateSettings(s => s.WebEnabled = true);
            var hub = _services.GetRequiredService<WebSocketHub>();
            core.Changed += (type, data) => _ = hub.Broadcast(type, data);
            _web = _services.GetRequiredService<WebServerService>();
            MainWindow? window = null;
            if (!_backendOnly)
            {
                window = new MainWindow(core, _web, hub, _services.GetRequiredService<SteamMicDiagnostic>());
                MainWindow = window; window.Show();
                if (core.Settings.StartMinimized) window.WindowState = WindowState.Minimized;
            }
            if (core.Settings.WebEnabled)
            {
                _web.StartedOnPort += port =>
                {
                    if (_backendOnly && !string.IsNullOrWhiteSpace(_electronPortFile))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_electronPortFile)!);
                        File.WriteAllText(_electronPortFile, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                };
                try { await _web.StartAsync(); }
                catch (Exception ex) { Log.Error(ex, "Web server unavailable; desktop remains open"); }
            }
            window?.RefreshAll();
        }
        catch (Exception ex) { Log.Fatal(ex, "Application startup failed"); MessageBox.Show(ex.ToString(), "VRSoundboard startup failed"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try { _web?.StopAsync().GetAwaiter().GetResult(); _services?.GetService<AppCoordinator>()?.Shutdown(); _services?.Dispose(); Log.CloseAndFlush(); }
        finally { base.OnExit(e); }
    }
}
