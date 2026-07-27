namespace RSTT.Speech;

internal sealed class ModelManifest
{
    public string Id { get; set; } = LocalModelManager.DefaultModelId;

    public string DisplayName { get; set; } = "Parakeet Unified English";

    public string Engine { get; set; } = "online-transducer";

    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> FileSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Sha256 { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int NumThreads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public string Provider { get; set; } = "cpu";

    public int FeatureDimension { get; set; } = 128;
}
