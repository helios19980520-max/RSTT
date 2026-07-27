# Text injection

RSTT targets the current foreground application with the Windows `SendInput` API. Each UTF-16 code unit is emitted using `KEYEVENTF_UNICODE`; RSTT does not normally change the clipboard, paste text, or map words to a physical keyboard layout.

Before injecting stable text, RSTT verifies that:

1. typing is enabled;
2. the application is listening;
3. the text is non-empty and newly confirmed;
4. a foreground window exists; and
5. the foreground process is not RSTT itself.

Input is serialised so segments retain their order. An injection error is logged locally without stopping captions or ASR.

## Focus and captions

The caption overlay sets `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW`, is shown with `ShowActivated=false`, and uses no call to `Activate()`. Updating a caption therefore does not take focus from Word, a browser field, Notepad, or another destination.

## Windows integrity levels (UIPI)

Windows prevents a normal application from sending input to many elevated Administrator windows. RSTT does not try to bypass this protection. In that situation captions remain available, while text injection reports a subtle failure in local logs. Use applications at the same integrity level if injection is required.
