# Global hotkeys

RSTT implements application-wide shortcuts with Win32 `RegisterHotKey`, an `HwndSource` message hook, and `WM_HOTKEY`. The UI labels are backed by actual registrations.

## Defaults

| Shortcut | Command |
| --- | --- |
| `Ctrl+Alt+R` | Start or stop listening through the same command used by Dashboard/tray |
| `Ctrl+Alt+T` | Toggle text injection immediately |
| `Ctrl+Alt+C` | Toggle caption visibility without stopping recognition |

Windows can reject a default because another application already registered it. On the acceptance PC, `Ctrl+Alt+R` was already owned; RSTT surfaced the conflict and successfully registered `Ctrl+Alt+Shift+R` as the replacement.

## Parser

A shortcut must contain at least one modifier and one supported key.

Modifiers:

- `Ctrl`
- `Alt`
- `Shift`
- `Win`

Keys:

- letters and digits;
- `F1` through `F24`;
- `Space`, `PageUp`, `PageDown`, `Home`, and `End`.

Registrations include `MOD_NOREPEAT`, so holding the combination does not flood commands.

## Transactional replacement

When a shortcut edit loses focus:

1. the parser validates it;
2. RSTT registers a new temporary ID while the prior shortcut remains live;
3. if registration succeeds, the prior ID is unregistered and settings are saved;
4. if registration fails, the temporary state is removed, the old registration remains, the field reverts, and an inline error appears.

This avoids the common failure where an invalid/conflicting edit silently leaves the application with no hotkey.

## Lifecycle

The window handle and hook are initialized at `SourceInitialized` with a Loaded fallback. Configured shortcuts are registered after settings load. On shutdown, all owned IDs are unregistered and the hook is removed.

IDs are scoped to the RSTT window and are not fixed across processes. Incoming `WM_HOTKEY` IDs map to `RsttHotkey` commands.

## Command semantics

Hotkeys delegate to the same view-model/session operations as buttons and tray items:

- start/stop remains serialized by the session lifecycle semaphore;
- toggling typing does not wait for another recognition result and queued requests recheck the state;
- toggling captions changes only overlay visibility.

Normal hotkey handling does not activate the main RSTT window.

## Diagnostics

Lifecycle registration success is Information. Conflicts are Warning with the specific shortcut and user-facing reason. Received hotkeys are Information for manual acceptance. No per-key keyboard hook is installed; RSTT receives only its registered combinations.
