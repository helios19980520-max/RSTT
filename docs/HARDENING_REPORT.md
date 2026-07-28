# RSTT production completion report

Date: 2026-07-28

This report lists the requested 23 evidence items. Verified values were
reproduced in this workspace; remaining gates are explicit.

1. **Repeated-character root cause — verified and fixed.** Recognition,
   commit/request text, generated Unicode records, native messages, and
   `SendInput` acceptance were exact. Packaged Notepad first diverged after
   those boundaries under sustained `VK_PACKET` delivery. The measured exact
   profile is one paired UTF-16 unit per call plus 20 ms yield.
2. **Commit/request identity — verified.** Generation, commit, source sequence,
   UTF-16 length, and privacy-safe hash remain identical across boundaries.
3. **Native record/message identity — verified.** Every record carries RSTT's
   marker; the native host records down/up/char/scan/repeat data and exact text.
4. **Partial-send safety — verified.** Accepted records are never replayed;
   odd partials receive only their cleanup key-up; whole commits are never
   retried.
5. **Target/focus/UIPI safety — verified.** Exact HWND/PID is retained and
   checked per block. Self-focus is dropped, changed targets abort, integrity
   inspection reports elevated targets, and there is no clipboard fallback.
6. **Modern Notepad — verified.** 100 commits / 6,500 UTF-16 units matched UI
   Automation and the saved unique temporary file exactly. No existing document
   is reused or modified.
7. **Single instance — verified.** The mutex is acquired before services,
   hotkeys, capture, or injection. Published second launch exited 0 while the
   primary remained responsive.
8. **Final-tail lifecycle — verified.** Completing accepts final commits;
   queues drain before terminal input; final result precedes reset; stale
   generation work is rejected.
9. **GPU hardware — verified.** RTX 2060, 6,144 MiB, driver 595.71, compute
   capability 7.5.
10. **CUDA/cuDNN runtime — verified.** App-local sherpa pack uses CUDA 12.8 and
    cuDNN 9.24.0.43; installed CUDA 13.3 may coexist.
11. **Provider and worker isolation — verified.** CPU sherpa 1.13.4 and Whisper
    1.9.1 workers ship in base; matching CUDA workers ship in the pack. Only one
    recognizer worker is active.
12. **Structured diagnostics — implemented.** System, GPU, driver, CUDA,
    cuDNN, worker, provider, model, recognizer, warmup, active inference, audio,
    and performance have independent states/paths/reasons/links.
13. **Real diagnostics self-test — verified.** It decodes pinned public-domain
    speech, refreshes cards after inference, and excludes recognized words/audio
    from copied output. The published CUDA run completed in 7.51 seconds with
    one worker, `CUDA Active`, provider `cuda`, and transcript exclusion.
14. **Auto fallback — implemented.** CUDA handshake/load/warmup failure fully
    terminates that process before CPU starts. `CUDA Active` requires session
    decode.
15. **Accelerator Pack — built and installed.** 416 files /
    3,328,912,698 bytes; Zip64 2,042,431,774 bytes; every file is size/SHA-256
    pinned. Installer validation and staged install passed.
16. **Qwen integration — real CPU/CUDA decode passed.** Revision
    `68818b2313fe77bd06f6a7c5068ff3ef59d02b8a`, seven payload artifacts,
    128 features, derived tokenizer directory, bounded Silero VAD, final-only
    commits. RTF: CPU 0.263, CUDA 0.404.
17. **Whisper Large v3 Turbo — real CPU/CUDA decode passed.** Q5_0 and full
    downloads passed exact size/SHA validation. Q5 RTF: CPU 4.558, CUDA 0.116.
    Full RTF: CPU 4.916, CUDA 0.064.
18. **Factory routing — implemented.** Descriptor engine/mode selects
    `SherpaOnlineEngine`, `SherpaOfflineVadEngine`, or `WhisperCppEngine`; the
    view model has no model-family switch.
19. **Model workflow/UI — implemented.** Virtualized vertical list,
    search/filter/details, row-local progress/speed/ETA/cancel/retry/validation,
    and separate Use now/Set default actions.
20. **Performance telemetry — implemented.** Decode/RTF, CPU/memory, audio
    queues, commit/injection rates, UTF-16 length, `SendInput` calls, high-water
    marks, and failures are available without transcript content.
21. **Known limitations.** Current 30-minute soak and complete multilingual /
    accented / quiet / no-trailing-silence corpus remain open. Parakeet TDT and
    Moonshine Tiny/Base remain Experimental/non-actionable. Qwen and Whisper
    remain Preview pending that corpus gate. Public GitHub release is not yet
    created.
22. **CPU publish command — verified.**

    `dotnet publish src\RSTT.App\RSTT.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\publish\cpu-only`

23. **Published paths — verified.**

    - CPU app:
      `E:\workSpace\RSTT\artifacts\publish\cpu-only\RSTT.App.exe`
    - CPU release archive:
      `E:\workSpace\RSTT\artifacts\release\RSTT-1.0.0-win-x64-cpu.zip`
      (`23ecf72615a054aacd76df95bf0d6d1179d973c3d6c4fcb88a215f305258e342`)
    - Accelerator-installed app:
      `E:\workSpace\RSTT\artifacts\publish\win-x64\RSTT.App.exe`
    - Pack:
      `E:\workSpace\RSTT\artifacts\accelerator-pack\RSTT-Accelerator-Pack-1.0.0-win-x64.zip`
    - Pack SHA-256:
      `286ee5e15ab5fcfc97bab0899a63167832423312cfcd96c7b83527410f17b443`

## Final validation

- restore passed;
- Debug and Release builds passed with zero warnings/errors;
- Debug and Release automated tests passed 85/85, zero skipped;
- clean CPU-only publish passed: 907 files / 373,062,964 bytes;
- packaged CPU worker real-audio decode passed: Parakeet RTF 0.582 on the
  11-second public-domain diagnostics sample;
- CPU-only published startup passed with both CPU workers and no CUDA worker;
- pack manifest/installer passed;
- accelerator-installed publish passed: 1,317 files / 3,701,801,512 bytes;
- accelerated published startup and single-instance checks passed;
- real CPU/CUDA Nemotron, Qwen, Whisper Q5_0, and Whisper full decodes passed.

The Accelerator Pack archive SHA-256 sidecar is:

`artifacts\accelerator-pack\RSTT-Accelerator-Pack-1.0.0-win-x64.zip.sha256`
