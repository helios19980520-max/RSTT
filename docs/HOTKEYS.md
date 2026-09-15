# Global shortcuts

| Default | Action |
| --- | --- |
| Ctrl+Alt+R | Start/stop listening |
| MMB | Paste Recognized Sentences |
| Ctrl+Alt+C | Toggle captions |

In Settings, click **Record Shortcut**, physically press the combination, release
it, and click **Confirm**. Single letters, digits, punctuation, and numpad keys
work with or without modifiers: for example `K`, `9`, `[`, `\`, and `NumPadAdd`
(the numpad `+` key). MMB alone, modifier+MMB, and MMB+key also work. Hold MMB
before pressing the accompanying key. Left/right mouse clicks stay available
for the recorder buttons.

Numpad keys have separate bindings from the main keyboard: `NumPad9` differs
from `9`, and `NumPadAdd` differs from `Plus`. Bindings use Windows virtual-key
codes; punctuation labels use US key names. Saved
codes such as `VK_6B` and `VK_DC` are also accepted and shown as `NumPadAdd` and
`\`. The codes follow [Windows virtual-key definitions](https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes).

Escape, Cancel, closing the recorder, or switching away cancels recording.
Reset restores that action's default; Clear unregisters and disables it. Changes
are saved only after registration succeeds. Keyboard conflicts with Windows or
other applications and duplicate RSTT bindings keep the previous working binding.
Other applications' low-level mouse hooks cannot be enumerated for conflicts.

Recording suppresses RSTT actions. Injected input is ignored by recorder/mouse
hooks, and repeated key-down events do not repeatedly trigger an MMB chord.
See [implementation and validation](PASTE_AND_RECOGNITION_UPDATE.md).
