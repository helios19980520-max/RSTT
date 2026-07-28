using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.State;
using RSTT.Core.Transcription;

namespace RSTT.App.Services;

/// <summary>
/// Owns one recognition session. Capture/inference, hypothesis stabilization, UI delivery,
/// and SendInput are separate bounded stages so a slow target window cannot block audio.
/// </summary>
public sealed partial class RecognitionCoordinator : IAsyncDisposable
{
    private const int ResultQueueCapacity = 128;
    private const int InjectionQueueCapacity = 128;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UiInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RepeatedWarningInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecognitionStallThreshold = TimeSpan.FromSeconds(5);
    private const double SpeechLevelThreshold = 0.012;

    private readonly IAudioCaptureService _audioCapture;
    private readonly ISpeechRecognitionEngine _speechEngine;
    private readonly ITextInjectionService _textInjection;
    private readonly ISettingsService _settings;
    private readonly IApplicationStateService _applicationState;
    private readonly TranscriptStabilizer _stabilizer;
    private readonly CaptionHistory _captionHistory;
    private readonly IPerformanceMonitor _performance;
    private readonly ILogger<RecognitionCoordinator> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _sessionStateGate = new();
    private readonly object _uiGate = new();
    private readonly object _terminalWriteGate = new();
    private readonly List<Task> _pendingTerminalWrites = [];
    private readonly TranscriptionSessionStateMachine _sessionState = new();
    private CancellationTokenSource? _audioCancellation;
    private CancellationTokenSource? _uiCancellation;
    private ChannelWriter<ResultEnvelope>? _resultWriter;
    private ChannelWriter<InjectionRequest>? _injectionWriter;
    private Task? _audioWorker;
    private Task? _resultWorker;
    private Task? _injectionWorker;
    private Task? _uiWorker;
    private TranscriptUpdate? _pendingUiUpdate;
    private int _pendingUiCoalesced;
    private long _generation;
    private long _lastResultQueueWarning;
    private long _lastInjectionWarning;
    private long _lastAudioTimestamp;
    private long _lastSpeechTimestamp;
    private long _lastRecognitionTimestamp;
    private long _lastHealthCheckTimestamp;
    private long _lastStallWarning;
    private int _recognizerRecoveryAttempted;
    private TextInjectionStatus _lastInjectionStatus = TextInjectionStatus.Success;
    private bool _isListening;

    public RecognitionCoordinator(
        IAudioCaptureService audioCapture,
        ISpeechRecognitionEngine speechEngine,
        ITextInjectionService textInjection,
        ISettingsService settings,
        IApplicationStateService applicationState,
        TranscriptStabilizer stabilizer,
        CaptionHistory captionHistory,
        IPerformanceMonitor performance,
        ILogger<RecognitionCoordinator> logger)
    {
        _audioCapture = audioCapture;
        _speechEngine = speechEngine;
        _textInjection = textInjection;
        _settings = settings;
        _applicationState = applicationState;
        _stabilizer = stabilizer;
        _captionHistory = captionHistory;
        _performance = performance;
        _logger = logger;
        _speechEngine.RecognitionResultAvailable += OnRecognitionResultAvailable;
        _audioCapture.AudioLevelChanged += OnAudioLevelChanged;
    }

    public bool IsListening => Volatile.Read(ref _isListening);

    public TranscriptionSessionState SessionState
    {
        get
        {
            lock (_sessionStateGate)
            {
                return _sessionState.Current;
            }
        }
    }

    public event EventHandler<TranscriptUpdate>? TranscriptUpdated;

