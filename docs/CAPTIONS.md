# Caption engine

Caption V2 models finalized history and the current revisable hypothesis separately.

## Data model

`CaptionHistory` contains:

- a bounded queue of finalized `CaptionSegment` records; and
- at most one current non-final segment.

Each segment has an ID, text, final flag, timestamp, and recognition sequence. A new partial replaces the prior partial in place. A final becomes history and clears the partial. The in-memory final queue is capped at 100; the overlay renders only its configured visible count.

## Auto-follow

The overlay follows the latest content when:

- a final segment is appended;
- the current partial changes; or
- it is first shown.

If the user scrolls materially above the bottom, auto-follow pauses and **Jump to latest** appears. Pressing it restores follow mode and moves to the end. New recognition does not forcibly pull a user away from older text while follow is paused.

## Move, resize, and lock

The overlay has three interaction states:

- **Locked** — no drag or resize; toolbar actions remain available on hover.
- **Unlocked** — drag from the surface/toolbar and resize from Windows window edges/corners.
- **Hidden** — recognition and caption state continue; `Ctrl+Alt+C` shows the current state again.

Bounds, width, height, lock state, opacity, font size, line spacing, maximum visible lines, topmost, and status visibility persist in settings.

The first position is bottom-center. Restored bounds are clamped to the current virtual desktop so a removed display cannot strand the overlay off-screen. WPF device-independent coordinates are retained across DPI changes instead of repeatedly applying manual scale multipliers.

## Focus behavior

The caption window uses:

- `ShowActivated=false`;
- `WS_EX_NOACTIVATE`;
- `WS_EX_TOOLWINDOW`; and
- a stable `Topmost` binding when the setting is enabled.

It does not activate itself for normal show/update operations and does not toggle topmost repeatedly. Verified acceptance preserved the foreground HWND while the overlay was moved/resized with no activation.

## Visual behavior

Final text uses the primary caption color. The current partial uses a quieter color to communicate that it may revise. The toolbar appears on hover and includes listening status, font controls, lock/unlock, and close. The surface uses a restrained dark translucent background without continuous blur or animation.

## Performance

Recognition can generate internal hypotheses faster than a person needs them rendered. Final updates publish immediately; partial UI updates are coalesced to a 50 ms interval (20 Hz maximum). Auto-scroll is scheduled on the WPF dispatcher after layout. Old caption objects are removed rather than hidden indefinitely.

## Settings

- Show caption overlay
- Font size
- Opacity
- Width and height
- Keep above other windows
- Show stable text only
- Lock position
- Listening-status indicator
- Visible caption lines

The Preview action displays sample text without starting capture and without taking keyboard focus.
