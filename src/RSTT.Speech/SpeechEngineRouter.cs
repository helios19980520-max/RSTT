using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Speech;

/// <summary>Owns exactly one engine and replaces it when the active model mode changes.</summary>
public sealed class SpeechEngineRouter : ISpeechRecognitionEngine
{
    private readonly IModelManager _models;
    private readonly ISpeechEngineFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISpeechRecognitionEngine? _engine;
    private bool _disposed;

    public SpeechEngineRouter(IModelManager models, ISpeechEngineFactory factory)
    {
        _models = models;
        _factory = factory;
    }

    public bool IsReady => _engine?.IsReady == true;

    public ModelInformation ModelInformation =>
        _engine?.ModelInformation ?? _models.GetSelectedModel();

    public event EventHandler<RecognitionHypothesis>? RecognitionResultAvailable;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _engine ??= CreateSelectedEngine();
            await _engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplaceEngineUnsafeAsync().ConfigureAwait(false);
            await _engine!.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default) =>
        _engine?.UnloadAsync(cancellationToken) ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        RequiredEngine().StartAsync(cancellationToken);

    public Task ProcessAudioAsync(
        AudioChunk chunk,
        CancellationToken cancellationToken = default) =>
        RequiredEngine().ProcessAudioAsync(chunk, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _engine?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        RequiredEngine().ResetAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_engine is not null)
            {
                _engine.RecognitionResultAvailable -= ForwardResult;
                await _engine.DisposeAsync().ConfigureAwait(false);
                _engine = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private ISpeechRecognitionEngine CreateSelectedEngine()
    {
        var descriptor = _models.GetSelectedModel().Descriptor ??
            throw new InvalidOperationException("The selected model has no engine descriptor.");
        var engine = _factory.Create(descriptor);
        engine.RecognitionResultAvailable += ForwardResult;
        return engine;
    }

    private async Task ReplaceEngineUnsafeAsync()
    {
        if (_engine is not null)
        {
            _engine.RecognitionResultAvailable -= ForwardResult;
            await _engine.DisposeAsync().ConfigureAwait(false);
        }

        _engine = CreateSelectedEngine();
    }

    private ISpeechRecognitionEngine RequiredEngine() =>
        _engine ?? throw new InvalidOperationException(
            "The speech engine router has not been initialized.");

    private void ForwardResult(object? sender, RecognitionHypothesis hypothesis) =>
        RecognitionResultAvailable?.Invoke(this, hypothesis);
}
