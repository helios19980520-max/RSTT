using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IModelManager
{
    ModelInformation GetSelectedModel();

    bool TryValidateModel(out ModelInformation model);

    ModelInstallation GetSelectedInstallation();
}
