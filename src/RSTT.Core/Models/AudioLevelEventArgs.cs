namespace RSTT.Core.Models;

public sealed class AudioLevelEventArgs(double level, double peak) : EventArgs
{
    public double Level { get; } = Math.Clamp(level, 0, 1);

    public double Peak { get; } = Math.Clamp(peak, 0, 1);
}
