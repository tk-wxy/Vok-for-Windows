using System.IO;
using Vok.Core.Stage;

namespace Vok.Features.Stage;

public sealed class StageItemViewModel
{
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
        AutomationName = $"{TypeText}，{item.ResolvedName}，{item.TargetValue}";
    }

    public StageItem Item { get; }

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

    public string ParentPath { get; }

    public string Glyph { get; }

    public string TypeText { get; }

    public string AutomationName { get; }

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
