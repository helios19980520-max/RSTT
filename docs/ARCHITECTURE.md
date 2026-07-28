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
  ├─ model-aware ITranscriptCommitPolicy + CaptionHistory
  │    → typed snapshot and zero-or-more TranscriptCommit values
  │    → final updates immediately; partial UI coalesced to 20 Hz
  └─ bounded ordered injection channel (128)
       → exact-HWND/UIPI checks
       → bounded single-consumer injection channel (64)
       → Direct 64-unit blocks or measured Notepad compatibility pacing
```

## Assembly ownership

| Project | Responsibility |
| --- | --- |
| `RSTT.Core` | Contracts, settings, model/compute metadata, state machine, transcript policies, caption history |
| `RSTT.Audio` | Device enumeration, WASAPI capture, pooled packet transport, conversion, metering |
| `RSTT.Speech` | Embedded catalog, managed download/install lifecycle, sherpa-onnx resources |
| `RSTT.Whisper.Worker` | Versioned isolated whisper.cpp CPU worker |
| `RSTT.Whisper.Cuda12.Worker` | Optional isolated whisper.cpp CUDA 12 worker |
| `RSTT.Input` | Foreground safety and Win32 Unicode `SendInput` |
| `RSTT.Infrastructure` | App paths, atomic settings, compute probes, performance monitor, rolling logs |
| `RSTT.App` | WPF shell, view model, coordinator, caption window, tray, and hotkeys |

Core is UI-independent. Sherpa native lifetime currently remains inside Speech.
Whisper native state exists only in its selected worker process. WPF objects
remain inside App.

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
4. Every audio chunk, result, caption, commit, and injection request carries and
   checks the current generation.
5. Stop enters `Completing` while final commits are still accepted, stops
   capture, drains conversion/normalized audio, drains that audio into the
   recognizer, calls the real `OnlineStream.InputFinished()`, decodes and emits
   the terminal result, drains result/commit/injection workers, and only then
   marks the generation stopped.
6. A five-second overall deadline hard-cancels only the incomplete stage and
   records its exact name.
7. The loaded recognizer remains warm for the next start.
8. A model reload happens only after the active session has stopped.

State access is protected separately from the asynchronous lifecycle semaphore so a faulting worker cannot race a user stop transition.

## Health diagnostics and recovery

The performance monitor exposes process CPU, working set, managed heap/GC, thread count, callback rate, queue depth/duration/age, dropped audio, decode P50/P95/max, RTF, result/UI/injection rates, provider, model, and device.

The lightweight health check runs once per second. If fresh audio and a speech-level signal continue but no recognition result arrives for five seconds, it writes one rate-limited diagnostic with queue, decode, CPU, provider, and model context.

If a native recognition call throws, a session permits exactly one controlled stream reset. The result worker clears only revisable hypothesis/partial-caption state while preserving finalized session history. A second failure crosses the session boundary, stops recognition, and surfaces an error; there is no restart loop.

## Transcript and caption contract

One result worker owns hypothesis ordering. `ITranscriptCommitPolicy` produces
`TranscriptSnapshot` plus zero-or-more `TranscriptCommit` values. Online native
and buffered engines use `StablePrefixCommitPolicy` with whole-word stability,
two confirmations, two-word holdback, and unconditional final-tail flush.
Offline/VAD engines use `FinalOnlyCommitPolicy`. The dashboard session preview
keeps the most recent 6,000 characters so WPF layout/allocation cost cannot grow
for the entire lifetime of a long session.

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

“Use now” selects and warms the active model without changing the persisted
default. “Set default” is a distinct operation. A listening-time switch confirms
with the user, completes the controlled stop/flush, loads the new engine through
`ISpeechEngineFactory`, and restarts only after load succeeds. The active
recognizer is unloaded before active files are deleted or replaced.

## Focus and local-data boundaries

The caption overlay is `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, uses
`ShowActivated=false`, and does not use activation toggling tricks. `SendInput`
retains and verifies the exact foreground HWND around every 64 UTF-16-unit
Direct block or every paced Notepad compatibility unit.

Raw audio, partial hypotheses, and caption objects are transient. Settings are atomically replaced. Logs contain operational metadata but not raw audio or full conversations.
