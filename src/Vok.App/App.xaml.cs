using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Vok.Appearance;
using Vok.Core.Appearance;
using Vok.Core.Diagnostics;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Hosting;
using Vok.Infrastructure.Windows.Diagnostics;
using Vok.Infrastructure.Windows.Input;
using Vok.Infrastructure.Windows.Persistence;
using Vok.Infrastructure.Windows.Shell;
using Vok.Infrastructure.Windows.Windowing;
using Vok.Features.Tray;
using Vok.Presentation;
using Vok.Features.Settings;

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
        var appearance = _services.GetRequiredService<AppearanceController>();
        await appearance.InitializeAsync();
        var window = _services.GetRequiredService<MainWindow>();
        var applicationController = _services.GetRequiredService<ApplicationController>();
        var controller = _services.GetRequiredService<WorkspaceController>();
        MainWindow = window;

        try
        {
            await applicationController.StartAsync();
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

        services.AddSingleton(GlobalHotkeyGesture.M0DiagnosticDefault);
        services.AddSingleton(WorkspaceHostOptions.FromEnvironment());
        services.AddSingleton<WorkspaceSessionStateMachine>();
        services.AddSingleton<IForegroundWindowService, Win32ForegroundWindowService>();
        services.AddSingleton<IWindowPlacementService, Win32WindowPlacementService>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<ITrayMenuService, WindowsTrayMenuService>();
        services.AddSingleton<ITrayIconService, WpfTrayIconService>();
        services.AddSingleton<IPerformanceSampler, ProcessPerformanceSampler>();
        services.AddSingleton<IWorkspaceDiagnosticPublisher, Win32WorkspaceDiagnosticPublisher>();
        services.AddSingleton<IWorkspacePresentationService, DwmWorkspacePresentationService>();
        services.AddSingleton<IAppSettingsStore>(_ => CreateSettingsStore());
        services.AddSingleton<IAppThemeApplier>(_ => new WpfAppThemeApplier(Current));
        services.AddSingleton<AppearanceController>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<IWorkspaceWindow>(provider => provider.GetRequiredService<MainWindow>());
        services.AddSingleton<IWorkspaceFrameSource, WpfWorkspaceFrameSource>();
        services.AddSingleton<WorkspaceController>();
        services.AddSingleton<IWorkspaceCommands>(provider =>
            provider.GetRequiredService<WorkspaceController>());
        services.AddSingleton<IApplicationLifetime, WpfApplicationLifetime>();
        services.AddSingleton<ApplicationController>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static IAppSettingsStore CreateSettingsStore()
    {
        try
        {
            return SqliteAppSettingsStore.CreateDefault();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new UnavailableAppSettingsStore(exception);
        }
    }
}
