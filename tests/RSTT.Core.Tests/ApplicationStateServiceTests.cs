using RSTT.Core.State;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class ApplicationStateServiceTests
{
    [Fact]
    public void TransitionUpdatesSnapshotAndPublishesTheSameValue()
    {
        var service = new ApplicationStateService();
        ApplicationStateSnapshot? observed = null;
        service.Changed += (_, snapshot) => observed = snapshot;

        service.TransitionTo(ApplicationState.Ready, "Ready");

        Assert.Equal(ApplicationState.Ready, service.Current.State);
        Assert.Equal("Ready", service.Current.StatusMessage);
        Assert.Same(service.Current, observed);
    }

    [Fact]
    public void ErrorTransitionPreservesDiagnosticException()
    {
        var service = new ApplicationStateService();
        var error = new InvalidOperationException("native load failed");

        service.TransitionTo(ApplicationState.Error, "Speech model could not be loaded.", error);

        Assert.Same(error, service.Current.Error);
    }
}
