using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IModelManager
{
    event EventHandler<ModelInformation>? ModelChanged;

    IReadOnlyList<ModelInformation> GetAvailableModels();

    ModelInformation GetSelectedModel();

    bool TryValidateModel(out ModelInformation model);

    ModelInstallation GetSelectedInstallation();

    Task SelectAsync(string modelId, CancellationToken cancellationToken = default);

    Task DownloadAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string modelId, CancellationToken cancellationToken = default);
}
