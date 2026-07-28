# RSTT production completion report

Date: 2026-07-28

This report contains the requested 23 evidence items. “Verified” means reproduced
in this workspace. “Pending” is never presented as a production claim.

## Evidence

1. **Repeated-character root cause — verified.** Commit/request text, generated
   records, native messages and `SendInput` acceptance are exact. Packaged
   Notepad 11.2604.5.0 first diverges afterward: multi-unit or insufficiently
   separated `VK_PACKET` delivery makes Notepad substitute repeated earlier
   characters. The first saved-file divergence was reproduced at UTF-16 offset
   6. The measured safe profile is one paired UTF-16 unit per call plus a 20 ms
   yield after every unit and commit boundary.

2. **Commit/request identity — verified.** Generation, commit and source
   sequence IDs are assigned once. `TranscriptCommit.Text` is assigned directly
   to `InjectionRequest.Text`; UTF-16 lengths and truncated SHA-256 values are
   logged without Release transcript text.

3. **Native record/message identity — verified.** Every record carries the RSTT
   `dwExtraInfo` marker. The native host records `WM_KEYDOWN`, `WM_KEYUP`,
   `WM_CHAR`, scan value and repeat count. It observes one marked down/up pair
   per UTF-16 unit and repeat count 1.

4. **Partial-send safety — verified.** Accepted records are never replayed.
   Even partial sends advance complete units. Odd partial sends receive only the
   missing cleanup key-up. Three positive-progress continuations are the limit;
   no whole commit retry exists.

5. **Target/focus safety — verified.** Requests retain exact HWND/PID. The HWND
   is checked around every block. Target changes abort the remainder, self-focus
   is dropped before enqueue, UIPI uses integrity inspection, and no clipboard
   or changed-target replay is used.

6. **Modern Notepad acceptance — verified.** The unique-temp-document runner
   passed 100 commits and 6,500 UTF-16 units with exact UI Automation and saved
   file equality. Delivery took 208,593.853 ms. Evidence:
   `artifacts/test-results/notepad-integration.json`.

7. **Single instance — implemented and verified from the published output.** A
   named mutex is acquired before service construction, hotkeys, capture or
   injection workers. A second launch signalled the primary process and exited
   with code 0 while the primary remained alive with its window handle.

8. **Final-tail lifecycle — verified by automated tests.** Completing accepts
   final commits; capture and normalized queues drain before terminal input;
   terminal result emission precedes reset; result/commit/injection queues drain
   before generation stop; stale generations are rejected.

9. **GPU hardware — verified.** RTX 2060, vendor `0x10DE`, 6,144 MiB dedicated,
   driver 595.71, compute capability 7.5.

10. **Installed CUDA — verified but incompatible for sherpa.** CUDA toolkit
    13.3 provides `cudart64_13.dll`. The required `cudart64_12.dll` is absent.

11. **cuDNN/provider — missing.** `cudnn64_9.dll` and
    `onnxruntime_providers_cuda.dll` are absent. File presence, provider load,
    recognizer load, warmup and active decode are reported separately.

12. **Structured diagnostics — implemented.** Settings shows System/VC++, GPU,
    driver, CUDA, cuDNN, sherpa worker, Whisper worker, provider, model,
    recognizer, warmup and active inference cards with state, required/detected
    version, path, fallback reason, Accelerator Pack action and official links.
    Copied diagnostics exclude transcript/audio.

13. **Actual active backend — CPU.** No RTX 2060 CUDA ASR decode has passed.
    `CUDA Active` is shown only for a session context set after a verified CUDA
    worker model load, warmup and decode.

14. **Worker protocol — implemented and tested.** Version 1 is little-endian,
    length-prefixed and bounded. It supports handshake, load, warmup, start,
    binary float32 audio, finish, unload, shutdown, ping, ready, hypothesis,
    performance and structured fault messages. Short-read and audio round trips
    are tested.

