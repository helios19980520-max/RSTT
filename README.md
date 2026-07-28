# RSTT

**RSTT — Real-Time Speech-to-Text** is a local-first Windows desktop captioning and text-injection utility by Helios.

RSTT captures the selected Windows playback device with WASAPI loopback, recognizes speech locally with sherpa-onnx, displays a non-activating caption overlay, and can type finalized text into the focused application. Recognition does not require an account, API key, cloud speech service, analytics endpoint, or transcript upload.

## Current capabilities

- Windows 10/11 x64 WPF application on .NET 8
- Input-driven, bounded WASAPI → resample → ASR pipeline
- Cache-aware Nemotron Streaming English as the recommended realtime model
- Available Nemotron 3.5 multilingual and Parakeet Unified alternatives
- Actionable Qwen3-ASR 0.6B INT8 and Whisper Large v3 Turbo Preview profiles
- Isolated versioned sherpa and whisper.cpp CPU workers
- Optional app-local CUDA 12 Accelerator Pack for sherpa and Whisper
- Resumable, verified in-app downloads with live progress, rate, and ETA
- Automatic compute probing with honest CPU fallback
- Movable, resizable, persistent, no-activate Caption V2 overlay
- Real `RegisterHotKey` shortcuts with editable bindings and conflict reporting
- Ordered, final-only Unicode `SendInput` delivery to the current foreground app
- Local performance telemetry, stall diagnostics, and one controlled stream recovery

## Requirements

- Windows 10 or Windows 11, x64
- A working Windows playback device
- Approximately 2–6 GB free for the app, selected models, and download headroom
- Internet access only when downloading a model
- .NET 8 SDK only when building from source

RSTT runs as a normal user. Windows UIPI can block a normal application from typing into elevated Administrator windows; captions remain available.

## Quick start

```powershell
dotnet restore RSTT.sln
dotnet run --project src\RSTT.App\RSTT.App.csproj -c Release
```

On first launch:

1. Open **Models** and install a model. Nemotron Streaming English is the recommended default.
2. Open **Audio**, choose the playback device carrying speech, and use **Test audio**.
3. Return to **Dashboard** and choose **Start listening**.
4. Enable captions and, if wanted, **Type stable text into the focused app**.

Downloads are resumable. RSTT does not mark a model ready until every required artifact passes exact-size and SHA-256 validation and the manifest is promoted into place.

## Models

| Model | Integration | Language | Streaming | Profile |
| --- | --- | --- | --- | --- |
| Nemotron Streaming English 0.6B INT8 | Available, recommended | English | Native/cache-aware | Balanced, 560 ms |
| Nemotron 3.5 Streaming Multilingual 0.6B INT8 | Available | 19 transcription-ready locales | Native/cache-aware | Balanced, 560 ms |
| Parakeet Unified English 0.6B INT8 | Available | English | Buffered | Accurate, 1120 ms |
| Qwen3-ASR 0.6B INT8 | Preview, actionable | 30 languages + 22 Chinese dialects | Segmented realtime | Silero VAD 200/500 ms |
| Whisper Large v3 Turbo Q5_0 | Preview, actionable | Multilingual auto/manual | Segmented realtime | Default Whisper profile |
| Whisper Large v3 Turbo full | Preview, actionable | Multilingual auto/manual | Segmented realtime | Maximum quality |

Qwen and both Whisper profiles have pinned download validation and real CPU/RTX
2060 CUDA decode evidence. They remain Preview until the full multilingual,
accented, quiet, and no-trailing-silence corpus gate passes. Coming-later
entries have no download or activation command. See [Model
management](docs/MODELS.md) and the [verified model
matrix](docs/MODEL_MATRIX.md).

## Compute backends

The standard RSTT package contains isolated sherpa and Whisper CPU workers, so
it launches without CUDA dependencies.

The optional Accelerator Pack installs versioned app-local sherpa CUDA
12.8/cuDNN 9.24 and Whisper CUDA 12 workers. Auto verifies handshake, provider,
model load, and warmup before selection, terminates a failed CUDA process before
CPU fallback, and displays `CUDA Active` only after real session inference.
RTX 2060 CPU/CUDA measurements are recorded in the documentation.

See [Compute backends](docs/COMPUTE_BACKENDS.md) for packaging and fallback details.

## Captions and typing

