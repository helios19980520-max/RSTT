using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using RSTT.Core.Models;
using RSTT.Speech;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        var backend = ParseBackend(RequiredArgument(args, "--backend"));
        var engine = RequiredArgument(args, "--engine").Trim().ToLowerInvariant();
        var modelDirectory = Path.GetFullPath(RequiredArgument(args, "--model-dir"));
        var audioPath = Path.GetFullPath(RequiredArgument(args, "--audio"));
        var workerRoot = OptionalArgument(args, "--worker-root");
        if (workerRoot is not null)
        {
            workerRoot = Path.GetFullPath(workerRoot);
        }
        if (!Directory.Exists(modelDirectory))
        {
            throw new DirectoryNotFoundException(modelDirectory);
        }

        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Validation audio is missing.", audioPath);
        }

        var samples = ReadMono16Khz(audioPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var report = engine switch
        {
            "sherpa" => await RunSherpaAsync(
                backend,
                modelDirectory,
                samples,
                workerRoot,
                timeout.Token).ConfigureAwait(false),
            "whisper" => await RunWhisperAsync(
                backend,
                modelDirectory,
                samples,
                workerRoot,
                timeout.Token).ConfigureAwait(false),
            _ => throw new ArgumentException("--engine must be sherpa or whisper."),
        };
        Console.WriteLine(JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions { WriteIndented = true }));
        return string.IsNullOrWhiteSpace(report.Transcription) ? 3 : 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 1;
    }
}

static async Task<GpuValidationReport> RunSherpaAsync(
    ComputeBackend backend,
    string modelDirectory,
    float[] samples,
    string? workerRoot,
    CancellationToken cancellationToken)
{
    var manifest = ReadManifest(modelDirectory);
    var files = manifest.Files.ToDictionary(
        pair => pair.Key,
        pair => Path.GetFullPath(Path.Combine(modelDirectory, pair.Value)),
        StringComparer.OrdinalIgnoreCase);
    var descriptor = new ModelDescriptor
    {
        Id = manifest.Id,
        DisplayName = manifest.DisplayName,
        Engine = manifest.Engine,
        StreamingMode = manifest.Engine.StartsWith("online-", StringComparison.OrdinalIgnoreCase)
            ? SpeechStreamingMode.NativeStreaming
            : SpeechStreamingMode.SegmentedVad,
        CpuSupported = true,
        CudaSupported = true,
        FeatureDimension = manifest.FeatureDimension,
        Languages = ["en"],
    };
    var information = new ModelInformation(
        manifest.Id,
        manifest.DisplayName,
        modelDirectory,
        true,
        Descriptor: descriptor);
    var installation = new ModelInstallation(
        information,
        manifest.Engine,
        files,
        manifest.NumThreads,
        backend == ComputeBackend.Cuda ? "cuda" : "cpu",
        manifest.FeatureDimension,
        Descriptor: descriptor);

    var total = Stopwatch.StartNew();
    await using var worker = await SherpaWorkerClient
        .StartAsync(backend, cancellationToken, workerRoot)
        .ConfigureAwait(false);
    var loadStopwatch = Stopwatch.StartNew();
    _ = await worker.LoadAsync(installation, "en", cancellationToken).ConfigureAwait(false);
    loadStopwatch.Stop();
    var warmup = await worker.WarmupAsync(cancellationToken).ConfigureAwait(false);
    await worker.StartSessionAsync(new SessionGenerationId(1), 0, cancellationToken)
        .ConfigureAwait(false);
    var hypotheses = new List<RSTT.Core.Workers.WorkerHypothesisResponse>();
    var decodeMilliseconds = 0d;
    var decodedAudioMilliseconds = 0d;
    var segmentCount = 0;
    var peakWorkingSet = 0L;
    const int chunkSamples = 8_960;
    var sequence = 0L;
    for (var offset = 0; offset < samples.Length; offset += chunkSamples)
    {
        var length = Math.Min(chunkSamples, samples.Length - offset);
        var chunk = new float[length];
        Array.Copy(samples, offset, chunk, 0, length);
        var result = await worker.ProcessAudioAsync(
            new AudioChunk(
                ++sequence,
                DateTimeOffset.UtcNow,
                chunk,
                new SessionGenerationId(1)),
            cancellationToken).ConfigureAwait(false);
        hypotheses.AddRange(result.Hypotheses);
        if (result.Performance is { } performance)
        {
            decodeMilliseconds += performance.DecodeMilliseconds;
            decodedAudioMilliseconds += performance.DecodedAudioMilliseconds;
            segmentCount += performance.SegmentCount;
            peakWorkingSet = Math.Max(peakWorkingSet, performance.WorkingSetBytes);
        }
    }

    var final = await worker.FinishAsync(cancellationToken).ConfigureAwait(false);
    hypotheses.AddRange(final.Hypotheses);
    if (final.Performance is { } finalPerformance)
    {
        decodeMilliseconds += finalPerformance.DecodeMilliseconds;
        decodedAudioMilliseconds += finalPerformance.DecodedAudioMilliseconds;
        segmentCount += finalPerformance.SegmentCount;
        peakWorkingSet = Math.Max(peakWorkingSet, finalPerformance.WorkingSetBytes);
    }

    total.Stop();
    var transcription = string.Empty;
    for (var index = hypotheses.Count - 1; index >= 0; index--)
    {
        if (hypotheses[index].IsFinal &&
            !string.IsNullOrWhiteSpace(hypotheses[index].Text))
        {
            transcription = hypotheses[index].Text;
            break;
        }
    }

    if (transcription.Length == 0 && hypotheses.Count > 0)
    {
        transcription = hypotheses[^1].Text;
    }

    return new GpuValidationReport(
        "sherpa-onnx",
        worker.Handshake.Backend,
        worker.Handshake.RuntimeVersion,
        manifest.Id,
        loadStopwatch.Elapsed.TotalMilliseconds,
        warmup.Performance?.DecodeMilliseconds ?? 0,
        decodeMilliseconds,
        samples.Length * 1000d / AudioChunk.SampleRate,
        decodeMilliseconds / (samples.Length * 1000d / AudioChunk.SampleRate),
        peakWorkingSet,
        total.Elapsed.TotalMilliseconds,
        transcription,
        decodedAudioMilliseconds,
        segmentCount);
}

