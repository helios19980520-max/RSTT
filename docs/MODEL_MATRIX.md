# Verified model matrix

This matrix describes the RSTT integration, not every capability of the upstream research model. “Available” means download, validation, load, recognition, activation, and deletion are wired through a native RSTT engine.

| Model | Engine | Languages | Streaming | Verified backend | Payload | Profile | Accuracy note | License | Status |
| --- | --- | --- | --- | --- | ---: | --- | --- | --- | --- |
| Nemotron Streaming English 0.6B INT8 | isolated sherpa-onnx online worker | English | Native, cache-aware | CPU + RTX 2060 CUDA | 631 MiB | Balanced, 560 ms | Exact final text; RTF 0.411 CPU / 0.311 CUDA | NVIDIA Open Model License | Available, recommended |
| Nemotron 3.5 Streaming Multilingual 0.6B INT8 | sherpa-onnx online transducer with stream language option | 19 transcription-ready locales plus documented broader coverage | Native, cache-aware | CPU architecture/integration | 651 MiB | Balanced, 560 ms | Not locally accuracy-benchmarked | OpenMDW 1.1 | Available |
| Parakeet Unified English 0.6B INT8 | sherpa-onnx online transducer | English | Buffered | CPU | 632 MiB | Accurate, 1120 ms | Strong punctuation/capitalization; materially more CPU here | NVIDIA Open Model License | Available |
| Parakeet TDT 0.6B v3 INT8 | sherpa offline/VAD route | 25 European languages | Segmented realtime | None | — | VAD 200/500 ms initial policy | Engine route exists; no pinned RSTT artifact/corpus validation | NVIDIA Open Model License | Experimental, non-actionable |
| Qwen3-ASR 0.6B INT8, 2026-03-25 | isolated sherpa offline/VAD worker | 30 languages plus 22 Chinese dialects | Segmented realtime | CPU + RTX 2060 CUDA | 942 MiB | VAD 200/500 ms, 20 s maximum | Meaningful final text; RTF 0.263 CPU / 0.404 CUDA; 128 features | Apache-2.0 | Preview, actionable |
| Moonshine Tiny English INT8 | sherpa offline/VAD route | English | Segmented realtime | None | — | VAD 200/500 ms initial policy | Engine route exists; no pinned RSTT artifact/corpus validation | MIT | Experimental, non-actionable |
| Moonshine Base English INT8 | sherpa offline/VAD route | English | Segmented realtime | None | — | VAD 200/500 ms initial policy | Engine route exists; no pinned RSTT artifact/corpus validation | MIT | Experimental, non-actionable |
| Whisper Large v3 Turbo Q5_0 | isolated whisper.cpp worker | Multilingual, auto/manual language | Segmented realtime | CPU + RTX 2060 CUDA | 548 MiB | VAD 200/500 ms, 20 s maximum | Exact final text; RTF 4.558 CPU / 0.116 CUDA | MIT | Preview, actionable |
| Whisper Large v3 Turbo full | isolated whisper.cpp worker | Multilingual, auto/manual language | Segmented realtime | CPU + RTX 2060 CUDA | 1,550 MiB | VAD 200/500 ms, 20 s maximum | Exact final text; RTF 4.916 CPU / 0.064 CUDA | MIT | Preview, actionable |
| Whisper Tiny/Base/Small/Medium | planned whisper.cpp profiles | Multilingual | Segmented realtime | None | — | — | Not benchmarked in RSTT | MIT | Coming later, non-actionable |

## Capability boundaries

- Current RSTT results expose text, partial/final state, sequence, time, and language. Word timestamps are not exposed, even if an upstream model/runtime can produce them.
- No current model exposes hotword/custom-vocabulary controls.
- Nemotron 3.5 applies the configured language through `OnlineStream.SetOption("language", ...)`; `auto` is offered only for that integration.
- CUDA evidence above means the named model completed a real decode through the
  packaged RTX 2060 worker. It does not imply that CUDA is faster for every
  model or clip.
- Preview entries are downloadable and usable. Qwen and both Whisper profiles
  have real-audio CPU/CUDA evidence, but remain Preview until the full
  multilingual/accented/quiet/no-trailing-silence corpus gate passes.
- Experimental and coming-later entries have no artifacts, install directory,
  download, **Use now**, or default action.

## Source and revision policy

Available entries pin a sherpa-onnx export revision plus exact delivered byte counts and SHA-256 values in `src/RSTT.Speech/Assets/model-catalog.json`. Directory names include model/profile/export identity so an incompatible update cannot silently reuse a prior installation.

Primary upstream references:

- [sherpa-onnx Nemotron streaming documentation](https://k2-fsa.github.io/sherpa/onnx/nemo/nemotron-streaming.html)
- [NVIDIA Nemotron Speech Streaming English](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b)
- [NVIDIA Nemotron 3.5 ASR Streaming](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b)
- [NVIDIA Parakeet Unified English](https://huggingface.co/nvidia/parakeet-unified-en-0.6b)
- [sherpa-onnx Parakeet TDT v3](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-transducer/parakeet.html)
- [sherpa-onnx Qwen3-ASR exports](https://k2-fsa.github.io/sherpa/onnx/qwen3-asr/pretrained.html)
- [Qwen3-ASR model card](https://huggingface.co/Qwen/Qwen3-ASR-0.6B)
- [sherpa-onnx Moonshine models](https://k2-fsa.github.io/sherpa/onnx/moonshine/index.html)