    public event EventHandler<ApplicationStateSnapshot>? StateChanged
    {
        add => _applicationState.Changed += value;
        remove => _applicationState.Changed -= value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        TransitionSessionTo(TranscriptionSessionState.LoadingModel);
        _applicationState.TransitionTo(ApplicationState.ModelLoading, "Loading local speech model…");
        try
        {
            await _speechEngine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            TransitionSessionTo(TranscriptionSessionState.Stopped);
            _applicationState.TransitionTo(ApplicationState.Ready, "Model ready");
        }
        catch (InvalidOperationException exception)
        {
            TransitionSessionTo(TranscriptionSessionState.Stopped);
            _applicationState.TransitionTo(
                ApplicationState.ModelMissing,
                "Install a speech model to begin",
                exception);
            LogModelNotReady(_logger, exception.Message);
        }
        catch (Exception exception)
        {
            TransitionSessionTo(TranscriptionSessionState.Faulted);
            _applicationState.TransitionTo(
                ApplicationState.Error,
                "Speech model could not be loaded.",
                exception);
            LogEngineInitializationFailed(_logger, exception);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsListening)
            {
                return;
            }

            if (SessionState == TranscriptionSessionState.Faulted)
            {
                TransitionSessionTo(TranscriptionSessionState.Starting);
            }
            else
            {
                TransitionSessionTo(TranscriptionSessionState.Starting);
            }

            if (!_speechEngine.IsReady)
            {
                TransitionSessionTo(TranscriptionSessionState.Faulted);
                _applicationState.TransitionTo(
                    ApplicationState.ModelMissing,
                    _speechEngine.ModelInformation.StatusMessage ??
                    "No local speech model is installed.");
                return;
            }

            _stabilizer.Reset();
            _captionHistory.Reset();
            var sessionStarted = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastAudioTimestamp, sessionStarted);
            Interlocked.Exchange(ref _lastSpeechTimestamp, 0);
            Interlocked.Exchange(ref _lastRecognitionTimestamp, sessionStarted);
            Interlocked.Exchange(ref _lastHealthCheckTimestamp, sessionStarted);
            Interlocked.Exchange(ref _lastStallWarning, 0);
            Interlocked.Exchange(ref _recognizerRecoveryAttempted, 0);
            await _speechEngine.ResetAsync(cancellationToken).ConfigureAwait(false);
            await _speechEngine.StartAsync(cancellationToken).ConfigureAwait(false);

            var generation = new SessionGenerationId(Interlocked.Increment(ref _generation));
            _audioCancellation = new CancellationTokenSource();
            CreateSessionWorkers(generation);

            TransitionSessionTo(TranscriptionSessionState.StartingAudio);
            await _audioCapture
                .StartAsync(_settings.Current.AudioDeviceId, cancellationToken)
                .ConfigureAwait(false);

