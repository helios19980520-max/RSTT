using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.Transcription;

namespace RSTT.Speech;

/// <summary>
/// Selects commit semantics from the model descriptor. Online models use stable
/// whole-word streaming commits; VAD/offline models commit final segments only.
/// </summary>
public sealed class ModelAwareTranscriptCommitPolicy : ITranscriptCommitPolicy
{
    private readonly IModelCatalog _catalog;
    private readonly StablePrefixCommitPolicy _streaming = new();
    private readonly FinalOnlyCommitPolicy _segmented = new();
    private ITranscriptCommitPolicy _active;

    public ModelAwareTranscriptCommitPolicy(IModelCatalog catalog)
    {
        _catalog = catalog;
        _active = _streaming;
    }

    public TranscriptUpdate Process(RecognitionHypothesis hypothesis, string normalizedText)
    {
        var descriptor = _catalog.GetModels().FirstOrDefault(model =>
            string.Equals(model.Id, hypothesis.EngineId, StringComparison.OrdinalIgnoreCase));
        _active = descriptor?.StreamingMode is SpeechStreamingMode.SegmentedVad or
            SpeechStreamingMode.Offline
            ? _segmented
            : _streaming;
        return _active.Process(hypothesis, normalizedText);
    }

    public void Reset()
    {
        _streaming.Reset();
        _segmented.Reset();
        _active = _streaming;
    }

    public string ResetCurrentSegment() => _active.ResetCurrentSegment();
}
