namespace RSTT.App.Services;

/// <summary>
/// Acquires one per-user-session application instance and lets later launches
/// ask the primary instance to restore its main window before they exit.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\Helios.RSTT.SingleInstance.v1";
    private const string ActivationEventName = @"Local\Helios.RSTT.Activate.v1";
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private bool _ownsMutex;
    private bool _disposed;

    private SingleInstanceCoordinator(
        Mutex? mutex,
        EventWaitHandle? activationEvent,
        bool ownsMutex)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static SingleInstanceCoordinator Acquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalPrimaryInstance();
            return new SingleInstanceCoordinator(null, null, false);
        }

        var activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivationEventName);
        return new SingleInstanceCoordinator(mutex, activationEvent, true);
    }

    public void Listen(Action activate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activate);
        if (!IsPrimary || _activationEvent is null || _listener is not null)
        {
            return;
        }

        _listener = Task.Run(() =>
        {
            var handles = new WaitHandle[]
            {
                _activationEvent,
                _shutdown.Token.WaitHandle,
            };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                activate();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        try
        {
            _listener?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _activationEvent?.Dispose();
        _shutdown.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _ownsMutex = false;
        }

        _mutex?.Dispose();
    }

    private static void SignalPrimaryInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent =
                    EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(25);
            }
        }
    }
}
