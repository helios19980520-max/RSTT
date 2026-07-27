# Local speech models

RSTT V1 uses the installed `org.k2fsa.sherpa.onnx` C# runtime and expects a local, streaming English model. The default profile is **Fast English**, an online Zipformer/transducer-compatible configuration. This is selected over a Parakeet profile because it is a mature streaming layout supported directly by the current sherpa-onnx C# API; no Python bridge or fragile subprocess is involved. The model is deliberately external to keep the application download small and to make licensing explicit.

## Install a model

1. Obtain a streaming English transducer model compatible with the installed sherpa-onnx runtime from the [official sherpa-onnx model documentation](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/online-transducer/index.html).
2. Review that model's licence before use or redistribution.
3. Create `%LOCALAPPDATA%\Helios\RSTT\Models\FastEnglish`.
4. Copy the model files into that directory.
5. Create `model.json` in the same directory. Use [model.json.example](model.json.example), updating every filename to match the downloaded package.
6. Restart RSTT. Its status changes to `Model ready` only after every declared file exists.

The process uses no API token. After these files are present, recognition does not need network access.

## Manifest format

```json
{
  "id": "FastEnglish",
  "displayName": "Fast English — streaming Zipformer",
  "engine": "online-transducer",
  "numThreads": 2,
  "provider": "cpu",
  "files": {
    "encoder": "encoder-epoch-99-avg-1.int8.onnx",
    "decoder": "decoder-epoch-99-avg-1.onnx",
    "joiner": "joiner-epoch-99-avg-1.int8.onnx",
    "tokens": "tokens.txt",
    "vad": "silero_vad.onnx"
  }
}
```

`vad` is optional. If omitted, RSTT uses a conservative local energy gate; adding a compatible local Silero VAD model gives better segmentation. RSTT validates every file that is declared in the manifest.

Supported `engine` values are:

- `online-transducer` — requires `encoder`, `decoder`, `joiner`, and `tokens`.
- `online-zipformer2-ctc` — requires `model` and `tokens`.
- `online-nemo-ctc` — requires `model` and `tokens`.

Keep model configuration external. This allows future model choices without putting sherpa-specific details into the WPF UI or Core projects.
