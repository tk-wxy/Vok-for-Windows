using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Vok.Core.Stage;

namespace Vok.Features.Stage;

public sealed class StageItemViewModel : ObservableObject
{
    private StageFileAvailability _availability;
    private bool _hasAvailabilityResult;
    private bool _isBusy;

    public StageItemViewModel(StageItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        ParentPath = GetParentPath(item.TargetValue);
        (Glyph, TypeText) = item.Kind switch
        {
            StageItemKind.File => ("\uE8A5", "文件"),
            StageItemKind.Directory => ("\uE8B7", "文件夹"),
            StageItemKind.ShellLink => ("\uE71B", "快捷方式"),
            _ => ("\uE8A5", "项目"),
        };
    }

    public StageItem Item { get; private set; }

    public Guid Id => Item.Id;

    public StageItemKind Kind => Item.Kind;

    public string TargetValue => Item.TargetValue;

    public string CanonicalKey => Item.CanonicalKey;

    public string DisplayName => Item.ResolvedName;

    public long SortOrder => Item.SortOrder;

    public bool IsPinned => Item.IsPinned;

    public long Revision => Item.Revision;

    public DateTimeOffset CreatedUtc => Item.CreatedUtc;

    public DateTimeOffset UpdatedUtc => Item.UpdatedUtc;

    public string ParentPath { get; private set; }

    public string Glyph { get; private set; }

    public string TypeText { get; private set; }

    public StageFileAvailability Availability
    {
        get => _availability;
        private set => SetProperty(ref _availability, value);
    }

    public bool HasAvailabilityStatus =>
        _hasAvailabilityResult && Availability is not StageFileAvailability.Available;

    public bool IsMissing =>
        _hasAvailabilityResult && Availability is StageFileAvailability.Missing;

    public bool CanOpenOrReveal => !IsBusy && !IsMissing;

    public bool CanManage => !IsBusy;

    public string AvailabilityText => Availability switch
    {
        StageFileAvailability.Missing => "源文件已失效",
        StageFileAvailability.Unavailable => "暂不可访问",
        StageFileAvailability.Unknown => "位置状态未知",
        _ => string.Empty,
    };

    public string CompactAvailabilityText => HasAvailabilityStatus
        ? Availability switch
        {
            StageFileAvailability.Missing => "已失效",
            StageFileAvailability.Unavailable => "暂不可用",
            StageFileAvailability.Unknown => "状态未知",
            _ => string.Empty,
        }
        : string.Empty;

    public string CompactMetaText
    {
        get
        {
            if (HasAvailabilityStatus)
            {
                return CompactAvailabilityText;
            }

            if (Kind is StageItemKind.Directory)
            {
                return "文件夹";
            }

            if (Kind is StageItemKind.ShellLink)
            {
                return ".lnk";
            }

            var extension = Path.GetExtension(TargetValue);
            return string.IsNullOrEmpty(extension) ? "文件" : extension;
        }
    }

    public bool HasCompactStatus => IsPinned || HasAvailabilityStatus;

    public string CompactStatusText
    {
        get
        {
            var availabilityText = CompactAvailabilityText;
            if (IsPinned && availabilityText.Length != 0)
            {
                return $"固定 · {availabilityText}";
            }

            return IsPinned ? "固定" : availabilityText;
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanOpenOrReveal));
            OnPropertyChanged(nameof(CanManage));
        }
    }

    public string AutomationName
    {
        get
        {
            var pinnedText = IsPinned ? "已固定" : "未固定";
            return HasAvailabilityStatus
                ? $"{TypeText}，{DisplayName}，{TargetValue}，{pinnedText}，{AvailabilityText}"
                : $"{TypeText}，{DisplayName}，{TargetValue}，{pinnedText}";
        }
    }

    public void Update(StageItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id != Id)
        {
            throw new ArgumentException(
                "An authoritative Stage item update must keep the same item id.",
                nameof(item));
        }

        Item = item;
        ParentPath = GetParentPath(item.TargetValue);
        (Glyph, TypeText) = item.Kind switch
        {
            StageItemKind.File => ("\uE8A5", "文件"),
            StageItemKind.Directory => ("\uE8B7", "文件夹"),
            StageItemKind.ShellLink => ("\uE71B", "快捷方式"),
            _ => ("\uE8A5", "项目"),
        };
        _hasAvailabilityResult = false;
        _availability = StageFileAvailability.Unknown;

        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(TargetValue));
        OnPropertyChanged(nameof(CanonicalKey));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(SortOrder));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(Revision));
        OnPropertyChanged(nameof(CreatedUtc));
        OnPropertyChanged(nameof(UpdatedUtc));
        OnPropertyChanged(nameof(ParentPath));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(Availability));
        NotifyAvailabilityChanged();
    }

    public bool ApplyAvailability(
        long expectedRevision,
        StageFileAvailability availability)
    {
        if (Revision != expectedRevision || !Enum.IsDefined(availability))
        {
            return false;
        }

        Availability = availability;
        _hasAvailabilityResult = true;
        NotifyAvailabilityChanged();
        return true;
    }

    public void ResetAvailability(long expectedRevision)
    {
        if (Revision != expectedRevision || !_hasAvailabilityResult)
        {
            return;
        }

        _hasAvailabilityResult = false;
        Availability = StageFileAvailability.Unknown;
        NotifyAvailabilityChanged();
    }

    private void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(HasAvailabilityStatus));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(CanOpenOrReveal));
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(CompactAvailabilityText));
        OnPropertyChanged(nameof(CompactMetaText));
        OnPropertyChanged(nameof(HasCompactStatus));
        OnPropertyChanged(nameof(CompactStatusText));
        OnPropertyChanged(nameof(AutomationName));
    }

    private static string GetParentPath(string targetValue)
    {
        try
        {
            var withoutTrailingSeparators = targetValue.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (withoutTrailingSeparators.Length == 2 && withoutTrailingSeparators[1] == ':')
            {
                return targetValue;
            }

            return Path.GetDirectoryName(withoutTrailingSeparators) ?? targetValue;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            return targetValue;
        }
    }
}
