# RSTT production hardening report

Date: 2026-07-28

This report distinguishes reproduced evidence from unfinished production gates.
It does not call a model supported or a backend active without a real decode.

## Required evidence

1. **Repeated-character root cause.** The screenshot-era failure's single
   historical root cause is not proven. The previous Notepad test returned
   early when a user-owned Notepad existed, and Release logs lacked commit,
   generated-record, accepted-record, offset, and HWND correlation. The unsafe
   ambiguity was partial-send handling: delivery could stop without a resumable
   offset, and callers had no structured way to avoid replay. The hardened
   implementation makes replay impossible and the new harness has not
   reproduced corruption.

2. **Exact injection fix.** `TranscriptCommit.Text` is assigned unchanged to
   `InjectionRequest.Text`. A single bounded worker retains the exact HWND,
   emits paired Unicode down/up records in 64-UTF-16-unit blocks, advances only
   accepted offsets, performs odd-key cleanup, allows at most three
   positive-progress continuations, and aborts on target change. Self-focused
   commits are dropped before enqueueing.

3. **Missing-final-word root cause.** Two concrete stop defects were confirmed:
   coordinator cancellation could end audio consumption before queued tail
   audio reached sherpa, and `IsListening` became false before the engine
   emitted its terminal result, causing the result/injection gates to discard a
   valid final commit.

4. **Endpoint/finalization fix.** Stop remains in `Completing` while final
   commits are accepted, stops capture, drains conversion and normalized audio,
   drains audio into the engine, calls `OnlineStream.InputFinished()`, decodes
   remaining work, emits the terminal hypothesis, drains result/commit/injection
   channels, then marks the generation stopped. A five-second overall timeout
   records the incomplete stage before hard cancellation. At natural online
   endpoints the final result is emitted before `Reset(stream)`.

5. **Transcript architecture.** Typed
   `RecognitionHypothesis -> TranscriptSnapshot/TranscriptCommit ->
   InjectionRequest` records carry generation, sequence, and commit identity.
   Online engines use two-confirmation whole-word stability with two-word
   holdback and unconditional final flush. Offline/VAD engines commit final
   segments only.

6. **Injection architecture.** See `docs/TEXT_INJECTION.md`. Results expose
   `Success`, `Partial`, `TargetChanged`, `SelfFocused`, `ElevatedTarget`,
   `Unavailable`, and `Failed` with record counts, UTF-16 offset, HWND/PID,
   Win32 error, and diagnostics.

7. **Exact-insertion tests.** Release tests pass 73/73 with zero skips. The
   dedicated native edit-control process verifies Latin, punctuation, Japanese,
   Korean, accented Latin, emoji/surrogates, long text, consecutive commits,
   and 100 exact sentence repetitions. Deterministic tests cover all requested
   complete/partial/focus/elevation/order/queue/generation cases.

8. **GPU detected.** DXGI and supplemental NVIDIA tooling report NVIDIA GeForce
   RTX 2060, vendor `0x10DE`, 6,144 MiB dedicated memory, driver 595.71.

9. **CUDA runtime.** NVIDIA tooling reports driver CUDA capability, and CUDA
   toolkit 13.3 is installed, but the required `cudart64_12.dll` is not on PATH.
   This is **not** a usable RSTT CUDA runtime.

10. **cuDNN status.** Required `cudnn64_9.dll` is absent.

11. **sherpa GPU runtime.** Not installed or published. The clean publish
    contains CPU `sherpa-onnx-c-api.dll`, `sherpa-onnx.dll`, and
    `onnxruntime.dll`; it contains no CUDA provider or CUDA dependency DLLs.

12. **Actual active provider.** CPU. RSTT shows `CUDA Active` only after a
    successful decode using provider `cuda`; no such decode occurred.

13. **CPU fallback.** CPU is always available in the base package. Auto/explicit
    CUDA selection reports layer-specific missing dependencies and selects CPU.
    The published CPU executable launched and remained alive for the five-second
    smoke window without CUDA dependencies.

14. **Genuinely supported models.** Three: Nemotron Streaming English 0.6B INT8
    560 ms, Nemotron 3.5 Streaming Multilingual 0.6B INT8 560 ms, and Parakeet
    Unified English 0.6B INT8 1120 ms. Four additional requested families are
    intentionally non-actionable Experimental records, not fake supported
    models.

15. **Runtime mode.** Nemotron English and Nemotron 3.5 are Native Streaming;
    Parakeet Unified is Buffered Streaming. Parakeet TDT v3, Qwen3-ASR,
    Moonshine Tiny, and Moonshine Base are designed for Segmented Realtime/VAD
    but remain unvalidated.

16. **Download sizes.** Verified catalogue payloads: Nemotron English
    approximately 631 MiB, Nemotron 3.5 approximately 651 MiB, and Parakeet
    Unified approximately 632 MiB. Experimental rows show no downloadable size
    until pinned files, byte counts, and SHA-256 values are verified.

17. **Models UI.** The horizontal carousel is replaced with one virtualized
    vertical list with horizontal scrolling disabled, compact rows, search,
    All/Installed/Streaming/Multilingual/CUDA-capable filters, inline state and
    actions, details, and separate Use now/Set default semantics.

18. **Recognition correctness.** Final-tail loss is addressed at policy,
    endpoint, and session-stop boundaries. Session generation IDs suppress late
    prior-session work. Online and offline engines have distinct commit and
    endpoint policies. No new word-error-rate claim is made without a licensed
    corpus run.

19. **Performance measurements.** The latest existing local CPU measurements
    remain in `docs/PERFORMANCE.md` (Nemotron RTF 0.329, decode P50/P95
    185.4/198.1 ms in the recorded end-to-end run). They predate this change set
    and were not relabeled as new measurements.

20. **Long-session result.** The repository records a prior 30-minute CPU run:
    RTF 0.333, CPU average/P95/max 1.907/2.524/3.413%, zero dropped audio and
    zero watchdog stalls. A new 30-minute run of this exact revision, including
    exact injection counters, remains required.

21. **Known limitations.** No isolated named-pipe backend worker, CUDA
    Accelerator Pack, provider warmup/decode, four new pinned model
    integrations, licensed Common Voice corpus, per-model benchmark
    persistence, Debug pipeline inspector, responsive details drawer/modal, or
    current-revision 30-minute acceptance run is complete.

22. **Release publish command.**

    `dotnet publish src\RSTT.App\RSTT.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishProfile=win-x64`

23. **Published executable.**

    `E:\workSpace\RSTT\artifacts\publish\win-x64\RSTT.App.exe`

## Final validation performed

- `dotnet restore RSTT.sln`: passed.
- Debug build: passed with zero warnings/errors.
- Debug tests: 73/73, zero skipped.
- Release build: passed with zero warnings/errors.
- Release tests: 73/73, zero skipped.
- Clean self-contained win-x64 CPU publish: passed, 270 top-level files,
  174,816,769 bytes.
- Published executable smoke test: process remained alive after five seconds
  and was then stopped by the validation script.

The CUDA worker/pack publish was not run because no CUDA worker/pack project
exists and the required runtime/licence gate has not passed.
