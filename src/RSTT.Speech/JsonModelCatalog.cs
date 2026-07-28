using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Speech;

public sealed class JsonModelCatalog : IModelCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyList<ModelDescriptor> _models;

    public JsonModelCatalog()
    {
        var assembly = typeof(JsonModelCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("Assets.model-catalog.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The built-in RSTT model catalog is missing.");
        _models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, SerializerOptions)
            ?? throw new InvalidDataException("The built-in RSTT model catalog is empty.");
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"The built-in model catalog is invalid: {string.Join("; ", errors)}");
        }
    }

    public IReadOnlyList<ModelDescriptor> GetModels() => _models;

    public ModelDescriptor GetById(string modelId) =>
        _models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentOutOfRangeException(nameof(modelId), modelId, "The requested model is not in the RSTT catalog.");

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var duplicate in _models.GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add($"duplicate model ID '{duplicate.Key}'");
        }

        foreach (var model in _models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.DisplayName))
            {
                errors.Add("a model has no ID or display name");
            }

            if (model.IntegrationStatus != ModelIntegrationStatus.Available)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(model.DirectoryName) ||
                model.Artifacts.Count == 0 ||
                model.Engine is not ("online-transducer" or "online-zipformer2-ctc" or "online-nemo-ctc"))
            {
                errors.Add($"{model.Id} has an incomplete available integration");
            }

            foreach (var artifact in model.Artifacts)
            {
                if (!Uri.TryCreate(artifact.SourceUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps ||
                    artifact.ExpectedBytes <= 0 ||
                    artifact.Sha256.Length != 64 ||
                    !artifact.Sha256.All(Uri.IsHexDigit))
                {
                    errors.Add($"{model.Id}/{artifact.FileName} has invalid artifact metadata");
                }
            }
        }

        return errors;
    }
}
