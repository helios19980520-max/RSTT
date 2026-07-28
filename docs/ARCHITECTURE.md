# RSTT architecture

RSTT is a Windows x64, local-first WPF application. Its recognition path is input-driven and bounded from the Windows audio callback through the UI and text-injection sinks.

```text
Windows render endpoint
  → NAudio WASAPI callback
  → ArrayPool-backed packet copy + TryWrite
  → bounded raw-packet channel (32)
  → downmix + direct incremental resample to 16 kHz mono
  → bounded normalized-audio channel (48)
  → input-driven sherpa-onnx engine
  → bounded ordered result channel (128)
  ├─ TranscriptStabilizer + CaptionHistory
  │    → final updates immediately
  │    → partial updates coalesced to 20 Hz
  └─ bounded ordered injection channel (128)
       → foreground/UIPI checks
       → paced UTF-16 SendInput
```

## Assembly ownership

| Project | Responsibility |
| --- | --- |
| `RSTT.Core` | Contracts, settings, model/compute metadata, state machine, transcript policies, caption history |
| `RSTT.Audio` | Device enumeration, WASAPI capture, pooled packet transport, conversion, metering |
| `RSTT.Speech` | Embedded catalog, managed download/install lifecycle, sherpa-onnx resources |
| `RSTT.Input` | Foreground safety and Win32 Unicode `SendInput` |
| `RSTT.Infrastructure` | App paths, atomic settings, compute probes, performance monitor, rolling logs |
| `RSTT.App` | WPF shell, view model, coordinator, caption window, tray, and hotkeys |

Core is UI-independent. Native object lifetime remains inside Speech, and WPF objects remain inside App.

## Callback and backpressure rules

The WASAPI callback may copy the incoming packet into an `ArrayPool<byte>` buffer and perform a non-blocking `TryWrite`. It may not resample, allocate large intermediate arrays, decode, write per-packet logs, call WPF, or wait for a consumer.

One worker owns downmix/resample/RMS work. The meter is capped at 25 Hz. When a bounded stage cannot accept more input, RSTT drops bounded work and records duration/age rather than growing latency and memory indefinitely.

The ASR worker naturally sleeps in `await foreach` while no normalized chunks exist. `IsReady`/`Decode` loops run only after genuinely new audio is accepted or during one deliberate endpoint flush.

## Recognition cadence and model profiles

`StreamingRecognitionProfile` records chunk/lookahead/expected latency, cache-aware versus buffered behavior, and recommended threads. The recommended 560 ms Nemotron profile is cache-aware. Parakeet's 1120 ms profile is buffered and materially more CPU-intensive on the test machine.

The engine accepts normalized audio incrementally and asks sherpa-onnx to decode only while the stream reports work ready. Decode time and the audio duration credited since the previous decode are recorded. RTF is the rolling ratio of decoder time to input-audio time, including terminal flush work.

## Session lifecycle

`RecognitionCoordinator` serializes start, stop, reload, and unload operations and validates transitions through `TranscriptionSessionStateMachine`.

1. A start resets transcript/session state and creates a new generation ID.
2. The engine stream starts before capture.
3. The capture, result, injection, and UI workers belong to that generation.
4. Every result and injection request checks the current generation.
5. Stop first stops accepting capture, awaits audio, deliberately finalizes the stream, drains results/injection, publishes the last UI value, cancels the UI timer, and clears transient queues.
6. The loaded recognizer remains warm for the next start.
7. A model reload happens only after the active session has stopped.

State access is protected separately from the asynchronous lifecycle semaphore so a faulting worker cannot race a user stop transition.

## Health diagnostics and recovery

The performance monitor exposes process CPU, working set, managed heap/GC, thread count, callback rate, queue depth/duration/age, dropped audio, decode P50/P95/max, RTF, result/UI/injection rates, provider, model, and device.

The lightweight health check runs once per second. If fresh audio and a speech-level signal continue but no recognition result arrives for five seconds, it writes one rate-limited diagnostic with queue, decode, CPU, provider, and model context.

If a native recognition call throws, a session permits exactly one controlled stream reset. The result worker clears only revisable hypothesis/partial-caption state while preserving finalized session history. A second failure crosses the session boundary, stops recognition, and surfaces an error; there is no restart loop.

## Transcript and caption contract

One result worker owns hypothesis ordering. `ITranscriptCommitPolicy` separates live visual hypotheses from irreversible injection policy. Production uses `FinalOnlyCommitPolicy`: partials update the dashboard/overlay, while only endpoint-final text enters the injection channel. The dashboard session preview keeps the most recent 6,000 characters so WPF layout/allocation cost cannot grow for the entire lifetime of a long session.

`CaptionHistory` stores at most 100 finalized segments and one current partial. The visible overlay further limits rendered segments to its configured line count. Updating a partial replaces the prior partial.

## Model installation boundary

`LocalModelManager` is the only runtime component with a network path. It:

- reads an embedded, versioned catalog;
- stages artifacts under `Models\.downloads\<model-id>`;
- resumes with a validated HTTP Range response;
- uses `ResponseHeadersRead` and a pooled 256 KiB buffer;
- throttles progress publication to roughly 13 Hz;
- verifies exact bytes and SHA-256;
- checks disk headroom and safe relative paths;
- writes the manifest last; and
- promotes the completed directory without exposing a partially valid model.

The active recognizer is unloaded before active files are deleted or replaced.

## Focus and local-data boundaries

The caption overlay is `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, uses `ShowActivated=false`, and does not use activation toggling tricks. `SendInput` resolves the current foreground process immediately before delivery and rechecks it every 16 UTF-16 code units.

Raw audio, partial hypotheses, and caption objects are transient. Settings are atomically replaced. Logs contain operational metadata but not raw audio or full conversations.
