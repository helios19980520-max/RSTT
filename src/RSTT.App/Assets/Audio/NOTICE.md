# Diagnostics audio

`jfk.wav` is the 16 kHz mono validation sample distributed in the
[`ggml-org/whisper.cpp`](https://github.com/ggml-org/whisper.cpp) repository.
It contains an excerpt from President John F. Kennedy's 1961 inaugural address,
a work of the United States federal government in the public domain.

- Upstream file:
  `https://github.com/ggml-org/whisper.cpp/raw/97c56f1dc1d1100a9d859c865a20c82d22f823ed/samples/jfk.wav`
- Pinned upstream revision: `97c56f1dc1d1100a9d859c865a20c82d22f823ed`
- SHA-256: `59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e`
- Intended use: local, transcript-free speech-engine diagnostics only

The diagnostics report records only success, timing, and result counts. It does
not copy the recognized words into logs, telemetry, or the clipboard.
