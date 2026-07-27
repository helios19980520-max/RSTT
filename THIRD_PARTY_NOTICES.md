# Third-party notices

RSTT source and publish output do not include a speech model. The user explicitly downloads the model into local application data through the Models page.

## Direct software dependencies

| Component | Version | Licence | Project |
| --- | --- | --- | --- |
| NAudio | 2.3.0 | MIT | https://github.com/naudio/NAudio |
| sherpa-onnx | 1.13.4 | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging | 8.0.1 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging.Abstractions | 8.0.2 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging.Debug | 8.0.1 | MIT | https://github.com/dotnet/runtime |
| xUnit.net | 2.9.3 | Apache-2.0 | https://xunit.net/ |
| xunit.runner.visualstudio | 3.0.2 | Apache-2.0 | https://github.com/xunit/visualstudio.xunit |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT | https://github.com/microsoft/vstest |
| coverlet.collector | 6.0.2 | MIT | https://github.com/coverlet-coverage/coverlet |

The .NET self-contained publish includes Microsoft .NET runtime components under their applicable Microsoft licence terms. NuGet packages also carry transitive dependencies; a distributor should preserve the licence files included in publish/package outputs and complete its own release audit.

## Downloaded speech model

| Item | Source | Terms |
| --- | --- | --- |
| Parakeet Unified English 0.6B | https://huggingface.co/nvidia/parakeet-unified-en-0.6b | NVIDIA Open Model License Agreement |
| sherpa-onnx INT8 ONNX export, 1.12 s buffered streaming | https://huggingface.co/csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-streaming-1120ms | Export of the NVIDIA model for sherpa-onnx; underlying model terms continue to apply |

Model notice:

> Licensed by NVIDIA Corporation under the NVIDIA Open Model License.

The NVIDIA model card describes the model as ready for commercial and non-commercial use, subject to the governing licence. Review the current [NVIDIA Open Model License Agreement](https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/) and model card before use, modification, or redistribution.

The model was trained on third-party datasets listed in NVIDIA's model card. Model output can be inaccurate or biased and must not be treated as authoritative. RSTT provides transcription software, not a warranty of model fitness for a particular purpose.
