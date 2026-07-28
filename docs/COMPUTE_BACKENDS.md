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

## Detection and diagnostics

`WindowsHardwareDetectionService` enumerates DXGI 1.1 adapters, filters software
adapters, and reports vendor/device IDs plus dedicated/shared memory. NVIDIA
command-line tooling is supplemental evidence only.

`WindowsComputeDeviceService` reports each layer independently:

1. hardware;
2. driver;
3. CUDA runtime;
4. cuDNN;
5. sherpa CUDA runtime;
6. ONNX provider load;
7. model compatibility;
8. recognizer load;
9. warmup; and
10. active-session inference.

The Settings diagnostics self-test includes those layers, ASR, audio, and
performance data. “Copy Diagnostics” excludes transcript content.

File presence is only dependency evidence. `CUDA Active` is not shown until an
actual decode has occurred with provider `cuda`.

## AMD and Intel

No DirectML, ROCm, OpenVINO, or other accelerator provider is shipped. AMD/Intel adapter presence therefore does not create an activatable backend. CPU remains the fallback until a native engine is integrated and measured.

## Packaging strategy and current boundary

Keep CPU and GPU runtime packages separable:

- the CPU build stays small and portable;
- a CUDA build must carry/declare its exact prerequisites;
- both use the same model/catalog/session architecture;
- model downloads are not duplicated;
- only one complete recognizer is active at a time.

The base CPU package is implemented and verified. The versioned named-pipe
worker and CUDA Accelerator Pack are not yet shipped in this repository because
the matching CUDA 12.x/cuDNN 9.x/provider bundle and redistribution review are
not complete. Consequently Auto and explicit CUDA safely select CPU with a
layer-specific reason on the current machine; this document does not claim CUDA
inference.

References:

- [sherpa-onnx Windows CUDA build guidance](https://k2-fsa.github.io/sherpa/onnx/install/windows/build-cuda.html)
- [ONNX Runtime installation matrix](https://onnxruntime.ai/docs/install/)
- [ONNX Runtime CUDA execution provider requirements](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html)
