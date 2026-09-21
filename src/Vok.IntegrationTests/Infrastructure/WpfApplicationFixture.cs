using System.Runtime.ExceptionServices;
using System.Windows;
using Xunit;

namespace Vok.IntegrationTests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfApplicationCollection : ICollectionFixture<WpfApplicationFixture>
{
    public const string Name = "WPF application";
}

public sealed class WpfApplicationFixture : IDisposable
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private Application? _application;
    private Exception? _startupFailure;
    private int _disposed;

    public WpfApplicationFixture()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vok WPF integration test host",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();

        if (_startupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(_startupFailure).Throw();
        }
    }

    public Application Application =>
        _application ?? throw new InvalidOperationException("The WPF test host is unavailable.");

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Application.Dispatcher.Invoke(action);
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Application.Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_application is not null)
        {
            _application.Dispatcher.Invoke(_application.Shutdown);
        }

        _thread.Join();
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            _application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            _ready.Set();
            _application.Run();
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _ready.Set();
        }
    }
}
