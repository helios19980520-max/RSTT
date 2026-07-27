# RSTT architecture

RSTT is a Windows x64, local-first WPF application. Its normal recognition path does not write WAV files and does not call a network transcription service.

```text
Windows render endpoint
  → NAudio WASAPI loopback callback
  → channel mix + incremental resample to 16 kHz mono float
  → real audio-level event + bounded Channel<AudioChunk>
  → background sherpa-onnx online recognizer
  → partial/final RecognitionResult
  → ordered TranscriptStabilizer
  ├─ WPF dashboard + no-activate caption overlay
  └─ newly stable text only
       → foreground/UIPI checks
       → paced UTF-16 SendInput
```

## Assembly boundaries

| Project | Responsibility |
| --- | --- |
| `RSTT.Core` | Contracts, settings, state snapshots, domain records, transcript stability |
| `RSTT.Audio` | Device enumeration, WASAPI loopback, format conversion, meter events, bounded audio transport |
| `RSTT.Speech` | Managed model catalogue/download/validation and sherpa-onnx stream lifecycle |
| `RSTT.Input` | Foreground-process safety and x64 Windows Unicode input |
| `RSTT.Infrastructure` | Local folders, atomic JSON settings, rolling diagnostics |
| `RSTT.App` | WPF presentation, dependency composition, coordinator, overlay, tray, and hotkeys |

Core has no WPF dependency. Platform projects depend on Core contracts, and App composes them.

## Threading and backpressure

- The WASAPI callback performs conversion, level calculation, and a non-blocking channel write. It never waits for inference.
- The channel is bounded. A full channel drops audio and records diagnostics so latency cannot grow without limit.
- sherpa-onnx model construction and decode work run away from the WPF dispatcher.
- Recognition events are serialized before stabilization, caption publication, and text injection. This preserves hypothesis order.
- Settings writes use a semaphore and replace a temporary JSON file atomically.
- UI-bound events are dispatched back to the WPF dispatcher by the view model.

## Recognition stream lifecycle

The selected verified model is loaded once and retained across ordinary stop/start cycles. Each listening session owns an online stream.

1. Audio samples are accepted with their 16 kHz sample rate.
2. The engine decodes while sherpa-onnx reports readiness.
3. Endpointed or final streams emit a final result.
4. A stream that receives `InputFinished()` is disposed and recreated; terminal streams are never reset and reused.
5. Stop finalizes the active stream, drains the worker, clears transient transcript state, and leaves the model loaded.
6. Deleting or changing a model unloads the native recognizer before the managed files are changed.

Parakeet Unified selects sherpa-onnx's buffered RNN-T streaming path from model metadata. The installed profile uses a 128-bin feature configuration and an approximate 1.12-second buffered latency.

## Transcript stability contract

`TranscriptStabilizer` compares ordered hypotheses and keeps committed and pending text separately. It confirms only a completed common prefix across revisions. A final result flushes the remaining suffix.

The caption path sees both committed and pending text. The injection path sees only `NewlyStableText`; it does not receive raw partial hypotheses or previously committed history. This keeps normal output append-only and prevents ordinary ASR revisions from duplicating typed text.

## State and recovery

`RecognitionCoordinator` owns application states such as initialization, missing/downloading/loading model, ready, listening, stopping, and error. Missing or invalid model files keep the desktop shell usable: audio testing, model repair, settings, logs, and About remain available.

Expected failures—missing model, download cancellation, device initialization, hotkey collision, UIPI denial—are surfaced as recoverable UI state or diagnostics. They do not silently switch to a cloud service.

## Model installation boundary

`LocalModelManager` owns the single V1 catalogue entry and its exact artifact metadata. It:

- downloads through HTTPS into `.partial` files;
- resumes with HTTP Range when supported;
- verifies exact length and SHA-256 before promotion;
- writes `model.json` only after every artifact is valid;
- checks manifest identity, safe relative paths, existence, and exact lengths at startup; and
- serializes download/delete operations.

Recognition makes no network requests. The model manager is the only runtime component with a network path.

## Focus and Windows integrity

The caption overlay is a `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` window and never calls `Activate()`. Unicode input first rejects a missing foreground window and RSTT's own process. It then checks that the same process remains foreground while the segment is emitted. Windows UIPI remains authoritative; RSTT does not elevate or bypass it.

## Local data

| Data | Location | Content policy |
| --- | --- | --- |
| Settings | `%LOCALAPPDATA%\Helios\RSTT\settings.json` | User preferences; atomic replacement |
| Models | `%LOCALAPPDATA%\Helios\RSTT\Models` | ONNX and token artifacts plus generated manifest |
| Logs | `%LOCALAPPDATA%\Helios\RSTT\Logs` | Operational metadata; no raw audio or complete transcript |

Audio chunks and ASR hypotheses are transient in-memory data.
