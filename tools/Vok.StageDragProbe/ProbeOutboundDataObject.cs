using System.Buffers.Binary;
using System.Windows;

namespace Vok.StageDragProbe;

// Prepare before gesture handling: fixture validation performs disk/Shell reads.
// The eventual OLE source must retain this owner until DoDragDrop returns.
internal sealed class ProbeOutboundDataObject : IProbeSourcePayload
{
    internal const string PreferredDropEffectFormat = "Preferred DropEffect";
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly MemoryStream _preferredEffect;
    private readonly MemoryStream _marker;
    private DataObject? _data;
    private IReadOnlyList<string> _paths;
    private readonly ProbeSourcePayloadIdentity _identity;

    private ProbeOutboundDataObject(ProbeFixtureBundle bundle, ProbeOutboundDragToken token)
    {
        var paths = bundle.OrderedPaths.ToArray();
        _paths = Array.AsReadOnly(paths);
        FixtureRunId = bundle.Manifest.RunId;
        SourceTreeDigest = bundle.Manifest.SourceTreeDigest;
        OrderedOccurrenceDigest = bundle.Manifest.OrderedOccurrenceDigest;
        TokenFormat = token.FormatName;
        _identity = new(FixtureRunId, bundle.Manifest.Profile, paths.Length, SourceTreeDigest, OrderedOccurrenceDigest);
        var effect = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(effect, (uint)DragDropEffects.Copy);
        _preferredEffect = new MemoryStream(effect, writable: false);
        _marker = new MemoryStream([1], writable: false);
        try
        {
            _data = new DataObject();
            _data.SetData(DataFormats.FileDrop, (string[])paths.Clone(), autoConvert: false);
            // A stream exposes raw HGLOBAL bytes; byte[] may use object serialization.
            _data.SetData(PreferredDropEffectFormat, _preferredEffect, autoConvert: false);
            _data.SetData(TokenFormat, _marker, autoConvert: false);
        }
        catch
        {
            _data = null;
            try { _preferredEffect.Dispose(); }
            finally { _marker.Dispose(); }
            throw;
        }
    }

    internal static ProbeOutboundDataObject Create(
        ProbeOutputDirectory fixtureOutput,
        ProbeFixtureProfile profile,
        string sourceRevision,
        ProbeOutboundDragToken? token = null)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The outbound data object must be created on its owning STA.");
        // Accept only a freshly revalidated probe manifest, never an arbitrary path array.
        var bundle = ProbeFixtureManifestStore.LoadAndValidate(fixtureOutput, profile, sourceRevision);
        return new ProbeOutboundDataObject(bundle, token ?? ProbeOutboundDragToken.Current);
    }

    internal string FixtureRunId { get; }
    internal string SourceTreeDigest { get; }
    internal string OrderedOccurrenceDigest { get; }
    internal string TokenFormat { get; }
    internal IReadOnlyList<string> Paths => _paths;
    internal DragDropEffects AllowedEffects => DragDropEffects.Copy;
    internal bool IsDisposed => _data is null;

    DataObject IProbeSourcePayload.Data => Data;
    ProbeSourcePayloadIdentity IProbeSourcePayload.Identity { get { VerifyAccess(); return _identity; } }
    ProbeSourcePayloadResources IProbeSourcePayload.Resources
    {
        get
        {
            VerifyAccess();
            // Access revocation and the two actual stream releases are separate observations.
            return new(_data is null ? 0 : 1, _preferredEffect.CanRead ? 1 : 0, _marker.CanRead ? 1 : 0);
        }
    }

    internal DataObject Data
    {
        get
        {
            VerifyAccess();
            ObjectDisposedException.ThrowIf(_data is null, this);
            return _data;
        }
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("The outbound data object belongs to another STA.");
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_data is null) return;
        _data = null; // Reentrant/late callers cannot obtain an owner-released data object.
        _paths = Array.Empty<string>();
        try { _preferredEffect.Dispose(); }
        finally { _marker.Dispose(); }
    }
}
