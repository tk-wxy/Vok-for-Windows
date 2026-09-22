namespace Vok.StageDragProbe;

// One format per process instance, reused across drags. This is self-recognition,
// not authentication; no token payload ever needs to be read by the inbound gate.
internal sealed class ProbeOutboundDragToken
{
    internal static ProbeOutboundDragToken Current { get; } = new();

    // Separate instances are also useful for isolated source/target contract tests.
    internal ProbeOutboundDragToken() => FormatName = $"Vok.StageDragProbe.Outbound.v1.{Guid.NewGuid():N}";

    internal string FormatName { get; }
}
