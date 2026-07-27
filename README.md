# RSTT

Real-Time Speech-to-Text for Windows, created by Helios.

RSTT captures the audio currently playing through Windows, transcribes it with a local speech model, shows live captions, and can type only stable recognised text into the currently focused application.

> RSTT performs speech recognition locally. Audio does not need to be sent to a cloud transcription API.

RSTT has no accounts, API keys, subscriptions, cloud speech APIs, analytics, or automatic transcript uploads. Once a model has been installed, normal recognition works without an internet connection.

## V1 capabilities

- WASAPI loopback capture of the selected Windows output device
- Streaming local sherpa-onnx recognition on CPU
- 16 kHz mono conversion, bounded in-memory buffering, and local voice-activity detection
- Separate stable and pending caption text
- Unicode `SendInput` output to the current foreground app
- Non-activating caption overlay, tray menu, and global hotkeys
- JSON settings and privacy-safe local rolling logs

## Requirements

- Windows 10 or Windows 11, x64
- A normal user account; RSTT intentionally does not request Administrator privileges
- A local streaming sherpa-onnx model installed as described in [docs/MODELS.md](docs/MODELS.md)

RSTT cannot inject text into an elevated application while it runs normally. Captions continue to work in that case.

## Run from source

Install the .NET 8 SDK, then run:

```powershell
dotnet restore RSTT.sln
dotnet run --project src/RSTT.App/RSTT.App.csproj
```

Install a model before starting recognition. If no valid model is found, RSTT remains open and clearly reports the required local directory instead of falling back to a cloud service.

## Hotkeys

| Hotkey | Action |
| --- | --- |
| Ctrl+Alt+R | Start or stop listening |
| Ctrl+Alt+T | Toggle typing into the focused app |
| Ctrl+Alt+C | Toggle the caption overlay |

## Text injection safety

ASR hypotheses change as more audio arrives. RSTT keeps caption updates fast but runs them through `TranscriptStabilizer` before typing text. It sends only a newly confirmed prefix once, never raw partial results. Turning injection on only affects newly stabilised speech; it never dumps existing caption history into the active window.

Read [docs/TEXT_INJECTION.md](docs/TEXT_INJECTION.md) for the Windows behavior and UIPI limitation.

## Project layout

```text
src/RSTT.App             WPF UI, overlay, tray, hotkeys, orchestration
src/RSTT.Core            Domain models, state, transcript stabilizer
src/RSTT.Audio           WASAPI loopback and streaming PCM conversion
src/RSTT.Speech          Local model validation and sherpa-onnx engine
src/RSTT.Input           Foreground-window checks and Unicode SendInput
src/RSTT.Infrastructure  App folders, JSON settings, local logging
tests/                   Transcript-stabilizer tests
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full pipeline.

## Build, test, and publish

```powershell
dotnet build RSTT.sln
dotnet test RSTT.sln
dotnet publish src/RSTT.App/RSTT.App.csproj -c Release -r win-x64 --self-contained true
```

The publish output includes the .NET runtime and sherpa-onnx native runtime dependencies. Models stay external under `%LOCALAPPDATA%\Helios\RSTT\Models` and are not bundled into the executable.

## Troubleshooting

- Select the output device actually playing audio, then restart listening after connecting or disconnecting headphones.
- If RSTT says the model is missing, follow [docs/MODELS.md](docs/MODELS.md) and verify every path named in `model.json`.
- Check `%LOCALAPPDATA%\Helios\RSTT\Logs` for startup, device, model, and injection failures. Raw audio and full transcripts are not logged.
- If text is not typed into an Administrator application, use RSTT without injection or run both applications at the same integrity level. RSTT does not bypass Windows security.

## Licence

RSTT is licensed under the [MIT License](LICENSE). Dependency notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Speech models have their own licences and must be reviewed before distribution.
