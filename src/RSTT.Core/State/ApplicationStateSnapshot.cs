namespace RSTT.Core.State;

public sealed record ApplicationStateSnapshot(ApplicationState State, string StatusMessage, Exception? Error = null);
