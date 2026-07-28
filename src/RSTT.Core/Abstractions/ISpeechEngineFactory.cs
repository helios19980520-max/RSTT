using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface ISpeechEngineFactory
{
    ISpeechRecognitionEngine Create(ModelDescriptor descriptor);
}
