# Text injection

RSTT sends only `TranscriptCommit` values to Windows text targets. Caption
properties and raw recognition hypotheses never enter the injection path.

## Exact-text invariant

The irreversible boundary is:

```text
RecognitionHypothesis
  -> ITranscriptCommitPolicy
  -> TranscriptCommit
  -> InjectionRequest
  -> one Win32TextInjectionService worker
  -> paired Unicode INPUT records
  -> SendInput
```

`TranscriptCommit.Text` is assigned directly to `InjectionRequest.Text`. Session,
commit, and source-sequence IDs are retained. Boundary diagnostics record the
UTF-16 length and a truncated SHA-256 value; normal logs never record transcript
content.

## Commit policy

- Native and buffered online engines use `StablePrefixCommitPolicy`: whole-word
  boundaries, two consecutive confirmations, a two-word holdback, duplicate
  suppression, and an unconditional final-tail flush.
- Offline/VAD engines use `FinalOnlyCommitPolicy`.
- A session generation ID is present on audio, hypotheses, commits, captions,
  and injection requests. Late work from an old generation is rejected.

## Win32 delivery

`Win32TextInjectionService` owns a bounded 64-item channel and one consumer.
Every accepted request retains the exact foreground HWND and process ID captured
at enqueue time.

For each UTF-16 code unit, including both halves of a surrogate pair, the
service creates a `KEYEVENTF_UNICODE` key-down record and a matching Unicode
key-up record. Every record carries the RSTT `dwExtraInfo` marker.

Delivery is target-aware:

- **Direct** submits up to 64 UTF-16 units in one call with no pacing.
- **Compatibility** submits one paired UTF-16 unit per call and yields 20 ms
  after every unit, including the last unit of a commit.
- **Automatic** selects Compatibility only for the packaged Windows 11 Notepad
  executable and Direct for other targets.

The exact HWND is checked before and after every block. Compatibility's
single-unit exception is intentional: measured Notepad 11.2604.5.0 corruption
continued with 8- and 4-unit blocks, and sustained delivery still lost/duplicated
characters with 2-unit blocks. Correctness therefore takes priority over the
original 8-unit/2 ms design target for this one application.

The service tracks `SendInput`'s accepted record count:

- accepted records are never replayed;
- an even partial advances only complete UTF-16 units;
- an odd partial sends only the missing cleanup key-up, then advances that unit;
- at most three positive-progress continuations are permitted;
- a zero send or exhausted continuation budget aborts the remainder;
- a foreground HWND change returns `TargetChanged` and never redirects the tail.

Results include status, expected/sent record counts, committed UTF-16 offset,
target HWND/PID, Win32 error, and a diagnostic. Elevated-target status comes
from integrity-level comparison because `SendInput` alone cannot identify UIPI.
No clipboard fallback is used.

Self-focused requests are rejected before enqueueing. Text recognized while
RSTT is focused therefore cannot form a backlog that types after focus leaves.

## Verification

`tests/RSTT.Input.TestHost` is a dedicated WinForms process containing a native
edit control. Integration tests focus that control, inject text, and assert
exact equality for Latin text, punctuation, Japanese, Korean, accented Latin,
emoji/surrogate pairs, long text, consecutive commits, and 100 repetitions of
the required sentence. Tests contain no Notepad-owner check and no skip path.

`tests/RSTT.Notepad.IntegrationRunner` is the modern-Notepad gate. It creates a
uniquely named empty temporary file, obtains a fresh Notepad HWND even when
Notepad is already running, reads the document through UI Automation, saves only
the temporary document, verifies the saved file, and closes only the test
window. It fails rather than reusing a user document. Its acceptance-only
foreground watchdog prevents unrelated desktop activation from invalidating the
long run; production RSTT never takes focus back after a target change.

The 2026-07-28 run on Notepad 11.2604.5.0 passed 100 commits and 6,500 UTF-16
units with exact UIA and saved-file equality. Measured injection time was
208,593.853 ms. The evidence is written to
`artifacts/test-results/notepad-integration.json`.

The deterministic Win32-adapter suite covers complete, zero, even-partial,
odd-partial, continuation exhaustion, target change, self-focus, elevated
target, ordering, bounded-queue, and cancelled prior-generation behavior.

## Windows limitations

Windows UIPI can block a normal process from typing into an elevated target.
Secure desktop, protected controls, remote sessions, IMEs, autocorrect, and
custom input stacks can also reject or transform synthetic input. Compatibility
delivery is deliberately slower (about 50 UTF-16 units/second) because the
measured Notepad build corrupts faster `VK_PACKET` sequences. RSTT does not
request elevation, use `uiAccess`, inject code, or use the clipboard.
