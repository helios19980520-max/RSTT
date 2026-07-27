# Model management

RSTT V1 supports one curated model: **Parakeet Unified English, INT8, buffered streaming at approximately 1.12 seconds**. The application downloads and manages it; users do not create a manifest by hand.

## Why this profile

Parakeet Unified English is a 600M-parameter FastConformer RNN-T trained for both offline and streaming English recognition, with punctuation and capitalization. The 1.12-second converted profile provides a practical accuracy/latency tradeoff for CPU-based desktop captions. sherpa-onnx 1.13.4 selects the buffered RNN-T path automatically from the exported ONNX metadata.

The model is English-only. V1 does not expose alternative models or languages that have not been integrated and verified through the same managed lifecycle.

## Install through RSTT

1. Open **Models**.
2. Select **Download & verify**.
3. Leave RSTT running until the state reaches **Ready**.

The total model payload is 663,048,980 bytes (about 632 MiB). The installation directory is:

```text
%LOCALAPPDATA%\Helios\RSTT\Models\parakeet-unified-en-0.6b-int8-streaming-1120ms
```

Cancel leaves resumable `.partial` data. Starting again requests the remaining bytes when the server supports HTTP Range. Delete removes the managed model directory and unloads the recognizer.

## Artifact contract

| Role | File | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| Encoder | `encoder.int8.onnx` | 654,046,391 | `1c03f1192de41771384af22972ca10203613ba56197a024f275b86727cd35911` |
| Decoder | `decoder.int8.onnx` | 7,257,777 | `34fea72425d2506600772ba191a6d3f99c0710abdb68d9a3dc89fa8cb2aa473a` |
| Joiner | `joiner.int8.onnx` | 1,735,860 | `869f43f7d24595c55581ad3bf249a935fb8a71389fbdaa7504b9f46f93140f8a` |
| Tokens | `tokens.txt` | 8,952 | `dc0b4584ab2e4ddbf888425c076c61b736e7356a015250db7d307e6f1a8188ff` |

These SHA-256 values are calculated over the reconstructed downloaded files. They are intentionally not copied from Hugging Face Xet/CDN ETags, which identify storage objects and are not guaranteed to equal the delivered file hash.

Downloads originate from:

```text
https://huggingface.co/csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-streaming-1120ms
```

This is a sherpa-onnx maintainer export of [NVIDIA Parakeet Unified English](https://huggingface.co/nvidia/parakeet-unified-en-0.6b).

## Integrity and recovery

Each artifact downloads to `filename.partial`. RSTT verifies its exact byte length and SHA-256 before replacing the final file. `model.json` is generated last, so an interrupted install cannot appear ready.

At startup, RSTT checks:

- the expected model identity;
- a complete manifest;
- paths that stay inside the managed model directory;
- all required artifacts; and
- exact file lengths.

If validation fails, recognition stays disabled and the Models page remains available for retry or delete/reinstall. RSTT never falls back to an online recognizer.

## Generated manifest

A completed installation contains a generated manifest similar to:

```json
{
  "id": "ParakeetUnifiedEnInt8",
  "displayName": "Parakeet Unified English",
  "engine": "online-transducer",
  "files": {
    "encoder": "encoder.int8.onnx",
    "decoder": "decoder.int8.onnx",
    "joiner": "joiner.int8.onnx",
    "tokens": "tokens.txt"
  },
  "numThreads": 4,
  "provider": "cpu",
  "featureDimension": 128
}
```

Thread count is derived from the machine and clamped to 1–4. V1 uses CPU inference; no CUDA runtime is required.

## Offline and privacy boundary

Internet access is used only for the explicit model installation. After a verified install, model load and recognition read local files and do not call Hugging Face, NVIDIA, or a transcription service.

The model is not bundled with source or publish output. Review the model terms in [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) before redistribution.
