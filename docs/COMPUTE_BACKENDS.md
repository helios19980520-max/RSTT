# Compute backends

RSTT reports hardware, dependencies, worker handshake, provider, model load,
warmup, and active inference as separate facts. GPU hardware alone never
produces a `CUDA Active` label.

## Runtime layout

Exactly one native ASR worker and one recognizer are active:

| Package | Worker | Runtime |
| --- | --- | --- |
| Base CPU application | `workers/sherpa-cpu/1.13.4` | sherpa-onnx 1.13.4 CPU |
| Base CPU application | `workers/whisper-cpu/1.9.1` | Whisper.net / whisper.cpp CPU |
| Accelerator Pack | `workers/sherpa-cuda12/1.13.4` | sherpa 1.13.4, ONNX CUDA provider, CUDA 12.8, cuDNN 9.24 |
| Accelerator Pack | `workers/whisper-cuda12/1.9.1` | Whisper.net 1.9.1 CUDA 12 |

Workers communicate through a versioned length-prefixed named pipe with typed
handshake, load, warmup, start, little-endian float32 audio, finish, unload,
shutdown, ping, hypothesis, performance, readiness, and fault messages.

The base package does not load sherpa or Whisper native DLLs in the WPF process.
CUDA and CPU runtimes may therefore coexist without DLL-name collisions.

## Selection policy

| Requested setting | Behavior |
| --- | --- |
| CPU | Start only the model-appropriate CPU worker |
| CUDA | Require the model-appropriate CUDA worker; surface failure |
| Auto | Try CUDA handshake/load/warmup, fully terminate it on failure, then start CPU |

Warmup proves that the provider and model can execute, but the Live UI reports
`CUDA Active` only after active-session audio has actually decoded.

## RTX 2060 evidence

The validation machine has an RTX 2060 (6,144 MiB VRAM), driver 595.71, and
compute capability 7.5. CUDA 12.8 and 13.3 coexist. RSTT uses only its private
CUDA 12.8/cuDNN 9.24 files for sherpa; users do not replace DLLs manually.

Real packaged CUDA decode passed for:

- Nemotron Streaming English 0.6B;
- Qwen3-ASR 0.6B INT8;
- Whisper Large v3 Turbo Q5_0;
- Whisper Large v3 Turbo full.

See [Performance](PERFORMANCE.md) and [Current issues](CURRENT_ISSUES.md) for
the measured RTF and memory values. On this GPU, CUDA is a major improvement
for Whisper, modest for Nemotron, and slower than CPU for the short Qwen test
clip. Backend labels therefore describe execution, not a speed promise.

## Build and install the Accelerator Pack

Prerequisites for building the pack are CUDA Toolkit 12.8, cuDNN 9.x, and the
pinned official sherpa 1.13.4 CUDA archive. The script validates the archive
SHA-256, publishes both workers, copies only required redistributable DLLs,
removes the irrelevant Linux Whisper runtime, collects notices, and creates a
per-file SHA-256 manifest plus Zip64 archive.

```powershell
.\scripts\Build-AcceleratorPack.ps1 `
  -Configuration Release `
  -SherpaArchivePath C:\path\to\sherpa-onnx-v1.13.4-cuda-12.x-cudnn-9.x-win-x64-cuda.tar.bz2
```

Install into a published RSTT directory:

```powershell
Expand-Archive .\RSTT-Accelerator-Pack-1.0.0-win-x64.zip .\rstt-pack
& .\rstt-pack\RSTT-Accelerator-Pack-1.0.0-win-x64\Install-AcceleratorPack.ps1 `
  -AppDirectory C:\path\to\RSTT
```

The installer validates every file before copying, stages each worker, retains
an existing same-version worker as a timestamped recovery directory, and never
writes to the global CUDA installation.

## Diagnostics and remediation

Settings → Diagnostics shows:

1. System and Microsoft VC++ runtime;
2. physical GPU, vendor/device ID, dedicated memory;
3. NVIDIA driver;
4. CUDA runtime;
5. cuDNN;
6. sherpa and Whisper native workers;
7. provider handshake;
8. model compatibility and recognizer load;
9. warmup;
10. active inference and fallback reason.

The self-test launches the selected isolated worker and decodes a pinned
public-domain speech asset. Copied diagnostics contain no recognized words or
audio.

Official remediation links:

- [NVIDIA driver download](https://www.nvidia.com/Download/index.aspx)
- [CUDA Toolkit archive](https://developer.nvidia.com/cuda-toolkit-archive)
- [cuDNN downloads](https://developer.nvidia.com/cudnn-downloads)
- [Microsoft Visual C++ x64 runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe)
- [sherpa Windows CUDA guidance](https://k2-fsa.github.io/sherpa/onnx/install/windows/build-cuda.html)
- [ONNX Runtime CUDA requirements](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html)

## Licensing boundary

The pack includes RSTT's license, third-party notices, the CUDA EULA, and the
installed cuDNN license. CUDA Attachment A identifies `cudart`, cuBLAS, cuFFT,
and NVRTC runtime DLLs as redistributable, while the cuDNN supplement identifies
runtime `.dll` files as distributable with an application. Public distribution
still requires the release owner to accept and comply with those terms.

AMD/Intel GPU providers, DirectML, WinML, ROCm, and OpenVINO remain Planned.
Those adapters use the CPU worker in this release.
