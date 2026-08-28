using CommunityToolkit.Mvvm.ComponentModel;
using Vok.Core.Clipboard;

namespace Vok.Features.Clipboard;

public sealed partial class ClipboardItemViewModel : ObservableObject
{
    [ObservableProperty] private ClipboardEntry _entry;
    [ObservableProperty] private bool _isDeleteArmed;

    public ClipboardItemViewModel(ClipboardEntry entry)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public Guid Id => Entry.Id;

    public string PreviewText => string.IsNullOrEmpty(Entry.PlainPreview)
        ? "（空文本）"
        : Entry.PlainPreview;

    public string CapturedAtText => Entry.LastCapturedUtc
        .ToLocalTime()
        .ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public string PinGlyph => Entry.IsPinned ? "\uE77A" : "\uE840";

    public string PinToolTip => Entry.IsPinned ? "取消固定" : "固定";

    public string DeleteToolTip => IsDeleteArmed ? "再次点击确认删除" : "删除历史";

    public string DeleteGlyph => IsDeleteArmed ? "\uE73E" : "\uE74D";

    public string AutomationName =>
        $"文本剪贴板，{PreviewText}，{CapturedAtText}";

    public void Update(ClipboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(CapturedAtText));
        OnPropertyChanged(nameof(PinGlyph));
        OnPropertyChanged(nameof(PinToolTip));
        OnPropertyChanged(nameof(AutomationName));
    }

    public void SetDeleteArmed(bool isArmed)
    {
        IsDeleteArmed = isArmed;
        OnPropertyChanged(nameof(DeleteToolTip));
        OnPropertyChanged(nameof(DeleteGlyph));
    }
}
