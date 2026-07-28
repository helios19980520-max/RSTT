using RSTT.Core.Models;

namespace RSTT.App.ViewModels;

public sealed record ComputeDiagnosticItem(
    string Name,
    string State,
    string Status,
    string Version,
    string RequiredVersion,
    string DetectedPath,
    string RemediationUrl)
{
    public static ComputeDiagnosticItem FromLayer(ComputeLayerStatus layer) =>
        new(
            FormatName(layer.Layer),
            layer.State.ToString(),
            layer.Status,
            layer.Version,
            layer.RequiredVersion,
            layer.DetectedPath,
            layer.RemediationUrl);

    private static string FormatName(ComputeReadinessLayer layer) =>
        layer switch
        {
            ComputeReadinessLayer.SystemRuntime => "VC++ runtime",
            ComputeReadinessLayer.CudaRuntime => "CUDA runtime",
            ComputeReadinessLayer.Cudnn => "cuDNN",
            ComputeReadinessLayer.SherpaCudaRuntime => "RSTT CUDA worker",
            ComputeReadinessLayer.WhisperCudaRuntime => "Whisper CUDA worker",
            ComputeReadinessLayer.ProviderLoad => "ONNX CUDA provider",
            ComputeReadinessLayer.ModelCompatibility => "Model compatibility",
            ComputeReadinessLayer.RecognizerLoad => "Recognizer load",
            ComputeReadinessLayer.ActiveInference => "Active inference",
            _ => layer.ToString(),
        };
}
