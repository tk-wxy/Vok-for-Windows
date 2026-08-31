using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using Vok.Core.Clipboard;

namespace Vok.Features.Clipboard;

public enum ClipboardThumbnailDisplayState
{
    NotRequested = 0,
    Loading = 1,
    Ready = 2,
    Unavailable = 3,
}

public sealed partial class ClipboardItemViewModel : ObservableObject
{
    [ObservableProperty] private ClipboardEntry _entry;
    [ObservableProperty] private bool _isDeleteArmed;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private ClipboardFileAvailability _fileAvailability;
    [ObservableProperty] private string _fileDetailsText = string.Empty;
    [ObservableProperty] private ClipboardThumbnailDisplayState _thumbnailState;

    public ClipboardItemViewModel(ClipboardEntry entry)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public Guid Id => Entry.Id;

    public bool IsFileList => Entry.Kind is ClipboardContentKind.FileList;

    public bool IsImage => Entry.Kind is ClipboardContentKind.Image;

    public ClipboardFileTransferIntent? FileIntent => Entry.FileIntent;

    public string PreviewText => string.IsNullOrEmpty(Entry.PlainPreview)
        ? IsFileList
            ? "（空文件列表）"
            : IsImage
                ? "图片"
                : "（空文本）"
        : Entry.PlainPreview;

    public string ImageDimensionsText => Entry.Image is { } image
        ? $"{image.PixelWidth}×{image.PixelHeight}"
        : "尺寸不可用";

    public string ThumbnailStatusText => ThumbnailState switch
    {
        ClipboardThumbnailDisplayState.Loading => "正在加载缩略图",
        ClipboardThumbnailDisplayState.Unavailable => "缩略图不可用",
        _ => string.Empty,
    };

    public string IntentText => Entry.FileIntent switch
    {
        ClipboardFileTransferIntent.Copy => "复制",
        ClipboardFileTransferIntent.Move => "剪切",
        ClipboardFileTransferIntent.Unknown => "意图未知 · 将复制",
        _ => string.Empty,
    };

    public string AvailabilityText => FileAvailability switch
    {
        ClipboardFileAvailability.Missing => "源文件已失效",
        ClipboardFileAvailability.Unavailable => "暂不可访问",
        ClipboardFileAvailability.Unknown => "位置状态未知",
        _ => string.Empty,
    };

    public string PasteToolTip => Entry.FileIntent is ClipboardFileTransferIntent.Move
        ? "剪切粘贴到呼出前目标；可能移动源文件"
        : IsImage
            ? "粘贴到呼出前目标；文件夹或桌面将保存 PNG"
            : "粘贴到呼出前目标";

    public string CapturedAtText => Entry.LastCapturedUtc
        .ToLocalTime()
        .ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public string PinGlyph => Entry.IsPinned ? "\uE77A" : "\uE840";

    public string PinToolTip => Entry.IsPinned ? "取消固定" : "固定";

    public string DeleteToolTip => IsDeleteArmed ? "再次点击确认删除" : "删除历史";

    public string DeleteGlyph => IsDeleteArmed ? "\uE73E" : "\uE74D";

    public string AutomationName => Entry.Kind switch
    {
        ClipboardContentKind.FileList =>
            Entry.FileIntent is ClipboardFileTransferIntent.Move
                ? $"文件剪贴板，{PreviewText}，{IntentText}，粘贴可能移动源文件，{CapturedAtText}"
                : $"文件剪贴板，{PreviewText}，{IntentText}，{CapturedAtText}",
        ClipboardContentKind.Image => string.IsNullOrEmpty(ThumbnailStatusText)
            ? $"图片剪贴板，{ImageDimensionsText}，{CapturedAtText}"
            : $"图片剪贴板，{ImageDimensionsText}，{ThumbnailStatusText}，{CapturedAtText}",
        _ => $"文本剪贴板，{PreviewText}，{CapturedAtText}",
    };

    public void Update(ClipboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var imageIdentityChanged = Entry.Kind != entry.Kind ||
            Entry.Revision != entry.Revision ||
            !string.Equals(
                Entry.Image?.ThumbnailPng?.Hash,
                entry.Image?.ThumbnailPng?.Hash,
                StringComparison.Ordinal) ||
            Entry.Image?.ThumbnailPng?.Revision != entry.Image?.ThumbnailPng?.Revision;
        Entry = entry;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(IsFileList));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(FileIntent));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(ImageDimensionsText));
        OnPropertyChanged(nameof(IntentText));
        OnPropertyChanged(nameof(PasteToolTip));
        OnPropertyChanged(nameof(CapturedAtText));
        OnPropertyChanged(nameof(PinGlyph));
        OnPropertyChanged(nameof(PinToolTip));
        OnPropertyChanged(nameof(AutomationName));
        if (imageIdentityChanged)
        {
            SetThumbnailState(ClipboardThumbnailDisplayState.NotRequested);
        }
    }

    public void ApplyFilePresentation(
        ClipboardReplayPayload payload,
        ClipboardFilePresentation presentation,
        ImageSource? icon)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(presentation);
        if (!IsFileList || payload.Kind is not ClipboardContentKind.FileList ||
            payload.FileReferences.Count == 0)
        {
            return;
        }

        var first = payload.FileReferences[0];
        FileDetailsText = first.CanonicalPath;
        FileAvailability = presentation.Availability;
        Icon = icon;
        OnPropertyChanged(nameof(AvailabilityText));
    }

    public void MarkFilePresentationUnavailable()
    {
        if (!IsFileList)
        {
            return;
        }

        FileAvailability = ClipboardFileAvailability.Unavailable;
        OnPropertyChanged(nameof(AvailabilityText));
    }

    public void SetDeleteArmed(bool isArmed)
    {
        IsDeleteArmed = isArmed;
        OnPropertyChanged(nameof(DeleteToolTip));
        OnPropertyChanged(nameof(DeleteGlyph));
    }

    public void SetThumbnailState(ClipboardThumbnailDisplayState state)
    {
        if (!Enum.IsDefined(state) || ThumbnailState == state)
        {
            return;
        }

        ThumbnailState = state;
        OnPropertyChanged(nameof(ThumbnailStatusText));
        OnPropertyChanged(nameof(AutomationName));
    }
}