Caption V2 keeps bounded finalized segments plus one replaceable partial. It follows the latest caption until the user scrolls up, then offers **Jump to latest**. The overlay can be unlocked, dragged, resized, placed on another display, and restored after restart. Stored bounds are clamped to the current virtual desktop.

The overlay uses `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` and `ShowActivated=false`. Normal caption updates do not steal keyboard focus.

Typing is intentionally conservative: online models commit stable whole-word
prefixes with final-tail flush, while offline/VAD models commit final segments.
Automatic delivery uses fast 64-unit blocks generally and the measured
single-unit/20 ms compatibility profile only for packaged Windows 11 Notepad.
The exact HWND is rechecked around every block.

See [Caption behavior](docs/CAPTIONS.md) and [Text injection](docs/TEXT_INJECTION.md).

## Global shortcuts

Defaults:

| Shortcut | Action |
| --- | --- |
| `Ctrl+Alt+R` | Start/stop listening |
| `Ctrl+Alt+T` | Toggle text injection immediately |
| `Ctrl+Alt+C` | Show/hide captions without stopping recognition |

Shortcuts are editable in Settings. RSTT registers a replacement before releasing the old shortcut; a rejected or conflicting edit therefore cannot silently remove the working binding. Windows may already own a default combination—in that case RSTT reports the conflict and the user can enter another modifier-plus-key shortcut.

See [Global hotkeys](docs/HOTKEYS.md).

## Runtime architecture

```text
WASAPI callback
  → bounded raw-packet channel
  → one downmix/resample/meter worker
  → bounded normalized-audio channel
  → one descriptor-selected isolated sherpa or whisper.cpp worker
  → bounded ordered result channel
  ├─ coalesced WPF/caption publication (20 Hz maximum for partials)
  └─ bounded isolated SendInput worker
```

The WASAPI callback copies into pooled memory and returns; it does not resample, decode, log per packet, invoke WPF, or wait on downstream work. Queue depth and age are bounded. Stop/start creates a new generation and stale results are ignored.

See [Architecture](docs/ARCHITECTURE.md) and [Performance](docs/PERFORMANCE.md).

## Performance troubleshooting

The recommended model should maintain realtime factor (RTF) below `1.0` on supported hardware. If RTF is above `1.0`:

- use Nemotron Streaming English rather than buffered Parakeet;
- choose the Fast/Balanced profile supported by the installed model;
- leave the CPU thread limit on Auto unless profiling supports a change;
- use a verified GPU package when one is actually available;
- close competing CPU-heavy inference or media-processing workloads.

Do not raise the whole process to Windows Realtime priority. Correct callback and queue design protects playback without risking system responsiveness.

Operational diagnostics are in `%LOCALAPPDATA%\Helios\RSTT\Logs`. Logs include lifecycle, backend, model, queue, decode, and sanitized error data—not raw audio or complete transcript text.

## Local data

| Data | Location |
| --- | --- |
| Settings | `%LOCALAPPDATA%\Helios\RSTT\settings.json` |
| Models | `%LOCALAPPDATA%\Helios\RSTT\Models` |
| Resumable staging | `%LOCALAPPDATA%\Helios\RSTT\Models\.downloads` |
| Logs | `%LOCALAPPDATA%\Helios\RSTT\Logs` |

Model artifacts are not bundled in the repository or publish output.

## Build, test, and publish

```powershell
dotnet restore RSTT.sln
dotnet build RSTT.sln -c Release --no-restore
dotnet test RSTT.sln -c Release --no-restore
dotnet publish src\RSTT.App\RSTT.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishProfile=win-x64
.\scripts\Build-AcceleratorPack.ps1 -Configuration Release -SherpaArchivePath C:\path\to\sherpa-cuda-1.13.4.tar.bz2
```

The publish is self-contained and multi-file. Its entry point is:

```text
artifacts\publish\win-x64\RSTT.App.exe
artifacts\accelerator-pack\RSTT-Accelerator-Pack-1.0.0-win-x64.zip
```

The Windows text-injection integration fixture is non-destructive: it always
uses a uniquely named temporary document and a fresh Notepad HWND. It never
types into or closes a user document.

## Privacy and licensing

Raw audio is transient and is never written to disk. Session transcript history is bounded in memory and is not saved automatically. Internet access is limited to explicit model installation.

RSTT source is licensed under the [MIT License](LICENSE). Dependencies and separately downloaded models retain their own terms; see [Third-party notices](THIRD_PARTY_NOTICES.md).
