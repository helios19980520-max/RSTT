# RSTT current issues and reproducible baseline

Baseline date: 2026-07-28  
Branch/commit: `V2` / `139dfd5`

## Build and publish baseline

- `dotnet build RSTT.sln -c Debug --no-restore`: passed, zero warnings/errors.
- `dotnet build RSTT.sln -c Release --no-restore`: passed, zero warnings/errors.
- `dotnet test RSTT.sln -c Release --no-restore`: passed 47/47 tests.
- `dotnet publish src\RSTT.App\RSTT.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishProfile=win-x64`: passed.
- Published application: `artifacts\publish\win-x64\RSTT.App.exe`.
- Published native ASR runtime is the CPU `org.k2fsa.sherpa.onnx` 1.13.4 runtime with ONNX Runtime 1.27.0.

The existing `UnicodeSendInputTypesIntoNotepad` test did not exercise text injection during this baseline run because a user-owned Notepad window already existed and the test returned early. Its green result is therefore not evidence of exact injection.

## P0: text injection corruption

Observed product evidence shows recognizer/caption text that is substantially correct while Notepad receives repeated or corrupted characters. The current Release logs do not carry the commit ID, exact target HWND, generated input count, `SendInput` return count, or UTF-16 offset at every boundary. The root cause cannot be stated honestly until that instrumentation and a deterministic edit-control harness reproduce or disprove the failure.

Relevant current path:

`RecognitionResult` → `TranscriptStabilizer` → `TranscriptUpdate.NewlyStableText` → coordinator injection channel → `Win32TextInjectionService` → `INPUT[]` → `SendInput`.

Known weaknesses:

- The public injection contract accepts an uncorrelated string rather than a commit/request identity.
- Results do not report expected/sent input records, committed UTF-16 offsets, HWND, process ID, or Win32 error.
- Foreground verification compares process IDs, so switching between two windows owned by the same process is not detected.
- The integration test can silently skip and only checks that one short string is contained in Notepad text.
- Partial sends cannot be resumed or diagnosed without replay ambiguity.

## P0: final words lost

Two concrete stop-path defects exist in the current coordinator:

1. `StopUnsafeAsync` cancels the audio-reader token before the capture service finishes draining raw and normalized audio channels. The audio worker can exit without feeding queued tail audio to the recognizer.
2. `StopUnsafeAsync` sets `IsListening` to false before `ISpeechRecognitionEngine.StopAsync()` emits its terminal result. Result processing only enqueues injection while `IsListening` is true, so a correctly flushed final commit can be dropped.

Natural endpoint loss still requires boundary instrumentation. The online engine currently reads a result before `Reset(stream)`, but there is no lifecycle test proving that the last decoded hypothesis reaches commit, caption, and injection before reset.

## Compute baseline

- GPU: NVIDIA GeForce RTX 2060.
- Dedicated memory: 6144 MiB (reported by NVIDIA tooling).
- NVIDIA driver: 595.71.
- Driver-supported CUDA level reported by `nvidia-smi`: 13.2.
- Installed toolkit: CUDA 13.3.
- `cudart64_12.dll`, `cublas64_12.dll`, `cublasLt64_12.dll`, `cufft64_11.dll`, `cudnn64_9.dll`, and `onnxruntime_providers_cuda.dll`: not available on `PATH`.
- Current RSTT sessions: `provider=cpu`.
- Current app compute probe correctly refuses to advertise CUDA as active.

The NVIDIA driver/toolkit evidence does not prove that sherpa's CUDA execution provider can load. CUDA must remain unavailable until a matching worker/runtime loads a selected model and completes a real warm-up decode.

## Installed model baseline

- Nemotron Streaming English 0.6B INT8, 560 ms: installed and selected.
- Parakeet Unified English 0.6B INT8, 1120 ms: installed.
- Nemotron 3.5 multilingual: catalogued but not installed on this machine.
- Qwen3-ASR and Whisper entries are currently non-actionable roadmap records.

No additional model will be changed to a supported/available state until its pinned artifacts, validation, runtime load, real-audio result, final-tail behavior, and measured runtime mode have been verified.

## Reproduced hardening evidence

The following results were reproduced after implementation on 2026-07-28:

- Debug build: zero warnings/errors.
- Debug tests: 73/73, zero skipped.
- Release build: zero warnings/errors.
- Release tests: 73/73, zero skipped.
- The dedicated native edit-control host passed exact equality for Latin,
  punctuation, Japanese, Korean, accented Latin, emoji/surrogates, 2,048 UTF-16
  units, consecutive commits, and 100 required-sentence repetitions.
- Deterministic adapter tests passed complete, zero, even/odd partial,
  target-change, self-focus, elevated-target, ordering, bounded-channel, and
  stale/cancelled generation cases.
- Transcript tests passed growing/revising partials, whole-word stability,
  duplicate suppression, reset, and all final-tail examples.
- Lifecycle façade tests proved terminal result emission precedes online reset.
- Coordinator lifecycle testing proved queued audio reaches the engine before
  `InputFinished()` and the terminal tail reaches injection.
- DXGI hardware detection returned a physical NVIDIA GeForce RTX 2060 adapter.
- A clean self-contained win-x64 CPU publish succeeded, and its
  `RSTT.App.exe` process remained alive through a five-second startup smoke test.

The screenshot-era repeated-character corruption could not be reproduced in the
old build because the only integration test returned early when user-owned
Notepad existed. It is therefore still incorrect to claim one proven historical
root cause. The ambiguous behaviors that could corrupt delivery have been
removed: whole-request replay after a partial send is impossible, exact HWND
changes abort, all sends are serialized, and commit/request equality is
correlated by IDs and hashes.

## Open production gates

- The standard package remains CPU-only. CUDA 12.x, cuDNN 9.x, the sherpa CUDA
  worker, ONNX CUDA provider load, CUDA recognizer warmup, and CUDA inference are
  not verified on this machine.
- The versioned isolated backend worker and CUDA Accelerator Pack are not
  implemented/published.
- Only the three original pinned model integrations satisfy the catalogue's
  `Available` contract. Parakeet TDT v3, Qwen3-ASR, Moonshine Tiny, and Moonshine
  Base are visible as non-actionable `Experimental` records until artifact,
  licence, real-audio, final-tail, memory, and RTF gates pass.
- The requested licensed Common Voice corpus and per-model CPU/CUDA matrix have
  not been added or executed.
- A new 30-minute run of this exact hardening revision is still required; the
  measurements in `docs/PERFORMANCE.md` predate these changes.
