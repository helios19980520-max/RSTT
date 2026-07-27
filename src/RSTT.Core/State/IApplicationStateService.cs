namespace RSTT.Core.State;

public interface IApplicationStateService
{
    ApplicationStateSnapshot Current { get; }

    event EventHandler<ApplicationStateSnapshot>? Changed;

    void TransitionTo(ApplicationState state, string statusMessage, Exception? exception = null);
}
