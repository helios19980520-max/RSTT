namespace RSTT.Core.State;

public enum ApplicationState
{
    Initializing,
    ModelMissing,
    ModelDownloading,
    ModelLoading,
    Ready,
    Listening,
    Stopping,
    Error,
}