15. **Whisper workers — implemented.** `whisper-cpu/1.9.1` is self-contained in
    the base build. `whisper-cuda12/1.9.1` is a separate optional project.
    Auto terminates a failed CUDA process before CPU fallback. The CPU worker
    process handshake is verified; real model decode is pending.

16. **Qwen integration — actionable Preview.** Exact revision and seven
    artifact size/SHA-256 values are pinned. Nested tokenizer installation is
    supported, the tokenizer directory is mapped to sherpa, feature dimension is
    128, Silero VAD policy is 200/500 ms with a 20-second maximum, and commits
    are final-segment only. Real-audio/RTF validation remains pending.

17. **Whisper Large v3 Turbo integration — actionable Preview.** Q5_0 is the
    default family profile (574,041,195 bytes,
    SHA-256 `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2`);
    full is optional (1,624,555,275 bytes,
    SHA-256 `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69`).
    Both are transcription-only, segmented realtime and auto/manual language.

18. **Factory routing — implemented.** Engine selection is descriptor-driven:
    online sherpa, offline/VAD sherpa or isolated `WhisperCppEngine`. The view
    model contains no model-family switch.

19. **Model workflow/UI — implemented.** One vertical virtualized list, no
    horizontal carousel, search/filters, compact rows, details, row-local
    download state and distinct Use now/Set default semantics. Preview is
    downloadable/actionable but is not labeled Supported.

20. **Performance telemetry — implemented, current long run pending.** Injection
    rate, average UTF-16 length, `SendInput` calls/second, queue high-watermark
    and failures join existing CPU, memory, queue, decode and RTF metrics. The
    prior 30-minute CPU figures remain in `docs/PERFORMANCE.md`; they predate
    this revision.

21. **Known limitations.** Sherpa CPU still runs in-process. A complete sherpa
    CUDA worker/pack, dependency/licence review, Qwen/Whisper real-audio matrix,
    current 30-minute run, full Common Voice corpus, benchmark persistence and
    current RTX 2060 CUDA evidence remain open. Notepad Compatibility is exact
    but intentionally slow at about 50 UTF-16 units/second.

22. **CPU publish command.**

    `dotnet publish src\RSTT.App\RSTT.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishProfile=win-x64`

23. **Published executable and optional worker paths — verified.**

    - `E:\workSpace\RSTT\artifacts\publish\win-x64\RSTT.App.exe`
    - `E:\workSpace\RSTT\artifacts\publish\win-x64\workers\whisper-cpu\1.9.1\RSTT.Whisper.Worker.exe`
    - Optional CUDA developer publish:
      `E:\workSpace\RSTT\artifacts\publish\accelerator-pack\workers\whisper-cuda12\1.9.1\RSTT.Whisper.Cuda12.Worker.exe`

## Final validation

- `dotnet restore RSTT.sln`: passed.
- Debug build: passed, zero warnings and zero errors.
- Debug tests: 80/80 passed, zero skipped.
- Release build: passed, zero warnings and zero errors.
- Release tests: 80/80 passed, zero skipped (Core 42, Speech 38).
- CPU publish: passed. The output contains 710 files / 276,394,405 bytes,
  including the versioned Whisper CPU worker, and no CUDA/cuDNN-named files.
- Optional Whisper CUDA 12 worker developer publish: passed. The output contains
  202 files / 1,210,089,819 bytes. This is not a complete or release-eligible
  Accelerator Pack.
- Clean-output startup: `RSTT.App.exe` remained alive after five seconds and
  created main window handle `0x540EC6`; the exact test process was then stopped.
- Published single-instance check: the primary remained alive with a real window
  handle; the second launch exited with code 0.

The first CPU publish attempt exposed a packaging collision: worker files were
assigned a non-item-qualified relative path. The mapping now retains each
worker file's recursive path under `workers\whisper-cpu\1.9.1`; the corrected
publish passed.

A complete Accelerator Pack is not release-eligible until the sherpa CUDA
12/cuDNN 9 worker and notices are present and both CUDA workers pass real model
decode.
