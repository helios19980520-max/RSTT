# Compute backends

RSTT separates **hardware presence**, **runtime availability**, **model compatibility**, and **actual provider selection**. A GPU badge alone is never treated as successful acceleration.

## CPU

CPU is the mandatory baseline and is present in the standard package through NuGet `org.k2fsa.sherpa.onnx` 1.13.4. It requires no CUDA DLL merely to launch.

Thread selection is model/profile-aware:

- Nemotron 560 ms recommends one CPU thread;
- Parakeet 1120 ms recommends two;
- an explicit user limit is clamped to a safe range;
- CUDA, when genuinely available, does not create a second complete recognizer.

## CUDA

The current model artifacts are CUDA-capable, but the standard NuGet package is a CPU runtime. RSTT therefore reports an NVIDIA adapter without advertising CUDA as available.

A verified CUDA distribution requires:

- the sherpa-onnx build matching the app version;
- a matching ONNX Runtime CUDA execution provider;
- CUDA 12.x dependencies for the investigated sherpa 1.13.4 Windows build;
- cuDNN 9.x;
- all dependent DLLs discoverable at process start; and
- an actual recognizer warmup/inference probe.

The official sherpa 1.13.4 CUDA Windows archive was inspected during this pass. Its native library imports `cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll`, `cufft64_11.dll`, and `cudnn64_9.dll`. Those prerequisites were not installed on the test PC. Bundling the provider plus redistributable CUDA/cuDNN components would add a substantial separate package and requires its own redistribution/license review.

Consequently, this pass does **not** claim GPU execution. The test PC's RTX 2060 was detected, but actual ASR provider logs correctly read `cpu`.

## Selection policy

| Requested setting | Validated CUDA available and model-compatible | Result |
| --- | --- | --- |
| CPU | Either | CPU |
| Auto | Yes | CUDA |
| Auto | No | CPU with explicit reason |
| CUDA | Yes | CUDA |
| CUDA | No | CPU fallback with explicit reason |

The selection result records the request, actual backend, fallback flag, reason, and device. The speech engine uses only the selected provider string when creating the recognizer and logs the actual provider, model, thread count, profile, and reason.

## Startup probe

`WindowsComputeDeviceService` always publishes a CPU probe. It enumerates Windows display adapters to distinguish no NVIDIA hardware from NVIDIA hardware with an unavailable runtime. Probe results are cached for the process lifetime and displayed in Settings.

Future GPU packaging must extend this probe with a real provider load/warmup check before `IsAvailable=true`. File presence alone is insufficient.

## AMD and Intel

No DirectML, ROCm, OpenVINO, or other accelerator provider is shipped. AMD/Intel adapter presence therefore does not create an activatable backend. CPU remains the fallback until a native engine is integrated and measured.

## Packaging strategy

Keep CPU and GPU runtime packages separable:

- the CPU build stays small and portable;
- a CUDA build must carry/declare its exact prerequisites;
- both use the same model/catalog/session architecture;
- model downloads are not duplicated;
- only one complete recognizer is active at a time.

References:

- [sherpa-onnx Windows CUDA build guidance](https://k2-fsa.github.io/sherpa/onnx/install/windows/build-cuda.html)
- [ONNX Runtime installation matrix](https://onnxruntime.ai/docs/install/)
- [ONNX Runtime CUDA execution provider requirements](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html)
