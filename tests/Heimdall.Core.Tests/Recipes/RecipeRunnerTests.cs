/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>A readable stream that a test feeds; it honours cancellation like a pipe does.</summary>
internal sealed class FeedStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

    private byte[] _current = [];

    private int _offset;

    public void Feed(params byte[] bytes) => _chunks.Writer.TryWrite(bytes);

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void End() => _chunks.Writer.TryComplete();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;

            if (_chunks.Reader.TryRead(out byte[]? next))
            {
                _current = next;
                _offset = 0;
            }
        }

        int count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A process that lives in memory: the test decides what it prints and when it ends.</summary>
internal sealed class FakeRecipeProcess : IRecipeProcess
{
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FeedStream Out { get; } = new();

    public FeedStream Err { get; } = new();

    public int KillCount { get; private set; }

    public int InputClosedCount { get; private set; }

    public bool Disposed { get; private set; }

    public int ExitCodeValue { get; set; }

    /// <summary>When true, killing the process also ends its pipes, as for a real process.</summary>
    public bool KillEndsPipes { get; set; } = true;

    public Stream StandardOutput => Out;

    public Stream StandardError => Err;

    public int ExitCode => ExitCodeValue;

    public void CloseStandardInput() => InputClosedCount++;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public void KillTree()
    {
        KillCount++;
        if (KillEndsPipes)
            Finish(-1, endPipes: true);
    }

