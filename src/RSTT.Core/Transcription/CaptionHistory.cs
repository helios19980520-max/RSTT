using RSTT.Core.Models;

namespace RSTT.Core.Transcription;

public sealed record CaptionHistorySnapshot(
    IReadOnlyList<CaptionSegment> FinalSegments,
    CaptionSegment? CurrentPartial);

/// <summary>Keeps bounded final segments and replaces, rather than appends, the current partial.</summary>
public sealed class CaptionHistory
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<CaptionSegment> _finalSegments;
    private CaptionSegment? _currentPartial;
    private long _nextId;

    public CaptionHistory(int capacity = 100)
    {
        _capacity = Math.Clamp(capacity, 5, 500);
        _finalSegments = new Queue<CaptionSegment>(_capacity);
    }

    public CaptionHistorySnapshot Apply(TranscriptUpdate update)
    {
        lock (_gate)
        {
            if (update.IsFinal && !string.IsNullOrWhiteSpace(update.FinalizedSegmentText))
            {
                _finalSegments.Enqueue(new CaptionSegment(
                    ++_nextId,
                    update.FinalizedSegmentText,
                    true,
                    DateTimeOffset.UtcNow,
                    update.Sequence));
                while (_finalSegments.Count > _capacity)
                {
                    _finalSegments.Dequeue();
                }

                _currentPartial = null;
            }
            else if (!string.IsNullOrWhiteSpace(update.CurrentCaptionText))
            {
                _currentPartial = new CaptionSegment(
                    _currentPartial?.Id ?? ++_nextId,
                    update.CurrentCaptionText,
                    false,
                    DateTimeOffset.UtcNow,
                    update.Sequence);
            }
            else
            {
                _currentPartial = null;
            }

            return SnapshotUnsafe();
        }
    }

    public CaptionHistorySnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _finalSegments.Clear();
            _currentPartial = null;
        }
    }

    public CaptionHistorySnapshot ClearPartial()
    {
        lock (_gate)
        {
            _currentPartial = null;
            return SnapshotUnsafe();
        }
    }

    private CaptionHistorySnapshot SnapshotUnsafe() =>
        new(_finalSegments.ToArray(), _currentPartial);
}
