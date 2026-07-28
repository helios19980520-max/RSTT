# Text injection

RSTT sends finalized transcript text to the current foreground application with Windows `SendInput`. It does not use the clipboard, paste commands, or keyboard-layout-dependent character mappings.

## Production commit policy

Live ASR partials can revise words as context arrives. Caption updates are therefore responsive, but irreversible typing uses `FinalOnlyCommitPolicy`: only an endpoint-final segment enters the injection channel.

This is intentionally more conservative than stable-prefix typing. It avoids committing plausible-looking partial fragments that the model later rewrites. A future model may opt into another `ITranscriptCommitPolicy` only after model-specific validation.

Turning typing on does not replay caption history. Turning it off immediately prevents future enqueueing, and queued requests recheck the setting/listening generation before delivery.

## Delivery contract

For each segment RSTT:

1. resolves the current foreground window and process immediately before sending;
2. rejects a missing target and RSTT's own process;
3. submits UTF-16 `KEYEVENTF_UNICODE` down/up pairs in batches;
4. rechecks the target every 16 UTF-16 code units;
5. serializes segments through one bounded worker; and
6. pauses 2 ms between batches so target controls can process messages.

If focus changes during a segment, delivery stops instead of continuing into the new application. An already delivered prefix cannot be recalled. Future complete segments resolve the new foreground target, so switching from Notepad to Word routes later text to Word.

The x64 `INPUT` structure is explicitly laid out to match the Windows ABI.

## Isolation from capture

Text injection never runs on the WASAPI callback, audio conversion worker, recognition call, or WPF dispatcher. A slow target cannot block capture or decoding. The bounded injection queue preserves order while preventing unbounded memory growth.

Repeated self-focus or target warnings are rate-limited and status-deduplicated. Self-focus is Debug-level because opening RSTT during recognition is normal.

## Caption focus behavior

The overlay uses `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `ShowActivated=false`, and a no-activation window style. Caption show/update operations do not call `Activate()`.

## Windows integrity levels

Windows User Interface Privilege Isolation can block a normal application from sending input to an elevated Administrator window. RSTT intentionally:

- does not request Administrator privileges;
- does not use `uiAccess`;
- does not inject code into other processes; and
- does not bypass UIPI.

Run both applications at the same integrity level when typed output is required. Captions continue when injection is unavailable.

## Other limitations

- The target must accept Unicode keyboard packets in an editable control.
- Secure desktop, credential prompts, protected controls, some games, remote sessions, and custom input stacks may reject synthetic input.
- IMEs, autocorrect, application shortcuts, and editor behavior can transform delivered input.
- Text already delivered before a target change cannot be retracted.
