# Verified model matrix

This matrix describes the RSTT integration, not every capability of the upstream research model. “Available” means download, validation, load, recognition, activation, and deletion are wired through a native RSTT engine.

| Model | Engine | Languages | Streaming | Verified backend | Payload | Profile | Accuracy note | License | Status |
| --- | --- | --- | --- | --- | ---: | --- | --- | --- | --- |
| Nemotron Streaming English 0.6B INT8 | sherpa-onnx online transducer | English | Native, cache-aware | CPU | 631 MiB | Balanced, 560 ms | Official family publishes WER; local functional/performance test passed | NVIDIA Open Model License | Available, recommended |
| Nemotron 3.5 Streaming Multilingual 0.6B INT8 | sherpa-onnx online transducer with stream language option | 19 transcription-ready locales plus documented broader coverage | Native, cache-aware | CPU architecture/integration | 651 MiB | Balanced, 560 ms | Not locally accuracy-benchmarked | OpenMDW 1.1 | Available |
| Parakeet Unified English 0.6B INT8 | sherpa-onnx online transducer | English | Buffered | CPU | 632 MiB | Accurate, 1120 ms | Strong punctuation/capitalization; materially more CPU here | NVIDIA Open Model License | Available |
| Qwen3-ASR 0.6B INT8 | Planned separate offline engine | Multilingual | Offline | None | — | — | Not benchmarked in RSTT | See upstream model card | Coming later |
| Whisper Small | Planned whisper.cpp engine | Multilingual | Segmented/VAD plan, not native streaming | None | — | — | Not benchmarked in RSTT | Upstream artifact terms | Coming later |

## Capability boundaries

- Current RSTT results expose text, partial/final state, sequence, time, and language. Word timestamps are not exposed, even if an upstream model/runtime can produce them.
- No current model exposes hotword/custom-vocabulary controls.
- Nemotron 3.5 applies the configured language through `OnlineStream.SetOption("language", ...)`; `auto` is offered only for that integration.
- CUDA compatibility on a model card means the model/runtime family can use CUDA. It does not mean this CPU package executed CUDA.
- Coming-later entries have no artifacts, install directory, download, **Use model**, or default action.

## Source and revision policy

Available entries pin a sherpa-onnx export revision plus exact delivered byte counts and SHA-256 values in `src/RSTT.Speech/Assets/model-catalog.json`. Directory names include model/profile/export identity so an incompatible update cannot silently reuse a prior installation.

Primary upstream references:

- [sherpa-onnx Nemotron streaming documentation](https://k2-fsa.github.io/sherpa/onnx/nemo/nemotron-streaming.html)
- [NVIDIA Nemotron Speech Streaming English](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b)
- [NVIDIA Nemotron 3.5 ASR Streaming](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b)
- [NVIDIA Parakeet Unified English](https://huggingface.co/nvidia/parakeet-unified-en-0.6b)
