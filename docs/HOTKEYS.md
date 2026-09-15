# Global shortcuts

| Default | Action |
| --- | --- |
| Ctrl+Alt+R | Start/stop listening |
| MMB | Paste Recognized Sentences |
| Ctrl+Alt+C | Toggle captions |

In Settings, click **Record Shortcut**, physically press the combination, release
it, and click **Confirm**. Keyboard shortcuts need at least one modifier. MMB
alone, modifier+MMB, and MMB+key work. Hold MMB before pressing the accompanying
key. Left/right mouse clicks stay available for the recorder buttons.

Escape, Cancel, closing the recorder, or switching away cancels recording.
Reset restores that action's default; Clear unregisters and disables it. Changes
are saved only after registration succeeds. Keyboard conflicts with Windows or
other applications and duplicate RSTT bindings keep the previous working binding.
Other applications' low-level mouse hooks cannot be enumerated for conflicts.

Recording suppresses RSTT actions. Injected input is ignored by recorder/mouse
hooks, and repeated key-down events do not repeatedly trigger an MMB chord.
See [implementation and validation](PASTE_AND_RECOGNITION_UPDATE.md).
