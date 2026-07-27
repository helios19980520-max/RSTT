namespace RSTT.Core.State;

public enum ApplicationState
{
    Initializing,
    ModelMissing,
    ModelLoading,
    Ready,
    Listening,
    Stopping,
    Error,
}
