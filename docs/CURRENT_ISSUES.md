# RSTT current issues and reproduced evidence

Evidence date: 2026-07-28

Baseline branch/commit: `V2` / `bb94e8d`

Implementation state: uncommitted production-completion changes on that
baseline. This document contains no transcript or private audio content.

## Exact Windows text delivery

The repeated-character screenshot was localized after recognition: the Live
transcript remained coherent while packaged Windows 11 Notepad corrupted
accepted `VK_PACKET` input. Boundary tracing proved that commit/request text,
generated Unicode down/up records, the native message host, and `SendInput`
acceptance were exact before modern Notepad diverged.

The measured packaged-Notepad Compatibility profile is one paired UTF-16 unit
per call with a 20 ms yield after each unit, including commit boundaries.
Direct delivery remains 64 units per block without pacing for other targets.

The non-destructive runner passed 100 consecutive commits and 6,500 UTF-16
units with exact UI Automation and saved-file equality. The detailed evidence
is in `artifacts/test-results/notepad-integration.json`; the final run took
205,602.8471 ms on Notepad 11.2604.5.0.

## Final-tail and session lifecycle

Generation IDs flow through audio, hypotheses, commits, captions, and injection
requests. Stop enters Completing, drains capture/conversion/audio, finishes the
native stream, emits the terminal result, drains commit/injection work, then
stops the generation. Stale work is rejected. Endpoint result emission precedes
reset. These contracts remain covered by deterministic tests.

## Compute completion evidence

Detected machine:

- NVIDIA GeForce RTX 2060, 6,144 MiB dedicated VRAM;
- driver 595.71 and compute capability 7.5;
- CUDA toolkits 12.8 and 13.3 installed side by side;
- cuDNN 9.24.0.43;
- Microsoft Visual C++ 2015-2022 x64 runtime.

RSTT now ships native ASR only in one active, versioned worker process:

- base application: `sherpa-cpu/1.13.4` and `whisper-cpu/1.9.1`;
- Accelerator Pack: `sherpa-cuda12/1.13.4` and
  `whisper-cuda12/1.9.1`.

The length-prefixed named-pipe protocol implements handshake, load, warmup,
start, float32 audio, finish, unload, shutdown, ping, readiness, hypotheses,
performance, and structured faults. Auto terminates a failed CUDA worker before
CPU fallback. `CUDA Active` is emitted only after active-session decode.

The final app-local Accelerator Pack contains 416 files / 3,328,912,698 bytes.
Its Zip64 archive is 2,042,431,774 bytes with SHA-256:

`286ee5e15ab5fcfc97bab0899a63167832423312cfcd96c7b83527410f17b443`

The installer validated every manifest size/hash and installed both versioned
workers into a clean CPU publish. The published app then opened a responsive
main window with one CUDA worker; a second launch exited with code 0.

## Real-audio model evidence

All values use the same pinned 6.625-second, 16 kHz mono sherpa validation clip.
RTF below 1.0 is realtime. Text was meaningful and retained the final word.

| Model/profile | Backend | Load | Warmup | Decode | RTF | Peak worker WS |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Nemotron English 560 ms | CPU | 3,278.9 ms | 629.7 ms | 2,721.8 ms | 0.411 | 806,363,136 B |
| Nemotron English 560 ms | CUDA | 3,811.1 ms | 287.7 ms | 2,057.6 ms | 0.311 | 1,234,554,880 B |
| Qwen3-ASR 0.6B INT8 | CPU | 3,741.0 ms | 566.4 ms | 1,741.0 ms | 0.263 | 1,400,913,920 B |
| Qwen3-ASR 0.6B INT8 | CUDA | 5,090.4 ms | 814.1 ms | 2,677.7 ms | 0.404 | 1,849,569,280 B |
| Whisper Turbo Q5_0 | CPU | 553.9 ms | 29,974.0 ms | 30,196.4 ms | 4.558 | 649,736,192 B |
| Whisper Turbo Q5_0 | CUDA | 889.0 ms | 18,993.0 ms | 770.3 ms | 0.116 | 519,254,016 B |
| Whisper Turbo full | CPU | 2,266.8 ms | 31,965.2 ms | 32,567.7 ms | 4.916 | 1,703,587,840 B |
| Whisper Turbo full | CUDA | 1,631.9 ms | 507.4 ms | 420.9 ms | 0.064 | 455,553,024 B |

On this RTX 2060, Qwen's CPU path was faster than its CUDA path for this short
clip; the app reports the actual backend and does not claim that CUDA is always
faster. Whisper is not realtime on this CPU but is comfortably realtime on
CUDA.

Qwen's production download found and fixed a real mapping defect: the pinned
catalog correctly provides three tokenizer files, while the worker had expected
a synthetic `tokenizer` key. It now derives and validates the tokenizer
directory from `tokenizer-merges`. Qwen uses feature dimension 128 and
final-segment-only Silero VAD with 200 ms pre-roll, 500 ms post-roll, and a
20-second maximum segment.

Whisper Q5_0 and full use the same isolated-worker VAD/final-only route. The VAD
history is strictly bounded to the configured segment plus context window.

## Current validation

- `dotnet restore RSTT.sln`: passed.
- Debug build: passed with zero warnings/errors.
- Debug tests: 85/85, zero skipped.
- Release build: passed with zero warnings/errors.
- Release tests: 85/85, zero skipped.
- Clean CPU-only publish: 907 files / 373,062,964 bytes.
- CPU-only published startup: responsive main window; both CPU workers present;
  no CUDA worker directory.
- CPU-only packaged sherpa worker real-audio decode: passed; Parakeet RTF 0.582
  on the 11-second public-domain diagnostics sample.
- Accelerator-installed publish: 1,317 files / 3,701,801,512 bytes.
- Accelerator-installed startup: responsive main window and CUDA worker.
- Published Settings diagnostics: completed in 7.51 seconds; maximum/unique
  worker count 1/1; `CUDA Active`, provider `cuda`, self-test Ready, transcript
  excluded.
- Published single-instance check: second launch exited 0 while primary stayed
  responsive.
- Real CPU and RTX 2060 CUDA decode: passed for Nemotron, Qwen, Whisper Q5_0,
  and Whisper full.

## Remaining acceptance work

- The current revision's 30-minute mixed recognition/injection soak has not
  been repeated. Older 30-minute results remain historical evidence only.
- The full clean/fast/quiet/accented/multilingual Common Voice matrix is not
  bundled. The diagnostics asset is the pinned public-domain `whisper.cpp`
  `jfk.wav` sample and its report excludes transcript content.
- Parakeet TDT and Moonshine Tiny/Base remain Experimental and non-actionable
  because their pinned payload/corpus gates are incomplete.
- A public GitHub release has not been created. The complete local application
  and Accelerator Pack artifacts are ready, but publishing/tagging is an
  external release action and should occur only after choosing the release
  version and accepting the included NVIDIA terms/notices.
