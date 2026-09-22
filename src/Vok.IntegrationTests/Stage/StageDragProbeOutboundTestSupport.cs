using System.IO;
using System.Windows;
using Vok.StageDragProbe;

namespace Vok.IntegrationTests.Stage;

internal sealed class ProbeOutboundTestData(IDataObject? inner = null) : IDataObject
{
    private readonly IDataObject _inner = inner ?? new DataObject(
        DataFormats.FileDrop, new[] { @"C:\probe\a.txt", @"C:\probe\a.txt" }, autoConvert: false);
    internal List<(string Operation, string Format, bool AutoConvert)> Calls { get; } = [];
    internal Action<string, string, bool>? BeforeCall { get; set; }
    internal Func<string, object?>? ReadOverride { get; set; }

    public object? GetData(string format, bool autoConvert)
    {
        Call("get", format, autoConvert);
        return ReadOverride is { } read ? read(format) : _inner.GetData(format, autoConvert);
    }

    public bool GetDataPresent(string format, bool autoConvert)
    {
        Call("present", format, autoConvert);
        return _inner.GetDataPresent(format, autoConvert);
    }

    public string[] GetFormats(bool autoConvert)
    {
        Call("formats", "", autoConvert);
        return _inner.GetFormats(autoConvert);
    }

    private void Call(string operation, string format, bool autoConvert)
    {
        Calls.Add((operation, format, autoConvert));
        BeforeCall?.Invoke(operation, format, autoConvert);
    }

    public object? GetData(string format) => GetData(format, true);
    public object? GetData(Type format) => GetData(format.FullName!, true);
    public bool GetDataPresent(string format) => GetDataPresent(format, true);
    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!, true);
    public string[] GetFormats() => GetFormats(true);
    public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
    public void SetData(string format, object data) => throw new NotSupportedException();
    public void SetData(Type format, object data) => throw new NotSupportedException();
    public void SetData(object data) => throw new NotSupportedException();
}

internal sealed class ProbeOutboundTestRecorder : IProbeReceiverRecorder
{
    internal List<ProbeReceiverReport> Reports { get; } = [];
    internal int DisposeCount { get; private set; }
    internal Action<string>? OnRecord { get; set; }
    internal Action? OnComplete { get; set; }
    internal Action? OnDispose { get; set; }
    public void Record(string name, object details) => OnRecord?.Invoke(name);
    public void Complete(ProbeReceiverReport report)
    {
        Reports.Add(report);
        OnComplete?.Invoke();
    }
    public void Dispose()
    {
        DisposeCount++;
        OnDispose?.Invoke();
    }
}

internal sealed class ProbeOutboundTestLayout : IDisposable
{
    private ProbeOutboundTestLayout(string root, StageDragProbeOptions options, ProbeOutputDirectory output,
        ProbeFixtureBundle bundle)
    {
        Root = root;
        Options = options;
        Output = output;
        Bundle = bundle;
    }

    internal string Root { get; }
    internal StageDragProbeOptions Options { get; }
    internal ProbeOutputDirectory Output { get; }
    internal ProbeFixtureBundle Bundle { get; }

    internal static ProbeOutboundTestLayout Create(ProbeFixtureProfile profile = ProbeFixtureProfile.Single)
    {
        var root = Path.Combine(Path.GetTempPath(), $"vok-drag-outbound-tests-{Guid.NewGuid():N}");
        var repository = Path.Combine(root, "repository");
        var allowed = Path.Combine(root, "allowed");
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        File.WriteAllText(Path.Combine(repository, "src", "Vok.sln"), "test");
        Directory.CreateDirectory(allowed);
        var options = new StageDragProbeOptions(StageDragProbeVerb.Prepare,
            allowed, Path.Combine(allowed, "run"), repository, "outbound-test-revision", profile, null);
        var output = ProbeOutputDirectory.Reserve(options, new ProbePathEnvironment(
            Path.Combine(root, "protected-product"), Path.Combine(root, "protected-profile")));
        return new(root, options, output, ProbeFixtureBuilder.Build(options, output));
    }

    internal ProbeOutboundDataObject CreateData(ProbeOutboundDragToken? token = null) =>
        ProbeOutboundDataObject.Create(Output, Options.Profile!.Value, Options.SourceRevision, token);

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(Root));
        if (!string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(Root).StartsWith("vok-drag-outbound-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to delete a non-test directory: {Root}");
        Directory.Delete(Root, recursive: true);
    }
}
