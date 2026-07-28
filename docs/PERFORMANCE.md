# Performance diagnosis and results

Measured on 2026-07-28:

| Component | Value |
| --- | --- |
| OS/runtime | Windows x64, .NET 8 application |
| CPU | Intel Core i7-12700F, 12 cores / 20 logical processors |
| GPU | NVIDIA GeForce RTX 2060 6 GB; driver 595.71 |
| Audio | Realtek render endpoint, 48 kHz, 32-bit float, stereo |
| Normalized ASR input | 16 kHz mono float |
| Test source | Repeating local SAPI English paragraph through real WASAPI loopback |

Percentages below are whole-machine-normalized process CPU values sampled during comparable 45-second continuous-speech windows. Working set includes loaded ONNX model/native runtime.

## Diagnosis

The audit did **not** find an unconditional empty `IsReady` busy-spin. The dominant cost was native ONNX inference for the original **buffered Parakeet 1120 ms model with four CPU threads**. It was asked for work at small audio arrival intervals and periodically performed expensive buffered-window computation. Managed tracing showed worker waits between inputs while native inference dominated active samples.

Several secondary hot-path problems amplified the visible stalls:

- conversion and temporary arrays occurred too close to the WASAPI callback;
- downstream result handling, WPF publication, and `SendInput` were insufficiently isolated;
- partial UI work could arrive faster than it needed to render;
- foreground-self injection produced repeated warnings;
- queue age, decode duration, and RTF were not observable, so model cost and pipeline backlog looked identical.

The reported 8–12-second symptom had no matching fatal exception. Evidence supports periodic buffered-model inference plus callback/downstream contention as the cause, not a crash. The replacement architecture removes those contention paths and changes the default to cache-aware Nemotron.

## Before/after continuous speech

| Configuration | Avg CPU | P95 CPU | Max CPU | Avg working set | Avg managed allocation | Queue/backlog |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Original Parakeet, 4 CPU threads | 44.86% | 47.77% | 48.91% | 1013.65 MB | 1.744 MB/s | No explicit bounded stage telemetry |
| Hardened pipeline, same Parakeet, 2 CPU threads | 18.04% | 19.36% | 19.63% | 1023.81 MB | 1.008 MB/s | Thread-pool queue 0 |
| Hardened pipeline, Nemotron EN 560 ms, 1 CPU thread | 1.88% | 2.43% | 3.05% | 982.84 MB | 0.823 MB/s | Thread-pool queue 0 |

The hardened architecture reduced Parakeet CPU by approximately 60% before changing models and reduced average allocation by approximately 42%. Selecting the cache-aware Nemotron default reduced process CPU approximately 96% relative to the original configuration in this test.

## Corrected in-app pipeline metrics

A separate 44-second end-to-end run used the verified Nemotron EN 560 ms installation, CPU provider, one thread, real WASAPI capture, and 25 seconds of continuous synthesized speech followed by silence:

| Metric | Result |
| --- | ---: |
| Rolling RTF | 0.329 |
| Decode P50 | 185.4 ms |
| Decode P95 | 198.1 ms |
| Decode max | 204.6 ms |
| Audio queue duration at stop | 0.0 ms |
| Oldest queued audio at stop | 0.0 ms |
| Dropped audio | 0 ms |
| Coalesced UI events | 0 |

The original build had no valid RTF instrument, so a truthful before value is unavailable. Adding that instrument was part of this pass; no baseline RTF is inferred from CPU.

## Idle and silence

Baseline idle and listening-silence counter runs showed negligible CPU and no unconditional decoder spin. The new workers block on bounded channels, the UI telemetry timer is 1 Hz, the audio meter is capped at 25 Hz, and partial captions are capped at 20 Hz.

## Memory and GC

During the verified Nemotron continuous run:

- working set averaged 982.84 MB and peaked at 988.04 MB;
- managed heap averaged 16.98 MB and peaked at 23.55 MB;
- average managed allocation was 823,042 bytes/s;
- Gen0 collection rate was 0.044/s; no Gen1 or Gen2 collection occurred;
- thread-pool queue length remained zero.

The large working set is predominantly the loaded native model/runtime, not managed transcript history. Caption history is bounded, raw packet buffers are pooled, normalized channels are bounded, and model downloads reuse a 256 KiB pooled buffer.

## Acceptance interpretation

- RTF is comfortably below `1.0`.
- Audio queue age does not grow continuously.
- No normalized audio was dropped in the corrected run.
- No thread-pool backlog or Dispatcher flood was observed.
- The model was not reloaded between ordinary stop/start cycles.
- Session-stop logs now record provider, model, CPU, RTF, decode percentiles, queue age, drops, and coalescing.
- A one-second watchdog records a rate-limited stall diagnostic only when audio and a speech-level signal are fresh but results have been absent for five seconds.

## 30-minute published release stress run

The self-contained published executable completed 30 minutes of continuous SAPI speech through the real render endpoint and WASAPI loopback. The counter capture covered 30:29 with 1,830 one-second samples:

| Metric | Result |
| --- | ---: |
| CPU average / P95 / max | 1.907% / 2.524% / 3.413% |
| RTF at stop | 0.333 |
| Decode P50 / P95 / max | 185.2 / 197.1 / 209.5 ms |
| Working set average / max | 964.4 / 968.4 MB |
| Managed heap average / max | 13.3 / 21.2 MB |
| Thread-pool queue average / max | 0.001 / 1 |
| Managed exceptions | 0 |
| Watchdog stalls | 0 |
| Dropped audio | 0 ms |
| Queue duration / oldest audio at stop | 0 / 0 ms |
| Unexpected capture stops / model reloads | 0 / 0 |

System playback remained continuous and the UI stayed responsive. Native/private memory stabilized; managed heap cycled rather than growing indefinitely.

The run exposed a different long-session inefficiency: the dashboard preview retained and re-rendered the whole session string, causing allocation rate to rise as the transcript length increased even though GC reclaimed it. The final implementation bounds the dashboard preview to the most recent 6,000 characters; caption history was already bounded to 100 final segments and the configured visible-line count.

An accelerated six-minute regression on the refreshed final publish saturated that 6,000-character window. Allocation increased while the preview filled, then plateaued at 3.351 and 3.355 MB/s in the final two full minutes instead of continuing to scale with transcript length. CPU averaged 1.865% (P95 2.506%, max 3.385%), RTF was 0.335, and audio queue age/drops remained zero.

## Reproduction

Build and run RSTT, select the desired model/backend/profile, play a repeatable source through the chosen render endpoint, then inspect the end-of-session record:

```text
Session performance: provider=..., model=..., CPU=...,
RTF=..., decode P50/P95/max=..., audio queue=..., oldest=...,
dropped=..., UI coalesced=...
```

Use a release/published build for final numbers. Do not compare different model families, latency profiles, thread counts, or audio sources as if only architecture changed.
