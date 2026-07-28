namespace RSTT.Core.Models;

/// <summary>
/// Identifies one listening lifetime. Work from an earlier lifetime must never be
/// committed, displayed as current, or injected after a restart.
/// </summary>
public readonly record struct SessionGenerationId(long Value)
{
    public static SessionGenerationId None { get; } = new(0);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
