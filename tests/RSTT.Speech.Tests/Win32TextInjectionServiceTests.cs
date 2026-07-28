using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.Settings;
using RSTT.Input;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class Win32TextInjectionServiceTests
{
    private static readonly int[] OddPartialOfferedCounts = [4, 1];

    [Theory]
    [InlineData("Latin punctuation: Hello, world!")]
    [InlineData("日本語のテキスト")]
    [InlineData("한국어 텍스트")]
    [InlineData("café déjà vu")]
    [InlineData("emoji 🙂🚀 surrogate pairs")]
    public async Task CompleteSendPreservesExactUtf16Text(string text)
    {
        var native = new FakeWin32InputApi();
        using var service = CreateService(native);

        var result = await service.InjectAsync(Request(text));

        Assert.Equal(TextInjectionStatus.Success, result.Status);
        Assert.Equal(text, native.TypedText);
        Assert.Equal(text.Length, result.CommittedUtf16Offset);
        Assert.Equal(text.Length * 2, result.ExpectedInputCount);
        Assert.Equal(result.ExpectedInputCount, result.SentInputCount);
    }

    [Fact]
    public async Task ZeroSendReportsElevatedOnlyFromIntegrityInspection()
    {
        var native = new FakeWin32InputApi { TargetElevated = true };
        native.SendCounts.Enqueue(0);
        using var service = CreateService(native);

        var result = await service.InjectAsync(Request("blocked"));

        Assert.Equal(TextInjectionStatus.ElevatedTarget, result.Status);
        Assert.Equal(0, result.CommittedUtf16Offset);
        Assert.Empty(native.TypedText);
    }

    [Fact]
    public async Task ZeroSendWithoutElevatedTargetIsFailedNotElevated()
    {
        var native = new FakeWin32InputApi { LastError = 5 };
        native.SendCounts.Enqueue(0);
        using var service = CreateService(native);

        var result = await service.InjectAsync(Request("blocked"));

        Assert.Equal(TextInjectionStatus.Failed, result.Status);
        Assert.Equal(5, result.Win32Error);
    }

    [Fact]
    public async Task EvenPartialContinuesWithoutReplayingAcceptedRecords()
    {
        var native = new FakeWin32InputApi();
        native.SendCounts.Enqueue(4);
        using var service = CreateService(native);

        var result = await service.InjectAsync(Request("abcd"));

        Assert.Equal(TextInjectionStatus.Success, result.Status);
        Assert.Equal("abcd", native.TypedText);
        Assert.Equal(8, result.SentInputCount);
    }

    [Fact]
    public async Task OddPartialSendsOnlyCleanupKeyUpAndAdvancesTheUnit()
    {
        var native = new FakeWin32InputApi();
        native.SendCounts.Enqueue(3);
        native.SendCounts.Enqueue(1);
        using var service = CreateService(native);

        var result = await service.InjectAsync(Request("ab"));

        Assert.Equal(TextInjectionStatus.Success, result.Status);
        Assert.Equal("ab", native.TypedText);
        Assert.Equal(4, result.SentInputCount);
        Assert.Equal(2, result.CommittedUtf16Offset);
        Assert.Equal(OddPartialOfferedCounts, native.OfferedInputCounts.Take(2));
    }

    [Fact]
    public async Task ThreePositivePartialsAbortOnlyTheUnsentRemainder()
    {
        var native = new FakeWin32InputApi();
        native.SendCounts.Enqueue(2);
        native.SendCounts.Enqueue(2);
        native.SendCounts.Enqueue(2);
        using var service = CreateService(native);

        var result = await service.InjectAsync(Request("abcd"));

        Assert.Equal(TextInjectionStatus.Partial, result.Status);
        Assert.Equal("abc", native.TypedText);
        Assert.Equal(3, result.CommittedUtf16Offset);
    }

    [Fact]
    public async Task ExactHwndChangeStopsBeforeTheNextBlock()
    {
        var native = new FakeWin32InputApi { SwitchWindowAfterSend = true };
        using var service = CreateService(native);
        var text = new string('x', 65);

        var result = await service.InjectAsync(Request(text));

        Assert.Equal(TextInjectionStatus.TargetChanged, result.Status);
        Assert.Equal(64, result.CommittedUtf16Offset);
        Assert.Equal(new string('x', 64), native.TypedText);
    }

    [Fact]
    public async Task SelfFocusedCommitIsDroppedWithoutEnteringTheWorker()
    {
        var native = new FakeWin32InputApi { TargetProcessId = 77 };
        using var service = CreateService(native, currentProcessId: 77);

        var result = await service.InjectAsync(Request("must not queue"));

        Assert.Equal(TextInjectionStatus.SelfFocused, result.Status);
        Assert.Equal(0, native.SendCallCount);
    }

    [Fact]
    public async Task ConcurrentRequestsRemainInChannelOrder()
    {
        var native = new FakeWin32InputApi();
        using var service = CreateService(native);

        var first = service.InjectAsync(Request("first", 1));
        var second = service.InjectAsync(Request(" second", 2));
        var third = service.InjectAsync(Request(" third", 3));
        await Task.WhenAll(first, second, third);

        Assert.Equal("first second third", native.TypedText);
    }

    [Fact]
    public async Task BoundedQueueRejectsExcessWorkInsteadOfGrowing()
    {
        var native = new FakeWin32InputApi { BlockSends = true };
        using var service = CreateService(native);
        var active = service.InjectAsync(Request("active", 1));
        Assert.True(native.SendInputEntered.Wait(TimeSpan.FromSeconds(2)));

        var queued = Enumerable.Range(2, 70)
            .Select(index => service.InjectAsync(Request(
                index.ToString(CultureInfo.InvariantCulture),
                index)))
            .ToArray();
        var rejected = queued.Count(task =>
            task.IsCompletedSuccessfully &&
            task.Result.Status == TextInjectionStatus.Unavailable);

        Assert.Equal(6, rejected);
        native.ReleaseSendInput.Set();
        await Task.WhenAll(queued.Prepend(active));
    }

    [Fact]
    public async Task CancelledPriorGenerationWorkNeverTypesAfterRestart()
    {
        var native = new FakeWin32InputApi { BlockSends = true };
        using var service = CreateService(native);
        var active = service.InjectAsync(Request("accepted-", 1, generation: 1));
        Assert.True(native.SendInputEntered.Wait(TimeSpan.FromSeconds(2)));

        using var priorGeneration = new CancellationTokenSource();
        var stale = service.InjectAsync(
            Request("stale", 2, generation: 1),
            priorGeneration.Token);
        priorGeneration.Cancel();
        native.ReleaseSendInput.Set();
        await active;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stale);

        var current = await service.InjectAsync(Request("current", 1, generation: 2));

        Assert.Equal(TextInjectionStatus.Success, current.Status);
        Assert.Equal("accepted-current", native.TypedText);
    }

    [Fact]
    public async Task AutomaticUsesCompatibilityProfileForPackagedModernNotepad()
    {
        var native = new FakeWin32InputApi
        {
            ProcessImagePath =
                @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2604.5.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe",
        };
        using var service = CreateService(
            native,
            mode: TextInjectionDeliveryMode.Automatic);

        var result = await service.InjectAsync(Request(new string('x', 17)));

        Assert.Equal(TextInjectionStatus.Success, result.Status);
        Assert.Equal("Compatibility", result.DeliveryProfile);
        Assert.Equal(1, result.Utf16UnitsPerBlock);
        Assert.Equal(17, result.SendInputCallCount);
        Assert.Equal(17, native.OfferedInputCounts.Count);
        Assert.All(native.OfferedInputCounts, count => Assert.Equal(2, count));
    }

    [Fact]
    public async Task ExplicitDirectOverridesNotepadCompatibilityProfile()
    {
        var native = new FakeWin32InputApi
        {
            ProcessImagePath =
                @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2604.5.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe",
        };
        using var service = CreateService(
            native,
            mode: TextInjectionDeliveryMode.Direct);

        var result = await service.InjectAsync(Request(new string('x', 17)));

        Assert.Equal(TextInjectionStatus.Success, result.Status);
        Assert.Equal("Direct", result.DeliveryProfile);
        Assert.Equal(64, result.Utf16UnitsPerBlock);
        Assert.Equal(1, result.SendInputCallCount);
        Assert.Equal([34], native.OfferedInputCounts);
    }

    private static Win32TextInjectionService CreateService(
        FakeWin32InputApi native,
        uint currentProcessId = 99,
        TextInjectionDeliveryMode mode = TextInjectionDeliveryMode.Automatic) =>
        new(
            NullLogger<Win32TextInjectionService>.Instance,
            native,
            currentProcessId,
            () => mode);

    private static InjectionRequest Request(
        string text,
        long commitId = 1,
        long generation = 1) =>
        new(
            new SessionGenerationId(generation),
            commitId,
            commitId,
            text,
            DateTimeOffset.UtcNow);

    private sealed class FakeWin32InputApi : IWin32InputApi
    {
        private readonly object _gate = new();
        private readonly StringBuilder _typed = new();
        private nint _foregroundWindow = (nint)0x1234;

        public ConcurrentQueue<int> SendCounts { get; } = new();

        public List<int> OfferedInputCounts { get; } = [];

        public ManualResetEventSlim SendInputEntered { get; } = new(false);

        public ManualResetEventSlim ReleaseSendInput { get; } = new(false);

        public uint TargetProcessId { get; init; } = 42;

        public bool TargetElevated { get; init; }

        public bool SwitchWindowAfterSend { get; init; }

        public bool BlockSends { get; init; }

        public int LastError { get; init; }

        public string? ProcessImagePath { get; init; }

        public int SendCallCount { get; private set; }

        public string TypedText
        {
            get
            {
                lock (_gate)
                {
                    return _typed.ToString();
                }
            }
        }

        public nint GetForegroundWindow() => _foregroundWindow;

        public bool TryGetWindowProcessId(nint windowHandle, out uint processId)
        {
            processId = TargetProcessId;
            return windowHandle != nint.Zero;
        }

        public uint SendInput(NativeInput[] inputs)
        {
            SendInputEntered.Set();
            if (BlockSends)
            {
                ReleaseSendInput.Wait(TimeSpan.FromSeconds(5));
            }

            var accepted = SendCounts.TryDequeue(out var scripted)
                ? Math.Clamp(scripted, 0, inputs.Length)
                : inputs.Length;
            lock (_gate)
            {
                SendCallCount++;
                OfferedInputCounts.Add(inputs.Length);
                foreach (var input in inputs.Take(accepted))
                {
                    if ((input.Keyboard.Flags & 0x0002) == 0)
                    {
                        _typed.Append((char)input.Keyboard.ScanCode);
                    }
                }
            }

            if (SwitchWindowAfterSend)
            {
                _foregroundWindow = (nint)0x5678;
            }

            return (uint)accepted;
        }

        public int GetLastError() => LastError;

        public bool IsTargetElevated(uint targetProcessId) => TargetElevated;

        public string? TryGetProcessImagePath(uint targetProcessId) =>
            ProcessImagePath;
    }
}
