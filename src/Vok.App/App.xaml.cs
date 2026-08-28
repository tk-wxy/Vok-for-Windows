using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Core.Diagnostics;
using Vok.Core.Launcher;
using Vok.Core.Clipboard;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Hosting;
using Vok.Infrastructure.Windows.Diagnostics;
using Vok.Infrastructure.Windows.Input;
using Vok.Infrastructure.Windows.Clipboard;
using Vok.Infrastructure.Windows.Launcher;
using Vok.Infrastructure.Windows.Persistence;
using Vok.Infrastructure.Windows.Shell;
using Vok.Infrastructure.Windows.Windowing;
using Vok.Features.Tray;
using Vok.Presentation;
using Vok.Features.Settings;
using Vok.Features.Launcher;
using Vok.Features.Clipboard;
using Vok.Hotkeys;

namespace Vok;

public partial class App : Application
{
    private ServiceProvider? _services;
    private SingleInstanceGuard? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstanceGuard("Local\\Vok.Workspace.M0");
        if (!_singleInstance.HasOwnership)
        {
            MessageBox.Show(
                "Vok 已经在运行。请使用已配置的全局快捷键呼出工作台。",
                "Vok",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _services = BuildServices();
        InitializeLauncherLayouts(_services);
        var appearance = _services.GetRequiredService<AppearanceController>();
        await appearance.InitializeAsync();
        var hotkeys = _services.GetRequiredService<HotkeyController>();
        await hotkeys.InitializeAsync();
        var launcher = _services.GetRequiredService<LauncherViewModel>();
        await launcher.InitializeAsync();
        var clipboard = _services.GetRequiredService<ClipboardViewModel>();
        await clipboard.InitializeAsync();
        var window = _services.GetRequiredService<MainWindow>();
        var controller = _services.GetRequiredService<WorkspaceController>();
        _ = _services.GetRequiredService<LauncherActionController>();
        _ = _services.GetRequiredService<ClipboardPasteController>();
        var clipboardController = _services.GetRequiredService<ClipboardController>();
        var applicationController = _services.GetRequiredService<ApplicationController>();
        MainWindow = window;

        try
        {
            await applicationController.StartAsync();
            await clipboardController.StartAsync();
        }
        catch (Exception exception)
        {
            controller.ShowDiagnostic(exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<ApplicationController>()?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(GlobalHotkeyGesture.Default);
        services.AddSingleton(WorkspaceHostOptions.FromEnvironment());
        services.AddSingleton<WorkspaceSessionStateMachine>();
        services.AddSingleton<IForegroundWindowService, Win32ForegroundWindowService>();
        services.AddSingleton<IWindowPlacementService, Win32WindowPlacementService>();
        services.AddSingleton<IDisplayWorkAreaProvider, Win32DisplayWorkAreaProvider>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<IGlobalHotkeyService>(provider =>
            provider.GetRequiredService<GlobalHotkeyService>());
        services.AddSingleton<IConfigurableGlobalHotkeyService>(provider =>
            provider.GetRequiredService<GlobalHotkeyService>());
        services.AddSingleton<ITrayMenuService, WindowsTrayMenuService>();
        services.AddSingleton<ITrayIconService, WpfTrayIconService>();
        services.AddSingleton<IPerformanceSampler, ProcessPerformanceSampler>();
        services.AddSingleton<IWorkspaceDiagnosticPublisher, Win32WorkspaceDiagnosticPublisher>();
        services.AddSingleton<IWorkspacePresentationService, DwmWorkspacePresentationService>();
        services.AddSingleton(_ => CreateStorageExecutor());
        services.AddSingleton<IAppSettingsStore>(provider =>
            new SqliteAppSettingsStore(provider.GetRequiredService<SqliteStorageExecutor>()));
        services.AddSingleton<ILauncherRepository>(provider =>
            new SqliteLauncherRepository(provider.GetRequiredService<SqliteStorageExecutor>()));
        services.AddSingleton<IClipboardHistoryRepository>(provider =>
            new SqliteClipboardHistoryRepository(provider.GetRequiredService<SqliteStorageExecutor>()));
        services.AddSingleton<IClipboardPlatformService, WindowsClipboardPlatformService>();
        services.AddSingleton<IClipboardCaptureNormalizer, ClipboardContentNormalizer>();
        services.AddSingleton<IPasteInputService, Win32PasteInputService>();
        services.AddSingleton<WindowsShellStaExecutor>();
        services.AddSingleton<ILauncherShellService, WindowsLauncherShellService>();
        services.AddSingleton<IClipboardFilePresentationService,
            WindowsClipboardFilePresentationService>();
        services.AddSingleton<IAppThemeApplier>(_ => new WpfAppThemeApplier(Current));
        services.AddSingleton<AppearanceController>();
        services.AddSingleton<HotkeyController>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LauncherLayoutProfileCache>();
        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<ClipboardViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<IWorkspaceWindow>(provider => provider.GetRequiredService<MainWindow>());
        services.AddSingleton<IWorkspaceFrameSource, WpfWorkspaceFrameSource>();
        services.AddSingleton<WorkspaceController>();
        services.AddSingleton<IWorkspaceCommands>(provider =>
            provider.GetRequiredService<WorkspaceController>());
        services.AddSingleton<LauncherActionController>();
        services.AddSingleton<ClipboardPasteController>();
        services.AddSingleton<ClipboardController>();
        services.AddSingleton<IApplicationLifetime, WpfApplicationLifetime>();
        services.AddSingleton<ApplicationController>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static void InitializeLauncherLayouts(ServiceProvider services)
    {
        var layouts = services.GetRequiredService<LauncherLayoutProfileCache>();
        try
        {
            layouts.Prewarm(
                services.GetRequiredService<IDisplayWorkAreaProvider>().GetWorkAreas());
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Unable to prewarm launcher layouts from display work areas: {0}",
                exception);
            _ = layouts.GetOrCreateForWorkArea(SystemParameters.WorkArea.Height);
        }
    }

    private static SqliteStorageExecutor CreateStorageExecutor()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        return new SqliteStorageExecutor(
            Path.Combine(localAppData, "Vok", "vok.db"));
    }
}
