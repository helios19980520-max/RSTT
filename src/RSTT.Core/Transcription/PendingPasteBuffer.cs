using RSTT.Core.Models;

namespace RSTT.Core.Transcription;

/// <summary>Owns unconsumed commits independently of the bounded caption history.</summary>
public sealed class PendingPasteBuffer
{
    private readonly object _gate = new();
    private readonly List<(long Id, string Text)> _pending = [];
    private long _nextId;
    private string _partial = string.Empty;
    private readonly TextFormattingPolicy _formatting = new();

    public void Apply(TranscriptUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            foreach (var commit in update.Commits)
            {
                var text = commit.Text;
                if (_pending.Count > 0)
                {
                    var previous = _formatting.Normalize(_pending[^1].Text);
                    text = _formatting.Append(previous, text)[previous.Length..];
                }
                _pending.Add((++_nextId, text));
            }

            _partial = update.PendingText;
        }
    }

    public PendingPasteSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PendingPasteSnapshot(
                _nextId,
                string.Concat(_pending.Select(item => item.Text)).Trim(),
                _partial);
        }
    }

    public void Consume(long throughId)
    {
        lock (_gate)
        {
            _pending.RemoveAll(item => item.Id <= throughId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _partial = string.Empty;
        }
    }

    public void ClearPartial()
    {
        lock (_gate) { _partial = string.Empty; }
    }
}

public sealed record PendingPasteSnapshot(long ThroughId, string Text, string Partial);
