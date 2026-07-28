using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IModelCatalog
{
    IReadOnlyList<ModelDescriptor> GetModels();

    ModelDescriptor GetById(string modelId);

    IReadOnlyList<string> Validate();
}
