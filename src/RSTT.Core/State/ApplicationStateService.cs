namespace RSTT.Core.State;

public sealed class ApplicationStateService : IApplicationStateService
{
    private ApplicationStateSnapshot _current = new(ApplicationState.Initializing, "Initializing…");

    public ApplicationStateSnapshot Current => _current;

    public event EventHandler<ApplicationStateSnapshot>? Changed;

    public void TransitionTo(ApplicationState state, string statusMessage, Exception? exception = null)
    {
        _current = new ApplicationStateSnapshot(state, statusMessage, exception);
        Changed?.Invoke(this, _current);
    }
}
