# RSTT architecture

RSTT uses a bounded, local pipeline. No normal recognition path writes a WAV file or sends audio/text to a network service.

```text
Windows render device
  → NAudio WASAPI loopback capture
  → mono conversion + incremental 16 kHz resampling
  → bounded Channel<AudioChunk>
  → local VAD and sherpa-onnx streaming ASR
  → raw partial/final RecognitionResult
  → TranscriptStabilizer
  ├─ caption view model and non-activating WPF overlay
  └─ new confirmed text only → foreground check → Unicode SendInput
```

## Threading and lifecycle

- The WASAPI callback does only conversion and a non-blocking channel write.
- The audio worker performs model decoding away from the WPF dispatcher.
- The channel drops oldest audio under sustained decoder overload. Live captions are preferable to an ever-growing delayed backlog.
- The model is initialized once and retained across stop/start cycles. Stop cancels capture, drains/ends the worker, resets transient state, and keeps the UI responsive.
- `RecognitionCoordinator` owns listening state; the WPF UI reacts to `Initializing`, `ModelMissing`, `ModelLoading`, `Ready`, `Listening`, `Stopping`, and `Error` snapshots.

## Stability contract

`TranscriptStabilizer` compares consecutive hypotheses, confirms only a completed common prefix, and keeps a committed prefix separately from the pending suffix. A final result flushes remaining pending text. The injected output is therefore append-only and duplicates are prevented in ordinary revision scenarios.

## Data locations

| Data | Location |
| --- | --- |
| Settings | `%LOCALAPPDATA%\Helios\RSTT\settings.json` |
| Models | `%LOCALAPPDATA%\Helios\RSTT\Models` |
| Logs | `%LOCALAPPDATA%\Helios\RSTT\Logs` |

Logs intentionally omit raw audio and full recognised conversation text.
