namespace RSTT.Speech;

internal sealed class ModelManifest
{
    public string Id { get; set; } = "FastEnglish";

    public string DisplayName { get; set; } = "Fast English";

    public string Engine { get; set; } = "online-transducer";

    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int NumThreads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public string Provider { get; set; } = "cpu";
}
