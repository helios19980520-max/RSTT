using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Input;

/// <summary>
/// Serializes exact Unicode delivery to the foreground HWND captured when each
/// request is accepted. Accepted SendInput records are never replayed.
/// </summary>
public sealed partial class Win32TextInjectionService : ITextInjectionService, IDisposable
{
    private const int BlockUtf16UnitCount = 64;
    private const int QueueCapacity = 64;
    private const int MaximumPartialContinuations = 3;
    private const uint InputKeyboard = 1;
    private const uint KeyEventFUnicode = 0x0004;
    private const uint KeyEventFKeyUp = 0x0002;

    private readonly ILogger<Win32TextInjectionService> _logger;
    private readonly IWin32InputApi _native;
    private readonly Channel<InjectionWorkItem> _requests;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly uint _currentProcessId;
    private bool _disposed;

    public Win32TextInjectionService(ILogger<Win32TextInjectionService> logger)
        : this(logger, new Win32InputApi(), (uint)Environment.ProcessId)
    {
    }

    internal Win32TextInjectionService(
        ILogger<Win32TextInjectionService> logger,
        IWin32InputApi native,
        uint currentProcessId)
    {
        _logger = logger;
        _native = native;
        _currentProcessId = currentProcessId;
        _requests = Channel.CreateBounded<InjectionWorkItem>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ProcessRequestsAsync);
    }

    internal static int NativeInputSize => Marshal.SizeOf<NativeInput>();

    public Task<TextInjectionResult> InjectAsync(
        InjectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Text.Length == 0)
        {
            return Task.FromResult(Result(
                TextInjectionStatus.Success,
                request,
                0,
                0,
                0,
                nint.Zero,
                0,
                0));
        }

        var target = _native.GetForegroundWindow();
        if (target == nint.Zero || !_native.TryGetWindowProcessId(target, out var processId))
        {
            return Task.FromResult(Result(
                TextInjectionStatus.Unavailable,
                request,
                request.Text.Length * 2,
                0,
                0,
                target,
                0,
                _native.GetLastError(),
                "No identifiable foreground window is available."));
        }

        // A self-focused request is deliberately never queued. It must not turn
        // into a backlog that types later when focus leaves RSTT.
        if (processId == _currentProcessId)
        {
            return Task.FromResult(Result(
                TextInjectionStatus.SelfFocused,
                request,
                request.Text.Length * 2,
                0,
                0,
                target,
                processId,
                0,
                "RSTT is focused; this commit was dropped."));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TextInjectionResult>(cancellationToken);
        }

        var completion = new TaskCompletionSource<TextInjectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workItem = new InjectionWorkItem(
            request,
            target,
            processId,
            completion,
            cancellationToken);

        if (!_requests.Writer.TryWrite(workItem))
        {
            return Task.FromResult(Result(
                TextInjectionStatus.Unavailable,
                request,
                request.Text.Length * 2,
                0,
                0,
                target,
                processId,
                0,
                "The ordered injection queue is full or stopping."));
        }

        LogQueued(
            _logger,
            request.SessionGenerationId.Value,
            request.CommitId,
            request.SequenceId,
            request.Text.Length,
            ComputeTextHash(request.Text),
            target,
            processId);
        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _requests.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Disposal is best effort. Individual requests are completed below.
        }

        while (_requests.Reader.TryRead(out var pending))
        {
            pending.Completion.TrySetResult(Result(
                TextInjectionStatus.Unavailable,
                pending.Request,
                pending.Request.Text.Length * 2,
                0,
                0,
                pending.TargetWindow,
                pending.TargetProcessId,
                0,
                "Text injection stopped before this request was processed."));
        }

        _shutdown.Dispose();
    }

    internal static NativeInput[] CreateUnicodeInputs(ReadOnlySpan<char> text)
    {
        var inputs = new NativeInput[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            inputs[index * 2] = CreateInput(text[index], KeyEventFUnicode);
            inputs[(index * 2) + 1] = CreateInput(
                text[index],
                KeyEventFUnicode | KeyEventFKeyUp);
        }

        return inputs;
    }

    private async Task ProcessRequestsAsync()
    {
        try
        {
            await foreach (var workItem in _requests.Reader.ReadAllAsync(_shutdown.Token)
                               .ConfigureAwait(false))
            {
                if (workItem.CancellationToken.IsCancellationRequested)
                {
                    workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                    continue;
                }

                try
                {
                    workItem.Completion.TrySetResult(Deliver(workItem));
                }
                catch (OperationCanceledException) when (workItem.CancellationToken.IsCancellationRequested)
                {
                    workItem.Completion.TrySetCanceled(workItem.CancellationToken);
                }
                catch (Exception exception)
                {
                    LogInjectionFailure(_logger, exception);
                    workItem.Completion.TrySetResult(Result(
                        TextInjectionStatus.Failed,
                        workItem.Request,
                        workItem.Request.Text.Length * 2,
                        0,
                        0,
                        workItem.TargetWindow,
                        workItem.TargetProcessId,
                        _native.GetLastError(),
                        "Text injection failed. See local logs for details."));
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private TextInjectionResult Deliver(InjectionWorkItem workItem)
    {
        var request = workItem.Request;
        var expectedInputCount = checked(request.Text.Length * 2);
        var sentInputCount = 0;
        var utf16Offset = 0;
        var partialContinuations = 0;

        while (utf16Offset < request.Text.Length)
        {
            workItem.CancellationToken.ThrowIfCancellationRequested();
            if (!IsSameForegroundWindow(workItem.TargetWindow))
            {
                return Complete(
                    TextInjectionStatus.TargetChanged,
                    workItem,
                    expectedInputCount,
                    sentInputCount,
                    utf16Offset,
                    0,
                    "The exact foreground window changed before a block was delivered.");
            }

            var unitCount = Math.Min(BlockUtf16UnitCount, request.Text.Length - utf16Offset);
            var block = CreateUnicodeInputs(request.Text.AsSpan(utf16Offset, unitCount));
            var blockRecordOffset = 0;

            while (blockRecordOffset < block.Length)
            {
                workItem.CancellationToken.ThrowIfCancellationRequested();
                var remaining = block[blockRecordOffset..];
                var sent = checked((int)Math.Min(
                    _native.SendInput(remaining),
                    (uint)remaining.Length));
                var error = _native.GetLastError();
                sentInputCount += sent;

                if (sent == remaining.Length)
                {
                    utf16Offset += sent / 2;
                    blockRecordOffset = block.Length;
                    continue;
                }

                if (sent == 0)
                {
                    var status = _native.IsTargetElevated(workItem.TargetProcessId)
                        ? TextInjectionStatus.ElevatedTarget
                        : sentInputCount > 0
                            ? TextInjectionStatus.Partial
                            : TextInjectionStatus.Failed;
                    var message = status == TextInjectionStatus.ElevatedTarget
                        ? "The target has a higher integrity level; Windows UIPI blocks delivery."
                        : DescribeWin32Failure(error, sentInputCount > 0);
                    return Complete(
                        status,
                        workItem,
                        expectedInputCount,
                        sentInputCount,
                        utf16Offset,
                        error,
                        message);
                }

                partialContinuations++;
                var completeUnits = sent / 2;
                utf16Offset += completeUnits;
                blockRecordOffset += completeUnits * 2;

                if ((sent & 1) != 0)
                {
                    // The key-down for this UTF-16 unit was accepted. Send only its
                    // paired key-up, then move beyond the already-delivered unit.
                    var acceptedUnit = request.Text[utf16Offset];
                    var cleanup = new[] { CreateInput(acceptedUnit, KeyEventFUnicode | KeyEventFKeyUp) };
                    var cleanupSent = IsSameForegroundWindow(workItem.TargetWindow)
                        ? _native.SendInput(cleanup)
                        : 0;
                    if (cleanupSent == 1)
                    {
                        sentInputCount++;
                    }

                    utf16Offset++;
                    blockRecordOffset += 2;
                    if (cleanupSent != 1)
                    {
                        return Complete(
                            TextInjectionStatus.Partial,
                            workItem,
                            expectedInputCount,
                            sentInputCount,
                            utf16Offset,
                            _native.GetLastError(),
                            "A partial send left a key down and its cleanup key-up was not accepted.");
                    }
                }

                if (!IsSameForegroundWindow(workItem.TargetWindow))
                {
                    return Complete(
                        TextInjectionStatus.TargetChanged,
                        workItem,
                        expectedInputCount,
                        sentInputCount,
                        utf16Offset,
                        0,
                        "The exact foreground window changed during a partial continuation.");
                }

                if (partialContinuations >= MaximumPartialContinuations &&
                    utf16Offset < request.Text.Length)
                {
                    return Complete(
                        TextInjectionStatus.Partial,
                        workItem,
                        expectedInputCount,
                        sentInputCount,
                        utf16Offset,
                        error,
                        "SendInput remained partial after three positive-progress continuations.");
                }
            }

            if (!IsSameForegroundWindow(workItem.TargetWindow))
            {
                return Complete(
                    TextInjectionStatus.TargetChanged,
                    workItem,
                    expectedInputCount,
                    sentInputCount,
                    utf16Offset,
                    0,
                    "The exact foreground window changed after a block was delivered.");
            }
        }

        return Complete(
            TextInjectionStatus.Success,
            workItem,
            expectedInputCount,
            sentInputCount,
            utf16Offset,
            0,
            null);
    }

    private bool IsSameForegroundWindow(nint expected) =>
        _native.GetForegroundWindow() == expected;

    private TextInjectionResult Complete(
        TextInjectionStatus status,
        InjectionWorkItem workItem,
        int expectedInputCount,
        int sentInputCount,
        int utf16Offset,
        int win32Error,
        string? message)
    {
        LogCompleted(
            _logger,
            workItem.Request.SessionGenerationId.Value,
            workItem.Request.CommitId,
            workItem.Request.Text.Length,
            ComputeTextHash(workItem.Request.Text),
            status,
            expectedInputCount,
            sentInputCount,
            utf16Offset,
            workItem.TargetWindow,
            workItem.TargetProcessId,
            win32Error,
            message);
        return Result(
            status,
            workItem.Request,
            expectedInputCount,
            sentInputCount,
            utf16Offset,
            workItem.TargetWindow,
            workItem.TargetProcessId,
            win32Error,
            message);
    }

    private static TextInjectionResult Result(
        TextInjectionStatus status,
        InjectionRequest request,
        int expectedInputCount,
        int sentInputCount,
        int utf16Offset,
        nint targetWindow,
        uint targetProcessId,
        int win32Error,
        string? message = null) =>
        new(
            status,
            expectedInputCount,
            sentInputCount,
            utf16Offset,
            targetWindow,
            targetProcessId,
            win32Error,
            message);

    private static string DescribeWin32Failure(int error, bool partial) =>
        error == 0
            ? partial
                ? "SendInput stopped after accepting part of the commit."
                : "SendInput accepted no input records."
            : new Win32Exception(error).Message;

    private static string ComputeTextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private static NativeInput CreateInput(char character, uint flags) => new()
    {
        Type = InputKeyboard,
        Keyboard = new NativeKeyboardInput
        {
            VirtualKey = 0,
            ScanCode = character,
            Flags = flags,
        },
    };

    [LoggerMessage(
        LogLevel.Debug,
        "Queued injection generation={Generation} commit={CommitId} sequence={SequenceId} utf16={Utf16Length} hash={TextHash} hwnd={WindowHandle} pid={ProcessId}.")]
    private static partial void LogQueued(
        ILogger logger,
        long generation,
        long commitId,
        long sequenceId,
        int utf16Length,
        string textHash,
        nint windowHandle,
        uint processId);

    [LoggerMessage(
        LogLevel.Debug,
        "Injection completed generation={Generation} commit={CommitId} utf16={Utf16Length} hash={TextHash} status={Status} expected={Expected} sent={Sent} offset={Offset} hwnd={WindowHandle} pid={ProcessId} error={Win32Error} diagnostic={Diagnostic}")]
    private static partial void LogCompleted(
        ILogger logger,
        long generation,
        long commitId,
        int utf16Length,
        string textHash,
        TextInjectionStatus status,
        int expected,
        int sent,
        int offset,
        nint windowHandle,
        uint processId,
        int win32Error,
        string? diagnostic);

    [LoggerMessage(LogLevel.Error, "Text injection worker failed.")]
    private static partial void LogInjectionFailure(ILogger logger, Exception exception);

    private sealed record InjectionWorkItem(
        InjectionRequest Request,
        nint TargetWindow,
        uint TargetProcessId,
        TaskCompletionSource<TextInjectionResult> Completion,
        CancellationToken CancellationToken);
}

internal interface IWin32InputApi
{
    nint GetForegroundWindow();

    bool TryGetWindowProcessId(nint windowHandle, out uint processId);

    uint SendInput(NativeInput[] inputs);

    int GetLastError();

    bool IsTargetElevated(uint targetProcessId);
}

internal sealed class Win32InputApi : IWin32InputApi
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool TryGetWindowProcessId(nint windowHandle, out uint processId) =>
        NativeMethods.GetWindowThreadProcessId(windowHandle, out processId) != 0;

    public uint SendInput(NativeInput[] inputs) =>
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());

    public int GetLastError() => Marshal.GetLastWin32Error();

    public bool IsTargetElevated(uint targetProcessId)
    {
        var currentIntegrity = TryGetIntegrityLevel((uint)Environment.ProcessId);
        var targetIntegrity = TryGetIntegrityLevel(targetProcessId);
        return currentIntegrity.HasValue &&
               targetIntegrity.HasValue &&
               targetIntegrity.Value > currentIntegrity.Value;
    }

    private static int? TryGetIntegrityLevel(uint processId)
    {
        var process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == nint.Zero)
        {
            return null;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                _ = NativeMethods.GetTokenInformation(
                    token,
                    TokenIntegrityLevel,
                    nint.Zero,
                    0,
                    out var requiredLength);
                if (requiredLength == 0)
                {
                    return null;
                }

                var buffer = Marshal.AllocHGlobal(requiredLength);
                try
                {
                    if (!NativeMethods.GetTokenInformation(
                            token,
                            TokenIntegrityLevel,
                            buffer,
                            requiredLength,
                            out _))
                    {
                        return null;
                    }

                    var sid = Marshal.ReadIntPtr(buffer);
                    var countPointer = NativeMethods.GetSidSubAuthorityCount(sid);
                    if (countPointer == nint.Zero)
                    {
                        return null;
                    }

                    var count = Marshal.ReadByte(countPointer);
                    if (count == 0)
                    {
                        return null;
                    }

                    var authority = NativeMethods.GetSidSubAuthority(sid, (uint)(count - 1));
                    return authority == nint.Zero ? null : Marshal.ReadInt32(authority);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(
        uint numberOfInputs,
        [In] NativeInput[] inputs,
        int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        nint tokenHandle,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    internal static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    internal static extern nint GetSidSubAuthority(nint sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}

// The product publishes x64-only. INPUT's anonymous union begins at byte 8 and
// the complete structure is 40 bytes on x64.
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct NativeInput
{
    [FieldOffset(0)]
    public uint Type;

    [FieldOffset(8)]
    public NativeKeyboardInput Keyboard;

    [FieldOffset(8)]
    public NativeMouseInput Mouse;

    [FieldOffset(8)]
    public NativeHardwareInput Hardware;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeKeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMouseInput
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHardwareInput
{
    public uint Message;
    public ushort ParameterLow;
    public ushort ParameterHigh;
}
