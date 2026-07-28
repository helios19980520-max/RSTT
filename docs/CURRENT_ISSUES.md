# RSTT current issues and reproduced evidence

Evidence date: 2026-07-28

Baseline branch/commit: `V2` / `7cf6b4b`

Implementation state: uncommitted production-completion changes on that baseline

This file distinguishes reproduced facts from pending release gates. It contains
no transcript or private audio content.

## Exact text delivery: cause reproduced and fixed

The screenshot session showed a coherent Live transcript while packaged Windows
11 Notepad produced long repeated-character runs. The first divergent boundary
is now reproduced on Notepad 11.2604.5.0:

1. `TranscriptCommit.Text` and `InjectionRequest.Text` match by identity, UTF-16
   length and truncated SHA-256.
2. Generated `INPUT` records are paired Unicode down/up records carrying the
   RSTT `dwExtraInfo` marker.
3. The native message host observes one marked `WM_KEYDOWN` and `WM_KEYUP` per
   UTF-16 unit, repeat count 1, and exact destination text.
4. `SendInput` reports full acceptance.
5. Modern Notepad's UI Automation text and its saved temporary file both first
   diverge after that boundary, substituting repeated earlier characters.

Measured failing examples:

- 8 units with 8 ms pacing: one 65-unit commit diverged at offset 6.
- 4 units with 20 ms pacing: one commit diverged at offset 6.
- 2 units with 20 ms pacing: short runs passed, but the sustained run lost a
  character at offset 35 and duplicated the next character.
- 1 unit with 20 ms pacing but no final-commit yield: intra-commit text was
  exact, but the first character of later commits was intermittently lost.

The packaged-Notepad Compatibility profile is therefore one paired UTF-16 unit
per `SendInput` call with a 20 ms yield after every unit, including commit
boundaries. Direct mode remains 64 units with no pacing for other targets.

Acceptance evidence:

- 100 consecutive commits;
- 6,500 expected and observed UTF-16 units;
- exact UI Automation equality;
- exact saved temporary-file equality;
- 208,593.853 ms measured delivery time;
- exact Notepad HWND/process/version recorded in
  `artifacts/test-results/notepad-integration.json`.

The runner never edits an existing document. If Notepad reuses an existing
window, it creates a fresh window, closes only the runner's empty temporary tab,
opens the unique temp file in the fresh HWND, and closes only that test window.
There is no skip/early-return success path.

## Final-tail and session lifecycle

The controlled stop/finalization architecture remains in place:

- generation IDs are carried through audio, hypotheses, commits, captions and
  injection requests;
- stop enters Completing while final commits remain accepted;
- capture and normalized queues drain before terminal recognizer input;
- `InputFinished()` is used only at session completion;
- endpoint result emission precedes reset;
- result/commit/injection workers drain before the generation stops;
- stale prior-generation results are rejected.

Lifecycle and transcript tests cover final-tail, endpoint/reset ordering and
queued-audio-before-finish behavior.

## Compute evidence

- GPU: NVIDIA GeForce RTX 2060.
- Dedicated memory: 6,144 MiB.
- Driver: 595.71.
- Compute capability: 7.5.
- Installed toolkit: CUDA 13.3 (`cudart64_13.dll`).
- Required sherpa runtime: CUDA 12.x, cuDNN 9.x and matching ONNX provider.
- Missing: `cudart64_12.dll`, `cudnn64_9.dll`,
  `onnxruntime_providers_cuda.dll`, and the app-local sherpa CUDA worker.
- Recorded ASR provider: CPU.

The app now displays hardware, driver, CUDA, cuDNN, native workers, provider,
model compatibility, recognizer load, warmup and active inference separately.
It does not display `CUDA Active` from adapter/toolkit presence.

The versioned named-pipe protocol and isolated Whisper CPU/CUDA 12 workers are
implemented. The CPU worker process handshake is covered by the automated suite.
The sherpa CUDA worker/runtime bundle and its dependency/licence review remain
open, so a complete Accelerator Pack is not advertised.

## Model evidence

Production-Available models remain:

- Nemotron Streaming English 0.6B INT8, 560 ms (default).
- Nemotron 3.5 Streaming Multilingual 0.6B INT8, 560 ms.
- Parakeet Unified English 0.6B INT8, 1120 ms.

Actionable Preview models:

- Qwen3-ASR 0.6B INT8 `2026-03-25`, exact revision
  `68818b2313fe77bd06f6a7c5068ff3ef59d02b8a`, seven pinned artifacts,
  feature dimension 128, tokenizer directory mapping, Silero VAD 200/500 ms and
  20-second maximum segments.
- Whisper Large v3 Turbo Q5_0, 574,041,195-byte pinned model.
- Whisper Large v3 Turbo full, 1,624,555,275-byte pinned model.

Preview is intentionally distinct from Supported. Real licensed-audio decode,
final-tail, memory, RTF and RTX 2060 CUDA evidence are still required before
promotion.

## Current validation

- Debug build: passes with zero warnings/errors.
- Debug automated tests: 80/80, zero skipped.
- Release build: passes with zero warnings/errors.
- Release automated tests: 80/80, zero skipped.
- Modern Notepad acceptance: passed 100/100 commits as described above.
- CPU Whisper worker: self-contained process starts, performs protocol
  handshake, and shuts down in the automated suite.
- CPU application publish: passed; 710 files / 276,394,405 bytes, including
  `workers\whisper-cpu\1.9.1\RSTT.Whisper.Worker.exe` and no CUDA/cuDNN-named
  files. The published app passed a five-second startup check with a real main
  window. A second published launch exited with code 0 while the primary
  remained alive with its window.
- Optional Whisper CUDA 12 worker developer publish: passed; 202 files /
  1,210,089,819 bytes. It is not the complete Accelerator Pack.

## Open production gates

- Do not release a CUDA Accelerator Pack until the sherpa CUDA 12/cuDNN 9
  bundle, redistribution notices and actual provider/model decode pass.
- Download and run the Qwen and both Whisper payloads against licensed real
  audio; record transcription, final token, RTF, peak memory and language.
- Add the full requested Common Voice corpus and metadata.
- Repeat the 30-minute current-revision session. Older performance numbers must
  not be relabeled as current.
