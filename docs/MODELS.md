# Model management

RSTT uses an embedded, versioned ASR catalog. Catalog metadata is available offline; model payloads are downloaded only after an explicit user action.

## Integration states

- **Available** — the engine path, artifacts, validation, load, and activation are implemented.
- **Experimental** — executable integration exists but has stated limitations.
- **Coming later** — roadmap metadata only. No download or activation command is exposed.

The current catalog has three Available sherpa-onnx models and two non-activatable roadmap entries. See [MODEL_MATRIX.md](MODEL_MATRIX.md).

## Recommended default

**Nemotron Streaming English 0.6B INT8, 560 ms** is the default English realtime model. On the test machine it reduced continuous-speech CPU from 44.86% with the original four-thread buffered Parakeet configuration to 1.88% average while keeping RTF below 1. It is cache-aware, so it reuses encoder context rather than repeatedly recomputing a large overlapping window.

Parakeet remains an installed/available accuracy-oriented English alternative. Nemotron 3.5 is the available multilingual native-streaming option.

## Install lifecycle

1. RSTT checks free space for remaining payload plus working headroom.
2. Each artifact downloads to `Models\.downloads\<model-id>\<file>.partial`.
3. A valid partial is resumed with HTTP Range; an invalid range response restarts safely.
4. Progress includes current file, file count, bytes, total, rate, ETA, percentage, and state.
5. Exact length and SHA-256 are verified.
6. A generated `model.json` manifest is written only after required files pass.
7. The staging directory is promoted into the versioned installation directory.
8. Settings activate the model only after promotion succeeds.

Cancellation leaves valid partial data for resume. A failed validation never appears as Ready.

## Integrity contract

The embedded catalog pins:

- a versioned model ID and directory;
- upstream/export revision;
- artifact URL;
- safe local file name;
- exact byte count; and
- SHA-256 of the delivered file.

Hugging Face/Xet ETags are not treated as file SHA-256 values. Startup validation checks model identity, manifest completeness, required roles, safe paths, file existence, and exact sizes. The install path cannot escape the managed model root.

## Activation and deletion

Selecting **Use model** stops active recognition first through the normal session lifecycle, resets stabilizer state, loads the chosen verified model, and updates the default only after load succeeds. The UI does not replace native resources underneath an active decode call.

Deleting an active model unloads it first. RSTT selects another valid installed model when possible; otherwise the shell remains usable on the Models page. The confirmation includes the model name and storage size.

## Local paths

```text
%LOCALAPPDATA%\Helios\RSTT\Models\<versioned-model-directory>
%LOCALAPPDATA%\Helios\RSTT\Models\.downloads\<model-id>
```

The publish output never includes model payloads.

## Licensing

Each model card/detail view reports the catalog's actual license label and upstream link. “Free” is not used as a substitute for license terms. Users and redistributors should review [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) and the linked upstream license before distribution.
