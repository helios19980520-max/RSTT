# RSTT

**Real-Time Speech-to-Text for Windows**, created by Helios.

RSTT captures audio playing through a selected Windows output device, transcribes English speech locally, shows live captions, and can type only confirmed text into the focused application.

After the model has been installed, recognition is offline. RSTT has no account, API key, subscription, cloud speech API, analytics, or transcript upload.

## V1 capabilities

- Windows 10/11 x64 WPF desktop application
- WASAPI loopback capture from speakers, headphones, or another render device
- Local CPU inference with sherpa-onnx and Parakeet Unified English
- Live stable/pending captions and a non-activating caption overlay
- Unicode text output to the focused application through Windows `SendInput`
- Global hotkeys, system tray controls, audio metering, and device testing
- Resumable in-app model download with exact-size and SHA-256 verification
- Atomic JSON settings and privacy-safe rolling local logs

## Requirements

- Windows 10 or Windows 11, x64
- A working Windows output device
- .NET 8 SDK only when building from source
- About 1.5 GB of free disk space for the self-contained app, model download, and working headroom
- Internet access once to install the model through RSTT

RSTT intentionally runs as a normal user. Windows blocks normal applications from injecting input into many elevated Administrator windows; captions still work in that case.

## Quick start

From source:

```powershell
dotnet restore RSTT.sln
dotnet run --project src/RSTT.App/RSTT.App.csproj -c Release
```

On first launch:

1. Open **Models** and choose **Download & verify**.
2. Keep RSTT open while the 663 MB (632 MiB) Parakeet model downloads.
3. Select the output device that is playing speech on **Audio**.
4. Use **Test audio** to confirm that the level meter moves.
5. Return to **Dashboard** and select **Start listening**.

Downloads can be cancelled and resumed. A model is not marked ready until every artifact passes its expected size and SHA-256 check and the final manifest is written.

## Included model profile

V1 deliberately has one supported profile:

| Profile | Engine | Language | Download | Buffered latency |
| --- | --- | --- | ---: | ---: |
| Parakeet Unified English, INT8 | sherpa-onnx online transducer, CPU | English | 663,048,980 bytes | approximately 1.12 s |

The model is downloaded on demand from the sherpa-onnx maintainer's converted model repository and remains external to the app. It is derived from NVIDIA Parakeet Unified English and is governed by the NVIDIA Open Model License. See [Model management](docs/MODELS.md) and [Third-party notices](THIRD_PARTY_NOTICES.md).

## Audio and recognition behavior

RSTT listens to the selected **output**, not the microphone. The capture callback converts the device mix to mono 16 kHz floating-point samples, publishes a real level meter, and writes to a bounded in-memory channel. A background worker feeds sherpa-onnx. If decoding falls behind, audio is dropped instead of allowing delay and memory usage to grow without limit.

Parakeet uses buffered streaming, so captions intentionally trail the audio by roughly the selected model context plus processing time. CPU speed, competing workloads, device format, speech clarity, noise, accent, and source quality affect latency and accuracy.

Raw audio is not written to disk. Full recognized conversations are not written to the application log.

## Captions, stability, and typing

ASR hypotheses can revise themselves as more audio arrives. RSTT keeps pending caption text visually separate, confirms only a stable prefix, and types each newly confirmed segment once. Enabling typing does not paste existing caption history.

The caption overlay uses `WS_EX_NOACTIVATE` and does not take focus. Text injection:

- targets the foreground application;
- refuses to type into RSTT itself;
- emits UTF-16 Unicode input without using the clipboard;
- serializes and paces character delivery for Win32 and WinUI text controls; and
- stops if the foreground process changes during a segment.

See [Text injection](docs/TEXT_INJECTION.md) for focus, UIPI, and security limitations.

## Hotkeys

| Hotkey | Action |
| --- | --- |
| `Ctrl+Alt+R` | Start or stop listening |
| `Ctrl+Alt+T` | Toggle typing into the focused app |
| `Ctrl+Alt+C` | Toggle the caption overlay |

Windows may reject a hotkey if another application has already registered it. RSTT reports that condition and continues to work through the UI and tray menu.

## Local data

| Data | Location |
| --- | --- |
| Settings | `%LOCALAPPDATA%\Helios\RSTT\settings.json` |
| Model | `%LOCALAPPDATA%\Helios\RSTT\Models\parakeet-unified-en-0.6b-int8-streaming-1120ms` |
| Logs | `%LOCALAPPDATA%\Helios\RSTT\Logs` |

Deleting the model in RSTT removes only its managed model directory. Settings and logs remain.

## Project layout

```text
src/RSTT.App             WPF shell, overlay, tray, hotkeys, orchestration
src/RSTT.Core            Contracts, settings, state, transcript stability
src/RSTT.Audio           WASAPI loopback, PCM conversion, level metering
src/RSTT.Speech          Model manager and sherpa-onnx recognition engine
src/RSTT.Input           Foreground checks and Unicode SendInput
src/RSTT.Infrastructure  App paths, atomic JSON settings, local logging
tests/                   Unit and live Windows integration tests
```

Read [Architecture](docs/ARCHITECTURE.md) for pipeline and lifecycle details.

## Build, test, and publish

```powershell
dotnet restore RSTT.sln
dotnet build RSTT.sln -c Release --no-restore
dotnet test RSTT.sln -c Release --no-restore
dotnet publish src/RSTT.App/RSTT.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\publish\win-x64-verified
```

The publish is self-contained and multi-file. The executable is `artifacts\publish\win-x64-verified\RSTT.App.exe`. The model is never included in publish output.

The Windows integration tests use the active output device and open an isolated temporary document in Notepad. Close visible Notepad windows first. The fixture refuses to send input if an elevated or always-on-top application prevents its temporary editor from becoming the real foreground target.

## Troubleshooting

- **No meter movement:** select the render device that is actually playing audio. Bluetooth/headphone changes can create a different endpoint; stop and restart listening after changing devices.
- **Model unavailable:** open **Models**, resume or retry the download, and keep enough disk space available. RSTT rejects incomplete files and same-size files with an unexpected SHA-256 during installation.
- **Captions lag:** Parakeet's configured buffered latency is about 1.12 seconds before CPU decoding overhead. Reduce other CPU-heavy work.
- **No typed text:** enable **Type into focused app**, focus an editable control, and keep the destination focused while a stable segment is emitted.
- **Administrator target:** run both applications at the same integrity level or use captions only. RSTT does not bypass UIPI.
- **Protected or silent playback:** some protected content or device/driver combinations may not expose samples through loopback.
- **Diagnostics:** inspect `%LOCALAPPDATA%\Helios\RSTT\Logs`. Logs contain operational metadata, not raw audio or complete transcripts.

## Licence

RSTT source code is licensed under the [MIT License](LICENSE). Dependencies and the separately downloaded model have their own terms listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
