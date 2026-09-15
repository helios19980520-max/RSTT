# Paste recognized sentences

RSTT no longer automatically types recognized characters or sentences.

1. Let speech accumulate in Live Transcript.
2. Focus an input in another application.
3. Press **MMB**, or the recorded **Paste Recognized Sentences** shortcut.
4. The pending batch is copied to the clipboard and pasted using Shift+Insert.
5. Successfully submitted text disappears; newly recognized speech remains.

Physical keys must be released before paste. Clipboard, focus, and Windows input
failures retain the pending batch. Copy preserves the buffer; Clear discards it.
The clipboard keeps the pasted text. Applications must support normal paste;
Windows accepting the command does not guarantee that a custom editor uses it.

See [implementation and validation](PASTE_AND_RECOGNITION_UPDATE.md).