static async Task<GpuValidationReport> RunWhisperAsync(
    ComputeBackend backend,
    string modelDirectory,
    float[] samples,
    string? workerRoot,
    CancellationToken cancellationToken)
{
    var manifest = ReadManifest(modelDirectory);
    var modelPath = Path.Combine(modelDirectory, manifest.Files["model"]);
    var total = Stopwatch.StartNew();
    await using var worker = await WhisperWorkerClient
        .StartAsync(backend, cancellationToken, workerRoot)
        .ConfigureAwait(false);
    var loadStopwatch = Stopwatch.StartNew();
    await worker.LoadAsync(
            modelPath,
            Path.Combine(modelDirectory, manifest.Files["vad"]),
            "en",
            manifest.NumThreads,
            new RSTT.Core.Workers.WorkerVadPolicy(),
            cancellationToken)
        .ConfigureAwait(false);
    loadStopwatch.Stop();
    var warmupStopwatch = Stopwatch.StartNew();
    await worker.WarmupAsync(cancellationToken).ConfigureAwait(false);
    warmupStopwatch.Stop();
    var result = await worker.DecodeAsync(1, 1, samples, cancellationToken)
        .ConfigureAwait(false);
    total.Stop();
    return new GpuValidationReport(
        "whisper.cpp",
        worker.Handshake.Backend,
        worker.Handshake.RuntimeVersion,
        manifest.Id,
        loadStopwatch.Elapsed.TotalMilliseconds,
        warmupStopwatch.Elapsed.TotalMilliseconds,
        result.Performance.DecodeMilliseconds,
        result.Performance.AudioMilliseconds,
        result.Performance.DecodeMilliseconds / result.Performance.AudioMilliseconds,
        result.Performance.WorkingSetBytes,
        total.Elapsed.TotalMilliseconds,
        result.Hypothesis.Text,
        result.Performance.DecodedAudioMilliseconds,
        result.Performance.SegmentCount);
}

static ModelManifest ReadManifest(string directory)
{
    var path = Path.Combine(directory, "model.json");
    return JsonSerializer.Deserialize<ModelManifest>(
               File.ReadAllText(path),
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? throw new InvalidDataException($"Invalid model manifest: {path}");
}

static float[] ReadMono16Khz(string path)
{
    using var reader = new WaveFileReader(path);
    if (reader.WaveFormat.SampleRate != AudioChunk.SampleRate ||
        reader.WaveFormat.Channels != 1)
    {
        throw new InvalidDataException(
            $"Validation WAV must be 16 kHz mono; detected {reader.WaveFormat}.");
    }

    var provider = reader.ToSampleProvider();
    var result = new List<float>((int)(reader.Length / Math.Max(1, reader.BlockAlign)));
    var buffer = new float[AudioChunk.SampleRate];
    int read;
    while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
    {
        result.AddRange(buffer.AsSpan(0, read).ToArray());
    }

    return result.ToArray();
}

static ComputeBackend ParseBackend(string value) =>
    value.Trim().ToLowerInvariant() switch
    {
        "cpu" => ComputeBackend.Cpu,
        "cuda" => ComputeBackend.Cuda,
        _ => throw new ArgumentException("--backend must be cpu or cuda."),
    };

static string RequiredArgument(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    throw new ArgumentException($"Required argument is missing: {name}");
}

static string? OptionalArgument(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}

internal sealed record ModelManifest(
    string Id,
    string DisplayName,
    string Engine,
    Dictionary<string, string> Files,
    int NumThreads,
    int FeatureDimension);

internal sealed record GpuValidationReport(
    string Engine,
    string Backend,
    string RuntimeVersion,
    string ModelId,
    double ModelLoadMilliseconds,
    double WarmupMilliseconds,
    double DecodeMilliseconds,
    double AudioMilliseconds,
    double RealtimeFactor,
    long PeakWorkerWorkingSetBytes,
    double TotalMilliseconds,
    string Transcription,
    double DecodedAudioMilliseconds = 0,
    int SegmentCount = 0);