            Volatile.Write(ref _isListening, true);
            _audioWorker = Task.Run(
                () => ProcessAudioAsync(generation, _audioCancellation.Token),
                CancellationToken.None);
            TransitionSessionTo(TranscriptionSessionState.Listening);
            _applicationState.TransitionTo(
                ApplicationState.Listening,
                "Listening to system audio");
        }
        catch (Exception exception)
        {
            TransitionSessionTo(TranscriptionSessionState.Faulted);
            _applicationState.TransitionTo(
                ApplicationState.Error,
                "Audio capture could not be started.",
                exception);
            LogPipelineStartFailed(_logger, exception);
            await StopUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task ReloadModelAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsListening || _audioWorker is not null)
            {
                await StopUnsafeAsync().ConfigureAwait(false);
            }

            if (SessionState == TranscriptionSessionState.Faulted)
            {
                TransitionSessionTo(TranscriptionSessionState.Stopped);
            }

            TransitionSessionTo(TranscriptionSessionState.LoadingModel);
            _applicationState.TransitionTo(
                ApplicationState.ModelLoading,
                "Loading local speech model…");
            await _speechEngine.ReloadAsync(cancellationToken).ConfigureAwait(false);
            TransitionSessionTo(TranscriptionSessionState.Stopped);
            _applicationState.TransitionTo(ApplicationState.Ready, "Ready");
        }
        catch (InvalidOperationException exception)
        {
            TransitionSessionTo(TranscriptionSessionState.Faulted);
            _applicationState.TransitionTo(
                ApplicationState.ModelMissing,
                "Install a speech model to begin",
                exception);
            LogModelNotReady(_logger, exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            TransitionSessionTo(TranscriptionSessionState.Faulted);
            _applicationState.TransitionTo(
                ApplicationState.Error,
                "Speech model could not be loaded.",
                exception);
            LogEngineInitializationFailed(_logger, exception);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task UnloadModelAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsListening || _audioWorker is not null)
            {
                await StopUnsafeAsync().ConfigureAwait(false);
            }

            await _speechEngine.UnloadAsync(cancellationToken).ConfigureAwait(false);
            if (SessionState == TranscriptionSessionState.Faulted)
            {
                TransitionSessionTo(TranscriptionSessionState.Stopped);
            }

            _applicationState.TransitionTo(
                ApplicationState.ModelMissing,
                "Install a speech model to begin");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsListening &&
                _audioWorker is null &&
                _resultWorker is null &&
                _injectionWorker is null)
            {
                return;
            }

            if (SessionState != TranscriptionSessionState.Faulted)
            {
                TransitionSessionTo(TranscriptionSessionState.Completing);
            }

            _applicationState.TransitionTo(ApplicationState.Stopping, "Finishing final words…");
            await StopUnsafeAsync().ConfigureAwait(false);
            _applicationState.TransitionTo(
                ApplicationState.Ready,
                _speechEngine.IsReady
                    ? "Model ready"
                    : "No local speech model is installed.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void CreateSessionWorkers(SessionGenerationId generation)
    {
        var results = Channel.CreateBounded<ResultEnvelope>(
            new BoundedChannelOptions(ResultQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        var injections = Channel.CreateBounded<InjectionRequest>(
            new BoundedChannelOptions(InjectionQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });

        _resultWriter = results.Writer;
        _injectionWriter = injections.Writer;
        lock (_terminalWriteGate)
        {
            _pendingTerminalWrites.Clear();
        }
        _pendingUiUpdate = null;
        _pendingUiCoalesced = 0;
        _lastInjectionStatus = TextInjectionStatus.Success;
        _uiCancellation = new CancellationTokenSource();
        _resultWorker = Task.Run(
            () => ProcessResultsAsync(results.Reader, injections.Writer, generation),
            CancellationToken.None);
        _injectionWorker = Task.Run(
            () => ProcessInjectionsAsync(
                injections.Reader,
                generation,
                _audioCancellation?.Token ?? CancellationToken.None),
            CancellationToken.None);
        _uiWorker = Task.Run(
            () => PublishUiUpdatesAsync(_uiCancellation.Token),
            CancellationToken.None);
    }

    private async Task ProcessAudioAsync(
        SessionGenerationId generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in _audioCapture
                               .ReadChunksAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _lastAudioTimestamp, Stopwatch.GetTimestamp());
                try
                {
                    await _speechEngine
                        .ProcessAudioAsync(
                            chunk with { SessionGenerationId = generation },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested &&
                    Interlocked.CompareExchange(
                        ref _recognizerRecoveryAttempted,
                        1,
                        0) == 0)
                {
                    LogRecognizerRecoveryAttempt(_logger, exception);
                    try
                    {
                        await _speechEngine
                            .ResetAsync(cancellationToken)
                            .ConfigureAwait(false);
                        var writer = Volatile.Read(ref _resultWriter);
                        writer?.TryWrite(ResultEnvelope.CreateReset(
                            generation));
                        _applicationState.TransitionTo(
                            ApplicationState.Listening,
                            "Recovered from a recognition engine error");
                        LogRecognizerRecoverySucceeded(_logger);
                    }
                    catch (Exception recoveryException)
                    {
                        throw new AggregateException(
                            "Recognition failed and the single controlled recovery attempt also failed.",
                            exception,
                            recoveryException);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _isListening, false);
            TransitionSessionTo(TranscriptionSessionState.Faulted);
            _applicationState.TransitionTo(
                ApplicationState.Error,
                "The audio pipeline stopped unexpectedly.",
                exception);
            LogWorkerFailed(_logger, exception);
        }
    }

    private async Task ProcessResultsAsync(
        ChannelReader<ResultEnvelope> reader,
        ChannelWriter<InjectionRequest> injectionWriter,
        SessionGenerationId generation)
    {
        try
        {
            await foreach (var envelope in reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (envelope.Generation != generation ||
                    generation.Value != Volatile.Read(ref _generation))
                {
                    continue;
                }

                if (envelope.ResetCurrentSegment)
                {
                    var stableText = _stabilizer.ResetCurrentSegment();
                    _captionHistory.ClearPartial();
                    QueueUiUpdate(new TranscriptUpdate(
                        new TranscriptSnapshot(stableText, string.Empty, string.Empty),
                        Array.Empty<TranscriptCommit>(),
                        false,
                        SessionGenerationId: generation));
                    continue;
                }

                if (envelope.Result is not { } result)
                {
                    continue;
                }

                var update = _stabilizer.Process(result);
                _captionHistory.Apply(update);
                QueueUiUpdate(update);

                if (_settings.Current.TextInjectionEnabled)
                {
                    foreach (var commit in update.Commits)
                    {
                        var request = new InjectionRequest(
                            commit.SessionGenerationId,
                            commit.CommitId,
                            commit.SourceSequenceId,
                            commit.Text,
                            commit.Timestamp);
                        LogCommitBoundary(
                            _logger,
                            commit.SessionGenerationId.Value,
                            commit.CommitId,
                            commit.SourceSequenceId,
                            commit.Text.Length,
                            ComputeTextHash(commit.Text));
                        await injectionWriter
                            .WriteAsync(request, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            LogResultProcessingFailed(_logger, exception);
        }
    }

    private async Task ProcessInjectionsAsync(
        ChannelReader<InjectionRequest> reader,
        SessionGenerationId generation,
        CancellationToken cancellationToken)
    {
        await foreach (var request in reader
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (request.SessionGenerationId != generation ||
                generation.Value != Volatile.Read(ref _generation))
            {
                continue;
            }

            try
            {
                var result = await _textInjection
                    .InjectAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Succeeded)
                {
                    _performance.RecordInjection();
                    _lastInjectionStatus = TextInjectionStatus.Success;
                    continue;
                }

                LogInjectionResultRateLimited(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogResultProcessingFailed(_logger, exception);
            }
        }
    }

    private async Task PublishUiUpdatesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(UiInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_uiGate)
                {
                    PublishPendingUiUpdateUnsafe();
                }

                CheckSessionHealth();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void QueueUiUpdate(TranscriptUpdate update)
    {
        lock (_uiGate)
        {
            if (update.IsFinal)
            {
                _pendingUiUpdate = null;
                var coalesced = _pendingUiCoalesced;
                _pendingUiCoalesced = 0;
                PublishUiUpdateUnsafe(update, coalesced);
                return;
            }

            if (_pendingUiUpdate is not null)
            {
                _pendingUiCoalesced++;
            }

            _pendingUiUpdate = update;
        }
    }

    private void PublishPendingUiUpdateUnsafe()
    {
        if (_pendingUiUpdate is not { } update)
        {
            return;
        }

        _pendingUiUpdate = null;
        var coalesced = _pendingUiCoalesced;
        _pendingUiCoalesced = 0;
        PublishUiUpdateUnsafe(update, coalesced);
    }

    private void PublishUiUpdateUnsafe(
        TranscriptUpdate update,
        int coalescedEvents = 0)
    {
        _performance.RecordUiUpdate(coalescedEvents);
        TranscriptUpdated?.Invoke(this, update);
    }

    private async Task StopUnsafeAsync()
    {
        var incompleteStage = "stop WASAPI capture and complete raw audio";
        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            // IsListening intentionally remains true throughout completion so UI
            // reflects an active generation and terminal commits stay admissible.
            await _audioCapture.StopAsync(timeout.Token).ConfigureAwait(false);

            incompleteStage = "drain normalized audio into the recognizer";
            await AwaitWorkerAsync(_audioWorker, timeout.Token).ConfigureAwait(false);
            _audioWorker = null;

            incompleteStage = "finish the sherpa stream and emit its terminal hypothesis";
            await _speechEngine.StopAsync(timeout.Token).ConfigureAwait(false);
            if (SessionState == TranscriptionSessionState.Completing)
            {
                TransitionSessionTo(TranscriptionSessionState.Stopping);
            }

            incompleteStage = "drain recognition results and transcript commits";
            await AwaitPendingTerminalWritesAsync(timeout.Token).ConfigureAwait(false);
            var resultWriter = Interlocked.Exchange(ref _resultWriter, null);
            resultWriter?.TryComplete();
            await AwaitWorkerAsync(_resultWorker, timeout.Token).ConfigureAwait(false);
            _resultWorker = null;

            incompleteStage = "drain ordered text injection requests";
            var injectionWriter = Interlocked.Exchange(ref _injectionWriter, null);
            injectionWriter?.TryComplete();
            await AwaitWorkerAsync(_injectionWorker, timeout.Token).ConfigureAwait(false);
            _injectionWorker = null;

            incompleteStage = "publish the final UI snapshot";
            lock (_uiGate)
            {
                PublishPendingUiUpdateUnsafe();
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            LogStopTimedOut(_logger, incompleteStage, StopTimeout.TotalSeconds);
            _audioCancellation?.Cancel();
            Interlocked.Exchange(ref _resultWriter, null)?.TryComplete();
            Interlocked.Exchange(ref _injectionWriter, null)?.TryComplete();
            await AwaitPendingTerminalWritesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // Only now does this generation become stopped. Cancellation is a
            // hard-stop fallback, never the normal queue-draining mechanism.
            Volatile.Write(ref _isListening, false);
            _audioCancellation?.Cancel();
            _uiCancellation?.Cancel();
            await AwaitWorkersBestEffortAsync(
                    _audioWorker,
                    _resultWorker,
                    _injectionWorker,
                    _uiWorker)
                .ConfigureAwait(false);
            _audioWorker = null;
            _resultWorker = null;
            _injectionWorker = null;
            _uiWorker = null;
            _uiCancellation?.Dispose();
            _uiCancellation = null;
            _audioCancellation?.Dispose();
            _audioCancellation = null;
        }

        var snapshot = _performance.GetSnapshot();
        LogSessionPerformance(
            _logger,
            snapshot.Provider,
            snapshot.ModelId,
            snapshot.ProcessCpuPercent,
            snapshot.RealtimeFactor,
            snapshot.DecodeP50Ms,
            snapshot.DecodeP95Ms,
            snapshot.DecodeMaxMs,
            snapshot.AudioQueueDurationMs,
            snapshot.OldestAudioAgeMs,
            snapshot.DroppedAudioMilliseconds,
            snapshot.CoalescedUiEvents);

        if (SessionState is TranscriptionSessionState.Completing or
            TranscriptionSessionState.Stopping or
            TranscriptionSessionState.Faulted)
        {
            TransitionSessionTo(TranscriptionSessionState.Stopped);
        }
    }

    private void OnRecognitionResultAvailable(object? sender, RecognitionHypothesis result)
    {
        Interlocked.Exchange(ref _lastRecognitionTimestamp, Stopwatch.GetTimestamp());
        var generation = new SessionGenerationId(Volatile.Read(ref _generation));
        if (result.SessionGenerationId != generation)
        {
            LogStaleHypothesisDropped(
                _logger,
                result.SessionGenerationId.Value,
                generation.Value,
                result.SequenceId);
            return;
        }

        var writer = Volatile.Read(ref _resultWriter);
        var envelope = new ResultEnvelope(generation, result);
        if (writer is null || writer.TryWrite(envelope))
        {
            return;
        }

        // Partial UI hypotheses may be dropped under sustained overload. A
        // terminal hypothesis instead waits asynchronously for bounded channel
        // capacity and Stop awaits that write before completing the channel.
        if (result.IsFinal)
        {
            var pendingWrite = writer.WriteAsync(envelope).AsTask();
            lock (_terminalWriteGate)
            {
                _pendingTerminalWrites.RemoveAll(static task => task.IsCompleted);
                _pendingTerminalWrites.Add(pendingWrite);
            }

            return;
        }

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastResultQueueWarning);
        if (previous == 0 ||
            Stopwatch.GetElapsedTime(previous, now) >= RepeatedWarningInterval)
        {
            if (Interlocked.CompareExchange(
                    ref _lastResultQueueWarning,
                    now,
                    previous) == previous)
            {
                LogResultQueueFull(_logger);
            }
        }
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs eventArgs)
    {
        if (IsListening && eventArgs.Peak >= SpeechLevelThreshold)
        {
            Interlocked.Exchange(ref _lastSpeechTimestamp, Stopwatch.GetTimestamp());
        }
    }

    private void CheckSessionHealth()
    {
        if (!IsListening)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var lastCheck = Interlocked.Read(ref _lastHealthCheckTimestamp);
        if (Stopwatch.GetElapsedTime(lastCheck, now) < HealthCheckInterval)
        {
            return;
        }

        Interlocked.Exchange(ref _lastHealthCheckTimestamp, now);
        var lastAudio = Interlocked.Read(ref _lastAudioTimestamp);
        var lastSpeech = Interlocked.Read(ref _lastSpeechTimestamp);
        var lastResult = Interlocked.Read(ref _lastRecognitionTimestamp);
        if (lastSpeech == 0 ||
            Stopwatch.GetElapsedTime(lastAudio, now) > HealthCheckInterval ||
            Stopwatch.GetElapsedTime(lastSpeech, now) > HealthCheckInterval ||
            Stopwatch.GetElapsedTime(lastResult, now) < RecognitionStallThreshold)
        {
            return;
        }

        var previous = Interlocked.Read(ref _lastStallWarning);
        if (previous != 0 &&
            Stopwatch.GetElapsedTime(previous, now) < RepeatedWarningInterval)
        {
            return;
        }

        Interlocked.Exchange(ref _lastStallWarning, now);
        var snapshot = _performance.GetSnapshot();
        LogRecognitionStall(
            _logger,
            Stopwatch.GetElapsedTime(lastResult, now).TotalSeconds,
            snapshot.AudioQueueDurationMs,
            snapshot.OldestAudioAgeMs,
            snapshot.DecodeP95Ms,
            snapshot.ProcessCpuPercent,
            snapshot.Provider,
            snapshot.ModelId);
    }

    private void LogInjectionResultRateLimited(TextInjectionResult result)
    {
        var now = Stopwatch.GetTimestamp();
        var statusChanged = result.Status != _lastInjectionStatus;
        var previous = Interlocked.Read(ref _lastInjectionWarning);
        if (!statusChanged &&
            previous != 0 &&
            Stopwatch.GetElapsedTime(previous, now) < RepeatedWarningInterval)
        {
            return;
        }

        _lastInjectionStatus = result.Status;
        Interlocked.Exchange(ref _lastInjectionWarning, now);
        if (result.Status == TextInjectionStatus.SelfFocused)
        {
            LogSelfFocusedInjectionSuppressed(_logger);
        }
        else
        {
            LogInjectionNotCompleted(
                _logger,
                result.Status,
                result.DiagnosticMessage ?? "No diagnostic was provided.");
        }
    }

    private void TransitionSessionTo(TranscriptionSessionState next)
    {
        lock (_sessionStateGate)
        {
            if (_sessionState.Current == next)
            {
                return;
            }

            _sessionState.TransitionTo(next);
        }
    }

    private static async Task AwaitWorkerAsync(
        Task? worker,
        CancellationToken cancellationToken = default)
    {
        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task AwaitPendingTerminalWritesAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (_terminalWriteGate)
        {
            pending = _pendingTerminalWrites.ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            lock (_terminalWriteGate)
            {
                _pendingTerminalWrites.RemoveAll(static task => task.IsCompleted);
            }
        }
    }

    private static async Task AwaitWorkersBestEffortAsync(params Task?[] workers)
    {
        var remaining = workers.Where(static worker => worker is not null).Cast<Task>().ToArray();
        if (remaining.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(remaining).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The five-second stop timeout already records the precise incomplete
            // stage. This short wait only lets cooperative cancellation settle.
        }
    }

    private static string ComputeTextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    public async ValueTask DisposeAsync()
    {
        _speechEngine.RecognitionResultAvailable -= OnRecognitionResultAvailable;
        _audioCapture.AudioLevelChanged -= OnAudioLevelChanged;
        await StopAsync().ConfigureAwait(false);
        _audioCancellation?.Dispose();
        _uiCancellation?.Dispose();
        _lifecycleLock.Dispose();
    }

    private sealed record ResultEnvelope(
        SessionGenerationId Generation,
        RecognitionHypothesis? Result,
        bool ResetCurrentSegment = false)
    {
        public static ResultEnvelope CreateReset(SessionGenerationId generation) =>
            new(generation, null, true);
    }

    [LoggerMessage(LogLevel.Information, "Local speech model is not ready: {Message}")]
    private static partial void LogModelNotReady(ILogger logger, string message);

    [LoggerMessage(LogLevel.Error, "Failed to initialize local speech engine.")]
    private static partial void LogEngineInitializationFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(LogLevel.Error, "Failed to start the audio recognition pipeline.")]
    private static partial void LogPipelineStartFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(LogLevel.Error, "Audio recognition worker terminated unexpectedly.")]
    private static partial void LogWorkerFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        LogLevel.Warning,
        "Text injection was not completed ({Status}): {Message}")]
    private static partial void LogInjectionNotCompleted(
        ILogger logger,
        TextInjectionStatus status,
        string message);

    [LoggerMessage(
        LogLevel.Debug,
        "Text injection was suppressed while an RSTT window had focus.")]
    private static partial void LogSelfFocusedInjectionSuppressed(ILogger logger);

    [LoggerMessage(
        LogLevel.Warning,
        "The recognition-result queue is full; an intermediate hypothesis was dropped.")]
    private static partial void LogResultQueueFull(ILogger logger);

    [LoggerMessage(
        LogLevel.Error,
        "Recognition result processing failed; captions will continue on later results.")]
    private static partial void LogResultProcessingFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        LogLevel.Warning,
        "Recognition engine call failed; attempting the one controlled stream reset allowed for this session.")]
    private static partial void LogRecognizerRecoveryAttempt(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        LogLevel.Information,
        "Recognition engine stream recovered successfully.")]
    private static partial void LogRecognizerRecoverySucceeded(ILogger logger);

    [LoggerMessage(
        LogLevel.Warning,
        "Recognition stall detected: no result for {SecondsWithoutResult:F1}s while speech/audio are active; " +
        "queue={AudioQueue:F1} ms, oldest={OldestAudio:F1} ms, decode P95={DecodeP95:F1} ms, " +
        "CPU={CpuPercent:F1}%, provider={Provider}, model={ModelId}.")]
    private static partial void LogRecognitionStall(
        ILogger logger,
        double secondsWithoutResult,
        double audioQueue,
        double oldestAudio,
        double decodeP95,
        double cpuPercent,
        string provider,
        string modelId);

    [LoggerMessage(
        LogLevel.Information,
        "Session performance: provider={Provider}, model={ModelId}, CPU={CpuPercent:F2}%, " +
        "RTF={RealtimeFactor:F3}, decode P50/P95/max={DecodeP50:F1}/{DecodeP95:F1}/{DecodeMax:F1} ms, " +
        "audio queue={AudioQueue:F1} ms, oldest={OldestAudio:F1} ms, dropped={DroppedAudio} ms, " +
        "UI coalesced={CoalescedUiEvents}.")]
    private static partial void LogSessionPerformance(
        ILogger logger,
        string provider,
        string modelId,
        double cpuPercent,
        double realtimeFactor,
        double decodeP50,
        double decodeP95,
        double decodeMax,
        double audioQueue,
        double oldestAudio,
        long droppedAudio,
        long coalescedUiEvents);

    [LoggerMessage(
        LogLevel.Debug,
        "Transcript commit generation={Generation} commit={CommitId} sourceSequence={SourceSequenceId} utf16={Utf16Length} hash={TextHash}.")]
    private static partial void LogCommitBoundary(
        ILogger logger,
        long generation,
        long commitId,
        long sourceSequenceId,
        int utf16Length,
        string textHash);

    [LoggerMessage(
        LogLevel.Warning,
        "Recognition stop exceeded {TimeoutSeconds:F1}s while attempting to {IncompleteStage}; remaining work was hard-cancelled.")]
    private static partial void LogStopTimedOut(
        ILogger logger,
        string incompleteStage,
        double timeoutSeconds);

    [LoggerMessage(
        LogLevel.Debug,
        "Dropped stale recognition hypothesis generation={ResultGeneration}; active={ActiveGeneration}; sequence={SequenceId}.")]
    private static partial void LogStaleHypothesisDropped(
        ILogger logger,
        long resultGeneration,
        long activeGeneration,
        long sequenceId);
}
