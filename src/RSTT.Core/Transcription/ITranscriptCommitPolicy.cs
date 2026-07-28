namespace RSTT.Core.Transcription;

public interface ITranscriptCommitPolicy
{
    string GetStablePrefix(string previousHypothesis, string currentHypothesis, bool isFinal);
}

public sealed class FinalOnlyCommitPolicy : ITranscriptCommitPolicy
{
    public string GetStablePrefix(string previousHypothesis, string currentHypothesis, bool isFinal) =>
        isFinal ? currentHypothesis : string.Empty;
}

/// <summary>Commits completed words that are unchanged across consecutive partial hypotheses.</summary>
public sealed class StablePrefixCommitPolicy : ITranscriptCommitPolicy
{
    public string GetStablePrefix(string previousHypothesis, string currentHypothesis, bool isFinal)
    {
        if (isFinal)
        {
            return currentHypothesis;
        }

        if (previousHypothesis.Length == 0 || currentHypothesis.Length == 0)
        {
            return string.Empty;
        }

        var equalLength = 0;
        var compareLength = Math.Min(previousHypothesis.Length, currentHypothesis.Length);
        while (equalLength < compareLength &&
               char.ToUpperInvariant(previousHypothesis[equalLength]) ==
               char.ToUpperInvariant(currentHypothesis[equalLength]))
        {
            equalLength++;
        }

        if (equalLength == previousHypothesis.Length && equalLength == currentHypothesis.Length)
        {
            return previousHypothesis;
        }

        if (equalLength == previousHypothesis.Length)
        {
            return previousHypothesis;
        }

        if (equalLength == currentHypothesis.Length)
        {
            return string.Empty;
        }

        var boundary = -1;
        for (var index = 0; index < equalLength; index++)
        {
            if (char.IsWhiteSpace(previousHypothesis[index]))
            {
                boundary = index;
            }
            else if (previousHypothesis[index] is ',' or '.' or ';' or ':' or '!' or '?')
            {
                boundary = index + 1;
            }
        }

        return boundary <= 0
            ? string.Empty
            : previousHypothesis[..boundary].Trim();
    }
}
