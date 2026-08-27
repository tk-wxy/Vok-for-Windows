using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Reflection;
using Vok.Core.Diagnostics;
using Vok.Core.Platform;
using Vok.Features.Settings;

namespace Vok.Presentation;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private string _hotkeyLabel = "等待热键注册";
    [ObservableProperty] private string _statusText = "后台初始化中。";
    [ObservableProperty] private string _inputReadyText = "—";
    [ObservableProperty] private string _firstFrameText = "—";
    [ObservableProperty] private int _showCount;
    [ObservableProperty] private string _workingSetText = "WS —";
    [ObservableProperty] private string _privateMemoryText = "Private —";
    [ObservableProperty] private string _handleText = "Handles —";
    [ObservableProperty] private string _threadText = "Threads —";
    [ObservableProperty] private string _visibilityStateText = "未观察";
    [ObservableProperty] private string _topmostStateText = "未观察";
    [ObservableProperty] private string _foregroundStateText = "未观察";
    [ObservableProperty] private string _keyboardFocusStateText = "未观察";
    [ObservableProperty] private WorkspaceSection _currentSection = WorkspaceSection.Workspace;

    public MainWindowViewModel(SettingsViewModel settings)
    {
        Settings = settings;
    }

    public SettingsViewModel Settings { get; }

    public bool IsSettingsVisible => CurrentSection is WorkspaceSection.Settings;

    public bool IsWorkspaceVisible => CurrentSection is WorkspaceSection.Workspace;

    public string ApplicationVersionText { get; } = CreateApplicationVersionText();

    public void NavigateTo(WorkspaceSection section) => CurrentSection = section;

    [RelayCommand]
    private void OpenSettings() => NavigateTo(WorkspaceSection.Settings);

    [RelayCommand]
    private void CloseSettings() => NavigateTo(WorkspaceSection.Workspace);

    public void MarkReady(string gesture)
    {
        HotkeyLabel = gesture;
        StatusText = "热键已注册；生产宿主在后台保持隐藏。";
    }

    public void RecordPresentationObservation(
        WorkspacePresentationObservation observation,
        bool hasWpfInputFocus)
    {
        VisibilityStateText = observation.IsVisible ? "已确认" : "未确认";
        TopmostStateText = observation.IsTopmost ? "已确认" : "未确认";
        ForegroundStateText = observation.IsForeground ? "已确认" : "未确认";
        KeyboardFocusStateText = observation.HasWin32KeyboardFocus && hasWpfInputFocus
            ? "已确认"
            : "未确认";

        if (!observation.IsOsInputReady || !hasWpfInputFocus)
        {
            StatusText = "窗口已进入呈现流程，但四项宿主条件尚未全部确认。";
        }
    }

    public void RecordInputReady(TimeSpan elapsed)
    {
        ShowCount++;
        InputReadyText = $"{elapsed.TotalMilliseconds:F1} ms";
        StatusText = "面板内容、顶层、前台与键盘输入焦点均已确认。";
    }

    public void RecordFirstFrame(TimeSpan elapsed)
    {
        FirstFrameText = $"{elapsed.TotalMilliseconds:F1} ms";
    }

    public void RecordPerformanceSnapshot(PerformanceSnapshot snapshot) => ApplySnapshot(snapshot);

    public void RecordHidden(ForegroundRestoreResult result, PerformanceSnapshot snapshot)
    {
        StatusText = result.Status switch
        {
            ForegroundRestoreStatus.Confirmed or ForegroundRestoreStatus.AlreadyForeground =>
                "面板已隐藏，原前台与焦点上下文已确认恢复。",
            ForegroundRestoreStatus.ForegroundConfirmedFocusChanged =>
                "面板已隐藏；原窗口已恢复前台，但焦点子窗口发生变化。",
            ForegroundRestoreStatus.NotRequested =>
                "面板已隐藏；保留当前外部前台，未请求恢复旧目标。",
            _ => $"面板已隐藏；焦点恢复降级：{result.Status}。",
        };
        ApplySnapshot(snapshot);
    }

    public void RecordFault(Exception exception) =>
        StatusText = $"宿主错误：{exception.Message}";

    partial void OnCurrentSectionChanged(WorkspaceSection value)
    {
        OnPropertyChanged(nameof(IsSettingsVisible));
        OnPropertyChanged(nameof(IsWorkspaceVisible));
    }

    private void ApplySnapshot(PerformanceSnapshot snapshot)
    {
        WorkingSetText = $"WS {FormatBytes(snapshot.WorkingSetBytes)}";
        PrivateMemoryText = $"Private {FormatBytes(snapshot.PrivateMemoryBytes)}";
        HandleText = $"Handles {snapshot.HandleCount} · USER {snapshot.UserObjectCount}";
        ThreadText = $"Threads {snapshot.ThreadCount} · GDI {snapshot.GdiObjectCount}";
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:F1} MB";

    private static string CreateApplicationVersionText()
    {
        var assembly = typeof(MainWindowViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = informationalVersion?.Split('+', 2)[0]
            ?? assembly.GetName().Version?.ToString(3)
            ?? "未知版本";
        return $"Vok {version}";
    }
}
