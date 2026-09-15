# Recognition, GPU, and shortcut fixes

## Root causes and changes

- **GPU packaging:** normal development builds could start a CUDA-named worker with CPU-only native ONNX Runtime libraries. Build and publish now combine freshly built workers with the matching app-local CUDA runtime. Readiness requires actual CUDA node placement and an inference warmup. The UI includes the inference worker's CPU and memory usage.
- **GPU model weights:** dynamically quantized INT8 graphs left substantial inference work on the CPU. CUDA now selects verified FP32 variants of Parakeet and Nemotron English; CPU retains the smaller INT8 variants. A first CUDA selection downloads its GPU weights when absent. GPU preparation and fallback failures are shown/logged. CPU Auto uses four inference threads (two for Low Power); an explicit thread limit is honored.
- **Parakeet missing sentence tails:** decoder-blank endpoint rules reset recognition state during speech. Continuous streaming now retains state through uncertain words and pauses. Stop supplies right context and drains the stream to recover buffered ending words. Offline Accuracy segmentation retains up to 20 seconds of context.
- **Nemotron missing middle words:** sherpa-onnx 1.13.4 advanced the recurrent decoder state twice across chunk boundaries. The app now uses 1.13.8, whose NeMo decoder caches the decoder output alongside its state. The exact source comparison is linked below.
- **Lost audio:** the old queue discarded audio during decode bursts; a captured diagnostic session reported 5.37 seconds lost. The new queue retains audio and reports overload as a capture failure if recognition falls more than 60 seconds behind. Persistent filtered resampling preserves callback continuity and suppresses frequencies that would alias into speech.
- **Premature text commitment:** production keeps partial hypotheses revisable until finalization or an explicit paste. A later revision cannot consume an already pasted prefix again.
- **Shortcut Confirm crash:** Windows reported a WPF exception from closing the recorder again while it was already closing. A closing guard prevents this reentrancy. A regression opens the actual compiled dialog, confirms keyboard and middle-mouse gestures, and recreates deactivation during closing.

The runtime combination is **sherpa-onnx 1.13.8 / ONNX Runtime 1.28.2 / Accelerator Pack 1.0.2 / worker protocol 2**. CUDA still uses the CPU for capture, resampling, unsupported operators, and UI work. Utilization is bursty during live listening because the GPU finishes inference before the next audio chunk arrives.

## Exact source-audio validation (2026-09-15)

Source: the user's `E:\source.mp3`, 181.477 seconds. Machine: NVIDIA GeForce RTX 2060, 6 GB VRAM. These are individual local measurements, not a general accuracy benchmark.

| Model | Original CPU decode | Updated FP32 CUDA decode | Updated real-time factor |
| --- | ---: | ---: | ---: |
| Parakeet Unified English 0.6B | 139.1 s | 16.5 s | 0.091 |
| Nemotron Streaming English 0.6B | 65.2 s | 10.6 s | 0.059 |

Decode time includes final flushing and excludes model loading. The original baseline used the old endpoint/runtime behavior and omitted text, so this table is a before/after system comparison rather than a controlled backend-only speedup.

The updated transcripts recover phrases missing in the screenshots, including the RTX architecture launch, the path-tracing explanation, "compressing geometry into clusters", "advanced level of detail", and "developing a new level of detail system". Proper-name and technical-word substitutions remain, including Alan Wake, 007, VRAM, and "path traced". A verified verbatim reference was not supplied, so no word-accuracy percentage or parity with Windows Live Captions is claimed.

A second Nemotron run fed the complete MP3 in real time, using the production resampler and audio queue with 10 ms packets. Peak queued audio was **200 ms**; cumulative worker decode time was **25.25 s** for 181.48 s of audio (RTF **0.139**). It reached the end of the source and retained the recovered phrases. ONNX Runtime verified **1,980 CUDA nodes across three model sessions**, with 769 CPU nodes. This test exercises the speech pipeline without playing audio through the user's speakers. A separate real WASAPI integration test covers device capture.

The complete regression suite passed **124 tests** (62 Core and 62 Speech), including real Windows shortcut confirmation, Unicode clipboard paste, WASAPI capture, GPU model selection, and audio continuity.

Raw reports are under `artifacts/test-results/`:

- `source-parakeet-before.json`, `source-nemotron-before.json`
- `source-parakeet-no-endpoints.json` (endpoint isolation)
- `source-parakeet-1.13.8-gpu.json`, `source-nemotron-1.13.8-gpu.json`
- `source-nemotron-realtime-final.json`, `final-live-gpu-utilization.csv`
- `tests-final.txt` (complete regression suite)
- `publish-binary-verification.json` (app/worker/runtime SHA-256 checks)
- `published-parakeet-gpu.json`, `published-nemotron-gpu.json`, `published-nemotron-cpu.json` (finished-package checks after the interruption)

## Paste and shortcut behavior

Speech collects in **Live transcript**. Focus a text input and press the configured **Paste Recognized Sentences** shortcut (MMB by default). RSTT copies the pending batch to the Windows clipboard and sends one Shift+Insert paste command. It does not send character-by-character input. The clipboard retains the text.

A paste includes the visible partial hypothesis. Later revisions retain the consumed prefix. Speech arriving during a paste remains for the next batch. Pending text survives stop/start and model reloads; Copy does not consume it, and Clear discards it. Failed pastes retain the batch. Destination applications must support clipboard paste.

Settings offers Record Shortcut, Confirm, Cancel, Reset, and Clear. Keyboard combinations, MMB, modifier+MMB, and MMB+B are supported. Conflicts preserve the previous binding. Escape or switching away cancels recording. Cleared shortcuts stay disabled after restart. Existing user settings are preserved.

## Build and run

Build the matching runtime pack before building or publishing the app:

```powershell
.\scripts\Build-AcceleratorPack.ps1 -NoArchive
dotnet build RSTT.sln -c Release
dotnet test RSTT.sln -c Release --no-restore
dotnet publish src\RSTT.App\RSTT.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\publish\accuracy-gpu-fixes
```

Normal build and publish output include CUDA workers when the local pack exists. CPU native libraries remain isolated. Without a pack, the build reports that GPU recognition is unavailable; a CUDA-named CPU worker is not accepted as proof of GPU use.

The prepared local build is `artifacts/publish/accuracy-gpu-fixes/RSTT.App.exe`. Exit the old running RSTT instance before launching it. Both verified FP32 model variants have been installed in the local RSTT model directory. After the interruption, the published app was started with the saved CUDA setting. Its normal startup log confirms Nemotron loaded in the isolated CUDA worker (1.13.8). The app remains running, ready to listen.

## Native runtime references

- [NeMo decoder in sherpa-onnx 1.13.4](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.4/sherpa-onnx/csrc/online-transducer-greedy-search-nemo-decoder.cc)
- [NeMo decoder output cache in sherpa-onnx 1.13.8](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/online-transducer-greedy-search-nemo-decoder.cc)
- [Pinned sherpa-onnx 1.13.8 release](https://github.com/k2-fsa/sherpa-onnx/releases/tag/v1.13.8)
