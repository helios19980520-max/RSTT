using System.Buffers.Binary;
using System.Text.Json;

namespace RSTT.Core.Workers;

public enum SpeechWorkerMessageType : ushort
{
    Handshake = 1,
    Load = 2,
    Warmup = 3,
    Start = 4,
    Audio = 5,
    Finish = 6,
    Unload = 7,
    Shutdown = 8,
    Ping = 9,
    Ready = 100,
    Hypothesis = 101,
    Performance = 102,
    Fault = 103,
    Pong = 104,
}

public sealed record SpeechWorkerFrame(
    SpeechWorkerMessageType Type,
    long CorrelationId,
    ReadOnlyMemory<byte> Payload);

public sealed record WorkerHandshakeRequest(string ClientVersion, string RequestedEngine);

public sealed record WorkerHandshakeResponse(
    int ProtocolVersion,
    string WorkerVersion,
    string Engine,
    string RuntimeVersion,
    string Backend);

public sealed record WorkerLoadRequest(
    string ModelPath,
    string Language,
    int Threads,
    bool Translate = false);

public sealed record WorkerStartRequest(long SessionGenerationId, long SequenceId);

public sealed record WorkerReadyResponse(string Stage, string Detail);

public sealed record WorkerHypothesisResponse(
    long SessionGenerationId,
    long SequenceId,
    string Text,
    string Language,
    bool IsFinal,
    float? Confidence = null);

public sealed record WorkerPerformanceResponse(
    double AudioMilliseconds,
    double DecodeMilliseconds,
    long WorkingSetBytes);

public sealed record WorkerFaultResponse(
    string Stage,
    string Code,
    string Message,
    bool IsRecoverable);

/// <summary>
/// Versioned, length-prefixed worker transport. The eight-byte body header is
/// little-endian: protocol version, message type, then correlation ID. Audio
/// payloads are raw little-endian float32 samples; control payloads are UTF-8 JSON.
/// </summary>
public static class SpeechWorkerProtocol
{
    public const ushort Version = 1;
    public const int HeaderBytes = 16;
    public const int MaximumPayloadBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask WriteAsync(
        Stream stream,
        SpeechWorkerFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (frame.Payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"Worker payload is {frame.Payload.Length:N0} bytes; maximum is {MaximumPayloadBytes:N0}.");
        }

        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, frame.Payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)frame.Type);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), frame.CorrelationId);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!frame.Payload.IsEmpty)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<SpeechWorkerFrame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderBytes];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (version != Version)
        {
            throw new InvalidDataException(
                $"Speech worker protocol {version} is incompatible with client protocol {Version}.");
        }

        if (payloadLength < 0 || payloadLength > MaximumPayloadBytes)
        {
            throw new InvalidDataException($"Invalid worker payload length {payloadLength:N0}.");
        }

        var typeValue = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if (!Enum.IsDefined(typeof(SpeechWorkerMessageType), typeValue))
        {
            throw new InvalidDataException($"Unknown worker message type {typeValue}.");
        }

        var payload = new byte[payloadLength];
        if (payloadLength > 0 &&
            !await ReadExactlyOrEndAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The speech worker disconnected during a payload.");
        }

        return new SpeechWorkerFrame(
            (SpeechWorkerMessageType)typeValue,
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)),
            payload);
    }

    public static SpeechWorkerFrame Json<T>(
        SpeechWorkerMessageType type,
        long correlationId,
        T value) =>
        new(type, correlationId, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    public static T DeserializeJson<T>(SpeechWorkerFrame frame) =>
        JsonSerializer.Deserialize<T>(frame.Payload.Span, JsonOptions)
        ?? throw new InvalidDataException($"Worker {frame.Type} payload was empty or invalid.");

    public static SpeechWorkerFrame Audio(long correlationId, ReadOnlySpan<float> samples)
    {
        if (samples.Length > MaximumPayloadBytes / sizeof(float))
        {
            throw new InvalidDataException("The audio frame exceeds the worker protocol limit.");
        }

        var payload = new byte[samples.Length * sizeof(float)];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                payload.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(samples[index]));
        }

        return new SpeechWorkerFrame(SpeechWorkerMessageType.Audio, correlationId, payload);
    }

    public static float[] DeserializeAudio(SpeechWorkerFrame frame)
    {
        if (frame.Type != SpeechWorkerMessageType.Audio || frame.Payload.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException("Worker audio payload is not aligned float32 data.");
        }

        var samples = new float[frame.Payload.Length / sizeof(float)];
        for (var index = 0; index < samples.Length; index++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(
                frame.Payload.Span.Slice(index * sizeof(float), sizeof(float)));
            samples[index] = BitConverter.Int32BitsToSingle(bits);
        }

        return samples;
    }

    private static async ValueTask<bool> ReadExactlyOrEndAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var count = await stream
                .ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException(
                    "The speech worker disconnected during a frame header.");
            }

            offset += count;
        }

        return true;
    }
}
