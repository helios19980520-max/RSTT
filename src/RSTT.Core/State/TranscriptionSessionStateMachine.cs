namespace RSTT.Core.State;

public enum TranscriptionSessionState
{
    Stopped,
    Starting,
    LoadingModel,
    StartingAudio,
    Listening,
    Completing,
    Stopping,
    Recovering,
    Faulted,
}

public sealed class TranscriptionSessionStateMachine
{
    private static readonly Dictionary<TranscriptionSessionState, TranscriptionSessionState[]> AllowedTransitions =
        new Dictionary<TranscriptionSessionState, TranscriptionSessionState[]>
        {
            [TranscriptionSessionState.Stopped] = [TranscriptionSessionState.Starting, TranscriptionSessionState.LoadingModel],
            [TranscriptionSessionState.Starting] = [TranscriptionSessionState.LoadingModel, TranscriptionSessionState.StartingAudio, TranscriptionSessionState.Stopping, TranscriptionSessionState.Faulted],
            [TranscriptionSessionState.LoadingModel] = [TranscriptionSessionState.StartingAudio, TranscriptionSessionState.Stopped, TranscriptionSessionState.Faulted],
            [TranscriptionSessionState.StartingAudio] = [TranscriptionSessionState.Listening, TranscriptionSessionState.Stopping, TranscriptionSessionState.Faulted],
            [TranscriptionSessionState.Listening] = [TranscriptionSessionState.Completing, TranscriptionSessionState.Stopping, TranscriptionSessionState.Recovering, TranscriptionSessionState.Faulted],
            [TranscriptionSessionState.Completing] = [TranscriptionSessionState.Stopping, TranscriptionSessionState.Faulted],
            [TranscriptionSessionState.Stopping] = [TranscriptionSessionState.Stopped, TranscriptionSessionState.Faulted],
            [TranscriptionSessionState.Recovering] = [TranscriptionSessionState.Listening, TranscriptionSessionState.Stopping, TranscriptionSessionState.Faulted],
            [TranscriptionSessionState.Faulted] = [TranscriptionSessionState.Stopped, TranscriptionSessionState.Starting],
        };

    public TranscriptionSessionState Current { get; private set; } = TranscriptionSessionState.Stopped;

    public bool CanTransitionTo(TranscriptionSessionState next) =>
        next == Current || AllowedTransitions[Current].Contains(next);

    public void TransitionTo(TranscriptionSessionState next)
    {
        if (!CanTransitionTo(next))
        {
            throw new InvalidOperationException($"Invalid transcription session transition: {Current} -> {next}.");
        }

        Current = next;
    }
}
