using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Vok.Core.Diagnostics;
using Vok.Core.Platform;
using Vok.Core.Workspace;
using Vok.Hosting;
using Vok.Infrastructure.Windows.Diagnostics;
using Vok.Infrastructure.Windows.Input;
using Vok.Infrastructure.Windows.Windowing;
using Vok.Presentation;

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
        var window = _services.GetRequiredService<MainWindow>();
        var controller = _services.GetRequiredService<WorkspaceController>();
        MainWindow = window;

        try
        {
            await controller.StartAsync();
        }
        catch (Exception exception)
        {
            controller.ShowDiagnostic(exception);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<WorkspaceController>()?.Dispose();
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
        services.AddSingleton<IPerformanceSampler, ProcessPerformanceSampler>();
        services.AddSingleton<IWorkspaceDiagnosticPublisher, Win32WorkspaceDiagnosticPublisher>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<IWorkspaceWindow>(provider => provider.GetRequiredService<MainWindow>());
        services.AddSingleton<IWorkspaceFrameSource, WpfWorkspaceFrameSource>();
        services.AddSingleton<WorkspaceController>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