    public void Finish(int exitCode, bool endPipes = true)
    {
        ExitCodeValue = exitCode;
        if (endPipes)
        {
            Out.End();
            Err.End();
        }

        _exited.TrySetResult();
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeRecipeLauncher : IRecipeProcessLauncher
{
    public FakeRecipeProcess Process { get; } = new();

    public ProcessStartInfo? StartInfo { get; private set; }

    public int StartCount { get; private set; }

    public Exception? Failure { get; init; }

    public Action<FakeRecipeProcess>? OnStart { get; init; }

    public IRecipeProcess Start(ProcessStartInfo startInfo)
    {
        StartCount++;
        StartInfo = startInfo;
        if (Failure is not null)
            throw Failure;

        OnStart?.Invoke(Process);
        return Process;
    }
}

/// <summary>The runner, through the process seam: no real process is started.</summary>
public sealed class RecipeRunnerTests : IDisposable
{
    private const string Program = @"C:\Windows\System32\hostname.exe";

    private readonly ScratchFolder _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static Recipe MakeRecipe(
        RecipeStderrMode stderr = RecipeStderrMode.Separate,
        RecipeOutputEncoding encoding = RecipeOutputEncoding.Utf8,
        int timeoutSeconds = 30,
        int[]? successCodes = null)
        => RecipeTestData.Minimal() with
        {
            Stderr = stderr,
            OutputEncoding = encoding,
            TimeoutSeconds = timeoutSeconds,
            SuccessExitCodes = successCodes ?? [0],
        };

    private string EmptyFolder() => _scratch.Dir("work-" + Guid.NewGuid().ToString("N"));

    private Task<RecipeRunResult> Run(FakeRecipeLauncher launcher, Recipe recipe, IReadOnlyList<string>? arguments = null, string? folder = null, CancellationToken token = default)
        => new RecipeRunner(launcher).RunAsync(recipe, Program, arguments ?? [], folder ?? EmptyFolder(), token);

    [Fact]
    public async Task EveryArgument_GoesIntoArgumentList_OnePerEntry_AndNeverThroughArguments()
    {
        string[] arguments = ["two words", "--x=\"quoted\"", "-leading-dash", "", "a&b|c", "/slash"];
        FakeRecipeLauncher launcher = new() { OnStart = process => process.Finish(0) };
        string folder = EmptyFolder();

        RecipeRunResult result = await Run(launcher, MakeRecipe(), arguments, folder);

        Assert.True(result.IsSuccess);
        ProcessStartInfo info = launcher.StartInfo!;
        Assert.Equal(arguments, info.ArgumentList);
        Assert.Equal(string.Empty, info.Arguments);
        Assert.Equal(Program, info.FileName);
        Assert.Equal(folder, info.WorkingDirectory);
    }

    [Fact]
    public async Task TheStartInfo_HasNoShellNoWindowRedirectedPipesNoVerbAndNoEnvironmentChange()
    {
        FakeRecipeLauncher launcher = new() { OnStart = process => process.Finish(0) };

        await Run(launcher, MakeRecipe());

        ProcessStartInfo info = launcher.StartInfo!;
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.True(info.RedirectStandardInput);
        Assert.Equal(string.Empty, info.Verb);
        Assert.True(string.IsNullOrEmpty(info.UserName));
        Assert.Equal(Environment.GetEnvironmentVariables().Count, info.Environment.Count);
    }

    [Fact]
    public async Task TheInput_IsClosedAtOnce_AndTheProcessIsDisposed()
    {
        FakeRecipeLauncher launcher = new() { OnStart = process => process.Finish(0) };

        await Run(launcher, MakeRecipe());

        Assert.Equal(1, launcher.Process.InputClosedCount);
        Assert.True(launcher.Process.Disposed);
    }

    [Fact]
    public async Task AWorkingDirectoryThatDoesNotExist_RefusesWithoutStarting()
    {
        FakeRecipeLauncher launcher = new();

        RecipeRunResult result = await Run(launcher, MakeRecipe(), folder: Path.Combine(_scratch.Root, "missing"));

        Assert.Equal(RecipeRunStatus.Refused, result.Status);
        Assert.Equal(RecipeRunRefusal.WorkingDirectoryMissing, result.Refusal);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task AWorkingDirectoryThatIsNotEmpty_RefusesWithoutStarting()
    {
        FakeRecipeLauncher launcher = new();
        string folder = EmptyFolder();
        File.WriteAllText(Path.Combine(folder, "leftover.txt"), "x");

        RecipeRunResult result = await Run(launcher, MakeRecipe(), folder: folder);

        Assert.Equal(RecipeRunRefusal.WorkingDirectoryNotEmpty, result.Refusal);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task AnEmptySubfolderMakesTheWorkingDirectoryNonEmpty()
    {
        FakeRecipeLauncher launcher = new();
        string folder = EmptyFolder();
        Directory.CreateDirectory(Path.Combine(folder, "sub"));

        Assert.Equal(RecipeRunRefusal.WorkingDirectoryNotEmpty, (await Run(launcher, MakeRecipe(), folder: folder)).Refusal);
    }

    [Theory]
    [InlineData("work")]
    [InlineData("")]
    [InlineData(@"\\server\share")]
    public async Task ARelativeOrNetworkWorkingDirectory_RefusesWithoutStarting(string folder)
    {
        FakeRecipeLauncher launcher = new();

        RecipeRunResult result = await Run(launcher, MakeRecipe(), folder: folder);

        Assert.Equal(RecipeRunRefusal.WorkingDirectoryNotAbsolute, result.Refusal);
        Assert.Equal(0, launcher.StartCount);
    }

    [Theory]
    [InlineData("hostname.exe")]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData("")]
    public async Task AProgramThatIsNotAnAbsoluteDrivePath_RefusesWithoutStarting(string program)
    {
        FakeRecipeLauncher launcher = new();

        RecipeRunResult result = await new RecipeRunner(launcher).RunAsync(MakeRecipe(), program, [], EmptyFolder());

        Assert.Equal(RecipeRunRefusal.ProgramNotAbsolute, result.Refusal);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task OutputPastTheCap_IsFlaggedTruncated_AndTheProcessIsStillDrainedToItsEnd()
    {
        const int Chunk = 4096;
        int chunks = (RecipeLimits.MaxCaptureBytes / Chunk) + 10;
        FakeRecipeLauncher launcher = new();
        byte[] block = Enumerable.Repeat((byte)'x', Chunk).ToArray();
        for (int index = 0; index < chunks; index++)
            launcher.Process.Out.Feed(block);

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe());
        launcher.Process.Finish(0);
        RecipeRunResult result = await run;

        Assert.True(result.Truncated);
        Assert.Equal(RecipeLimits.MaxCaptureBytes, result.Output.Length);
        Assert.Equal(RecipeRunStatus.Succeeded, result.Status);
        Assert.Equal(0, launcher.Process.KillCount);
    }

    [Fact]
    public async Task OutputThatCrossesTheCapInsideOneRead_IsCutExactlyAndFlaggedTruncated()
    {
        const int Chunk = 4096;
        FakeRecipeLauncher launcher = new();
        byte[] block = Enumerable.Repeat((byte)'y', Chunk).ToArray();
        for (int index = 0; index < (RecipeLimits.MaxCaptureBytes / Chunk) - 1; index++)
            launcher.Process.Out.Feed(block);

        // The cap falls in the middle of this last read: 1 byte still fits, 2 are offered.
        launcher.Process.Out.Feed(Enumerable.Repeat((byte)'y', Chunk - 1).ToArray());
        launcher.Process.Out.Feed(0x7A, 0x7A);

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe());
        launcher.Process.Finish(0);
        RecipeRunResult result = await run;

        Assert.True(result.Truncated);
        Assert.Equal(RecipeLimits.MaxCaptureBytes, result.Output.Length);
        Assert.Equal('z', result.Output[^1]);
    }

    [Fact]
    public async Task OutputUnderTheCap_IsNotFlaggedTruncated()
    {
        FakeRecipeLauncher launcher = new();
        launcher.Process.Out.Feed("hello");

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe());
        launcher.Process.Finish(0);
        RecipeRunResult result = await run;

        Assert.False(result.Truncated);
        Assert.Equal("hello", result.Output);
    }

    [Theory]
    [InlineData(0, RecipeRunStatus.Succeeded)]
    [InlineData(1, RecipeRunStatus.FailedExitCode)]
    [InlineData(3, RecipeRunStatus.Succeeded)]
    [InlineData(-1, RecipeRunStatus.FailedExitCode)]
    [InlineData(259, RecipeRunStatus.FailedExitCode)]
    public async Task TheExitCode_IsComparedWithTheSuccessList(int exitCode, RecipeRunStatus expected)
    {
        FakeRecipeLauncher launcher = new() { OnStart = process => process.Finish(exitCode) };

        RecipeRunResult result = await Run(launcher, MakeRecipe(successCodes: [0, 3]));

        Assert.Equal(expected, result.Status);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(expected == RecipeRunStatus.Succeeded, result.IsSuccess);
    }

    [Fact]
    public async Task ATimeout_KillsTheWholeTree_AndKeepsWhatWasPrinted()
    {
        FakeRecipeLauncher launcher = new();
        launcher.Process.Out.Feed("partial");

        RecipeRunResult result = await Run(launcher, MakeRecipe(timeoutSeconds: RecipeLimits.MinTimeoutSeconds));

        Assert.Equal(RecipeRunStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal(1, launcher.Process.KillCount);
        Assert.Equal("partial", result.Output);
        Assert.True(launcher.Process.Disposed);
    }

    [Fact]
    public async Task ACancellation_KillsTheWholeTree()
    {
        FakeRecipeLauncher launcher = new();
        using CancellationTokenSource cancel = new();

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe(), token: cancel.Token);
        await cancel.CancelAsync();
        RecipeRunResult result = await run;

        Assert.Equal(RecipeRunStatus.Cancelled, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal(1, launcher.Process.KillCount);
        Assert.True(launcher.Process.Disposed);
    }

    [Fact]
    public async Task ACancellationAlreadyRequested_StartsNothing()
    {
        FakeRecipeLauncher launcher = new();
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        RecipeRunResult result = await Run(launcher, MakeRecipe(), token: cancel.Token);

        Assert.Equal(RecipeRunStatus.Cancelled, result.Status);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task AProcessThatCannotStart_IsReportedByExceptionTypeOnly()
    {
        const string SecretMessage = "The system cannot find the file C:\\Users\\someone\\secret.exe";
        FakeRecipeLauncher launcher = new() { Failure = new Win32Exception(2, SecretMessage) };

        RecipeRunResult result = await Run(launcher, MakeRecipe());

        Assert.Equal(RecipeRunStatus.FailedToStart, result.Status);
        Assert.Equal(nameof(Win32Exception), result.StartFailureType);
        Assert.Null(result.ExitCode);
        Assert.DoesNotContain(SecretMessage, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeparateMode_KeepsStandardErrorApart()
    {
        FakeRecipeLauncher launcher = new();
        launcher.Process.Out.Feed("to-out");
        launcher.Process.Err.Feed("to-err");

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe(RecipeStderrMode.Separate));
        launcher.Process.Finish(0);
        RecipeRunResult result = await run;

        Assert.Equal("to-out", result.Output);
        Assert.Equal("to-err", result.ErrorOutput);
    }

    [Fact]
    public async Task MergeMode_PutsBothStreamsIntoTheOutput()
    {
        FakeRecipeLauncher launcher = new();
        launcher.Process.Out.Feed("to-out;");
        launcher.Process.Err.Feed("to-err;");

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe(RecipeStderrMode.Merge));
        launcher.Process.Finish(0);
        RecipeRunResult result = await run;

        Assert.Equal(string.Empty, result.ErrorOutput);
        Assert.Contains("to-out;", result.Output, StringComparison.Ordinal);
        Assert.Contains("to-err;", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeMode_SharesOneCapBetweenTheTwoStreams()
    {
        const int Chunk = 4096;
        FakeRecipeLauncher launcher = new();
        byte[] block = Enumerable.Repeat((byte)'e', Chunk).ToArray();
        for (int index = 0; index < (RecipeLimits.MaxCaptureBytes / Chunk) + 2; index++)
        {
            launcher.Process.Out.Feed(block);
            launcher.Process.Err.Feed(block);
        }

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe(RecipeStderrMode.Merge));
        launcher.Process.Finish(0);
        RecipeRunResult result = await run;

        Assert.True(result.Truncated);
        Assert.Equal(RecipeLimits.MaxCaptureBytes, result.Output.Length);
    }

    [Fact]
    public async Task Utf8Output_IsDecodedEvenWhenACharacterIsSplitAcrossReads()
    {
        FakeRecipeLauncher launcher = new();
        launcher.Process.Out.Feed(0xC3);
        launcher.Process.Out.Feed(0xA9);
        launcher.Process.Out.Feed("t\u00E9");

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe());
        launcher.Process.Finish(0);

        Assert.Equal("\u00E9t\u00E9", (await run).Output);
    }

    [Fact]
    public async Task OemOutput_IsDecodedWithTheOemCodePage_NotAsUtf8()
    {
        byte[] bytes = [0x82, 0x41, 0x94];
        FakeRecipeLauncher launcher = new();
        launcher.Process.Out.Feed(bytes);

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe(encoding: RecipeOutputEncoding.Oem));
        launcher.Process.Finish(0);
        RecipeRunResult result = await run;

        Encoding oem = RecipeOutputEncodings.Resolve(RecipeOutputEncoding.Oem);
        Assert.Equal(oem.GetString(bytes), result.Output);
        Assert.NotEqual(Encoding.UTF8.GetString(bytes), result.Output);
        Assert.NotEqual(Encoding.UTF8.CodePage, oem.CodePage);
    }

    [Fact]
    public async Task APipeKeptOpenByAGrandchild_DoesNotHoldTheRunBeyondTheGrace()
    {
        FakeRecipeLauncher launcher = new();
        launcher.Process.Out.Feed("seen");
        Stopwatch clock = Stopwatch.StartNew();

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe());
        launcher.Process.Finish(0, endPipes: false);
        RecipeRunResult result = await run;

        Assert.Equal(RecipeRunStatus.Succeeded, result.Status);
        Assert.Equal("seen", result.Output);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(RecipeLimits.DrainGraceMilliseconds * 5));
    }

    [Fact]
    public async Task AKillThatDoesNotEndTheProcess_StillEndsTheRunAfterTheBound()
    {
        FakeRecipeLauncher launcher = new();
        launcher.Process.KillEndsPipes = false;
        using CancellationTokenSource cancel = new();

        Task<RecipeRunResult> run = Run(launcher, MakeRecipe(), token: cancel.Token);
        await cancel.CancelAsync();
        RecipeRunResult result = await run;

        Assert.Equal(RecipeRunStatus.Cancelled, result.Status);
        Assert.Equal(1, launcher.Process.KillCount);
        Assert.True(launcher.Process.Disposed);
    }

    [Fact]
    public void TheCapAndTheGraceAreNamedConstants()
    {
        Assert.True(RecipeLimits.MaxCaptureBytes > 0);
        Assert.True(RecipeLimits.DrainGraceMilliseconds > 0);
        Assert.True(RecipeLimits.KillWaitMilliseconds > 0);
    }
}
