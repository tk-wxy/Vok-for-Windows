namespace Vok.Core.Stage;

public enum StageTargetFailureKind
{
    Empty,
    NotFullyQualified,
    Missing,
    AccessDenied,
    InvalidPath,
    Unsupported,
    Unavailable,
}

public sealed record StageTargetResolution(
    string Input,
    StageTargetDescriptor? Target,
    StageTargetFailureKind? Failure,
    string? Error)
{
    public bool Succeeded => Target is not null;
}
