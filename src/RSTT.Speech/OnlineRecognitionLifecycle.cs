namespace RSTT.Speech;

/// <summary>Small facade boundary around sherpa's terminal stream operations.</summary>
internal interface IOnlineRecognizerFacade
{
    void InputFinished();

    bool IsReady();

    void Decode();

    string GetResultText();

    void Reset();
}

internal static class OnlineRecognitionLifecycle
{
    public static void EmitEndpointThenReset(
        IOnlineRecognizerFacade recognizer,
        Action<string> emit)
    {
        var text = recognizer.GetResultText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            emit(text);
        }

        recognizer.Reset();
    }

    public static void FinishInputAndEmit(
        IOnlineRecognizerFacade recognizer,
        Action<string> emit)
    {
        recognizer.InputFinished();
        while (recognizer.IsReady())
        {
            recognizer.Decode();
        }

        var text = recognizer.GetResultText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            emit(text);
        }
    }
}
