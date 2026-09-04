namespace Vok.Core.Stage;

public sealed record StageTargetDescriptor(
    StageItemKind Kind,
    string TargetValue,
    string CanonicalKey,
    string ResolvedName);
