# Text injection

RSTT sends newly stable transcript text to the current foreground application with Windows `SendInput`. It does not use the clipboard, simulate paste, or map words through the active physical keyboard layout.

## Delivery contract

Before and during a segment, RSTT verifies that:

1. typing is enabled and listening is active;
2. the segment is non-empty and newly confirmed;
3. a foreground window and process can be identified;
4. the foreground process is not RSTT itself; and
5. the same process remains foreground while characters are emitted.

Every UTF-16 code unit is submitted as a `KEYEVENTF_UNICODE` down/up pair. Calls are serialized so recognition segments retain their order. Character pairs are briefly paced because modern WinUI controls can coalesce a large, immediate `VK_PACKET` burst.

RSTT targets x64 Windows. Its managed `INPUT` layout is explicitly 40 bytes with the native anonymous union at byte 8, matching the Windows x64 ABI.

If focus changes partway through a segment, RSTT stops that segment instead of continuing into the new application. The already delivered prefix cannot be recalled. The failure is logged without stopping captions or recognition.

## Stable text only

Streaming ASR text can change as context arrives. `TranscriptStabilizer` confirms a common prefix across ordered hypotheses and exposes only the newly committed suffix to injection. A final result flushes the remaining pending text.

Consequences:

- raw partial hypotheses are never typed;
- a committed segment is submitted once;
- turning typing on does not dump old caption history; and
- captions can remain responsive while typed output is conservative.

## Focus and captions

The caption overlay uses `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW`, sets `ShowActivated=false`, and never calls `Activate()`. Showing or updating captions therefore does not take focus from Notepad, a browser field, Word, or another destination.

RSTT also refuses to type into its own process. Use the overlay or another window when testing live typing.

## Windows integrity levels (UIPI)

Windows User Interface Privilege Isolation prevents a normal application from sending input to many elevated Administrator windows. `SendInput` can report failure or simply deliver no usable input when Windows blocks the target.

RSTT intentionally:

- does not request Administrator privileges;
- does not use `uiAccess`;
- does not inject code into another process; and
- does not bypass UIPI.

Use both applications at the same integrity level if typed output is required. Captions remain available when injection is blocked.

## Other limitations

- The destination must expose an editable control and accept Unicode keyboard packets.
- Secure desktop, credential prompts, some games, remote sessions, protected controls, and applications with custom input stacks may reject synthetic input.
- Focus must remain on the intended process for the short duration of a stable segment.
- Applications can transform input through autocorrect, shortcuts, IMEs, or editor-specific behavior after RSTT delivers it.
