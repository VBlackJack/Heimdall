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

using System.Diagnostics;
using System.Text;

namespace Heimdall.Core.Recipes;

/// <summary>How a run ended.</summary>
public enum RecipeRunStatus
{
    /// <summary>The program ended on its own with an exit code listed in <c>successExitCodes</c>.</summary>
    Succeeded,

    /// <summary>The program ended on its own with another exit code.</summary>
    FailedExitCode,

    /// <summary>The recipe timeout elapsed; the process tree was killed.</summary>
    TimedOut,

    /// <summary>The caller cancelled; the process tree was killed.</summary>
    Cancelled,

    /// <summary>The process could not be started; <see cref="RecipeRunResult.StartFailureType"/> says why.</summary>
    FailedToStart,

    /// <summary>The run was refused before any start; <see cref="RecipeRunResult.Refusal"/> says why.</summary>
    Refused,
}

/// <summary>Why a run was refused before the process was started.</summary>
public enum RecipeRunRefusal
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>The program is not an absolute drive path.</summary>
    ProgramNotAbsolute,

    /// <summary>The working directory is not an absolute path.</summary>
    WorkingDirectoryNotAbsolute,

    /// <summary>The working directory does not exist.</summary>
    WorkingDirectoryMissing,

    /// <summary>The working directory is not empty.</summary>
    WorkingDirectoryNotEmpty,
}

/// <summary>The outcome of one run. Data only: a later layer turns it into text.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="ExitCode">The exit code when the program ended on its own; null otherwise.</param>
/// <param name="Output">The decoded standard output, with standard error interleaved when the recipe merges it.</param>
/// <param name="ErrorOutput">The decoded standard error; empty when the recipe merges it into <paramref name="Output"/>.</param>
/// <param name="Truncated">True when <see cref="RecipeLimits.MaxCaptureBytes"/> was exceeded and the rest was discarded.</param>
/// <param name="StartFailureType">The exception type name when the process could not start; null otherwise. The message is never kept.</param>
/// <param name="Refusal">Why the run was refused; <see cref="RecipeRunRefusal.None"/> otherwise.</param>
public sealed record RecipeRunResult(
    RecipeRunStatus Status,
    int? ExitCode,
    string Output,
    string ErrorOutput,
    bool Truncated,
    string? StartFailureType,
    RecipeRunRefusal Refusal)
{
    /// <summary>True when the program ended on its own with a success exit code.</summary>
    public bool IsSuccess => Status == RecipeRunStatus.Succeeded;

    /// <summary>
    /// True when the process could not be placed in a job object (Heimdall may itself run in a job
    /// that forbids nesting), so that a child it left behind was only killed if the run was interrupted.
    /// </summary>
    public bool JobUnavailable { get; init; }

    internal static RecipeRunResult Refused(RecipeRunRefusal refusal)
        => new(RecipeRunStatus.Refused, null, string.Empty, string.Empty, false, null, refusal);

    internal static RecipeRunResult StartFailed(string exceptionType)
        => new(RecipeRunStatus.FailedToStart, null, string.Empty, string.Empty, false, exceptionType, RecipeRunRefusal.None);
}

/// <summary>
/// Runs one resolved program with an already expanded argument list: no shell, no window, no
/// elevation, every argument a separate <c>ArgumentList</c> entry, output captured within a bound.
/// Every process runs inside a job object that ends its children when the run ends, whatever the
/// outcome; when the job cannot be used the result says so.
/// </summary>
public sealed class RecipeRunner : IRecipeRunner
{
    private const int ReadChunkBytes = 4096;

    private const int DriveColonIndex = 1;

    private const int DriveRootLength = 3;

    private const char ByteOrderMark = '\uFEFF';

    private readonly IRecipeProcessLauncher _launcher;

    private readonly Func<RecipeOutputEncoding, Encoding> _resolveEncoding;

    /// <summary>Creates a runner that starts real processes.</summary>
    public RecipeRunner()
        : this(new SystemRecipeProcessLauncher())
    {
    }

    internal RecipeRunner(IRecipeProcessLauncher launcher)
        : this(launcher, RecipeOutputEncodings.Resolve)
    {
    }

    /// <summary>Creates a runner with the encodings resolved by <paramref name="resolveEncoding"/>; a test passes code pages it chose.</summary>
    internal RecipeRunner(IRecipeProcessLauncher launcher, Func<RecipeOutputEncoding, Encoding> resolveEncoding)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _resolveEncoding = resolveEncoding ?? throw new ArgumentNullException(nameof(resolveEncoding));
    }

    /// <summary>Runs a program.</summary>
    /// <param name="recipe">The recipe: output encoding, standard error mode, success exit codes and timeout.</param>
    /// <param name="programPath">The absolute path from <see cref="RecipeProgramResolver"/>.</param>
    /// <param name="arguments">The output of <see cref="RecipeExpander.Expand"/>, one entry per argument.</param>
    /// <param name="workingDirectory">An existing empty folder owned by the caller.</param>
    /// <param name="cancellationToken">Cancels the run; the process tree is killed.</param>
    public async Task<RecipeRunResult> RunAsync(
        Recipe recipe,
        string programPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(programPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        RecipeRunRefusal refusal = CheckPreconditions(programPath, workingDirectory);
        if (refusal != RecipeRunRefusal.None)
            return RecipeRunResult.Refused(refusal);

        if (cancellationToken.IsCancellationRequested)
            return new RecipeRunResult(RecipeRunStatus.Cancelled, null, string.Empty, string.Empty, false, null, RecipeRunRefusal.None);

        Encoding encoding = _resolveEncoding(recipe.OutputEncoding);
        ProcessStartInfo startInfo = BuildStartInfo(programPath, arguments, workingDirectory, encoding);

        IRecipeProcess process;
        try
        {
            process = _launcher.Start(startInfo);
        }
        catch (Exception ex)
        {
            // Any failure to start (missing file, access denied, a bad image) is data: the type name only.
            return RecipeRunResult.StartFailed(ex.GetType().Name);
        }

        using (process)
            return await RunStartedAsync(recipe, process, encoding, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The start parameters of a run. Internal so that a test can read them.</summary>
    internal static ProcessStartInfo BuildStartInfo(
        string programPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Encoding outputEncoding)
    {
        ProcessStartInfo info = new()
        {
            FileName = programPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
            WorkingDirectory = workingDirectory,
        };

        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        return info;
    }

    private static RecipeRunRefusal CheckPreconditions(string programPath, string workingDirectory)
    {
        if (!IsDrivePath(programPath))
            return RecipeRunRefusal.ProgramNotAbsolute;

        if (!IsDrivePath(workingDirectory))
            return RecipeRunRefusal.WorkingDirectoryNotAbsolute;

        if (!Directory.Exists(workingDirectory))
            return RecipeRunRefusal.WorkingDirectoryMissing;

        try
        {
            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(workingDirectory).GetEnumerator();
            if (entries.MoveNext())
                return RecipeRunRefusal.WorkingDirectoryNotEmpty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RecipeRunRefusal.WorkingDirectoryMissing;
        }

        return RecipeRunRefusal.None;
    }

    private static bool IsDrivePath(string path)
        => path.Length >= DriveRootLength
            && char.IsAsciiLetter(path[0])
            && path[DriveColonIndex] == ':'
            && path[DriveColonIndex + 1] is '\\' or '/';

    private static async Task<RecipeRunResult> RunStartedAsync(
        Recipe recipe,
        IRecipeProcess process,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        process.CloseStandardInput();

        bool merge = recipe.Stderr == RecipeStderrMode.Merge;
        CaptureBuffer output = new();
        CaptureBuffer error = merge ? output : new CaptureBuffer();

        using CancellationTokenSource drain = new();
        bool stripMark = RecipeOutputEncodings.StripsLeadingMark(encoding);
        Task outputPump = PumpAsync(process.StandardOutput, encoding.GetDecoder(), output, stripMark, drain.Token);
        Task errorPump = PumpAsync(process.StandardError, encoding.GetDecoder(), error, stripMark, drain.Token);

        RecipeRunStatus? interrupted = null;
        using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(recipe.TimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                interrupted = cancellationToken.IsCancellationRequested ? RecipeRunStatus.Cancelled : RecipeRunStatus.TimedOut;
            }
        }

        if (interrupted is not null)
        {
            process.KillTree();
            await WaitUntilGoneAsync(process).ConfigureAwait(false);
        }

        // The pipes close with the process. A grandchild that kept one open must not hold the run: the
        // readers get a short grace and are then cancelled. What the program left running is ended when
        // the process is disposed, which closes its job.
        drain.CancelAfter(RecipeLimits.DrainGraceMilliseconds);
        await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);

        bool truncated = output.Truncated || error.Truncated;
        string text = output.Text;
        string errorText = merge ? string.Empty : error.Text;

        bool jobUnavailable = process.JobUnavailable;
        if (interrupted is not null)
            return new RecipeRunResult(interrupted.Value, null, text, errorText, truncated, null, RecipeRunRefusal.None) { JobUnavailable = jobUnavailable };

        int exitCode = process.ExitCode;
        RecipeRunStatus status = recipe.SuccessExitCodes.Contains(exitCode)
            ? RecipeRunStatus.Succeeded
            : RecipeRunStatus.FailedExitCode;
        return new RecipeRunResult(status, exitCode, text, errorText, truncated, null, RecipeRunRefusal.None) { JobUnavailable = jobUnavailable };
    }

    private static async Task WaitUntilGoneAsync(IRecipeProcess process)
    {
        using CancellationTokenSource bound = new(RecipeLimits.KillWaitMilliseconds);
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The process outlived the kill: the run still ends, and the process is disposed by the caller.
        }
    }

    private static async Task PumpAsync(Stream stream, Decoder decoder, CaptureBuffer sink, bool stripMark, CancellationToken token)
    {
        // One stream, one mark: standard error that is merged into the output has its own first character.
        StreamStart start = new(stripMark);
        byte[] buffer = new byte[ReadChunkBytes];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                    break;

                sink.Append(decoder, buffer, read, start);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancelled after the grace period, or the pipe broke: what was read so far is kept.
        }
    }

    /// <summary>Whether the first decoded character of one stream is still to be looked at for a byte order mark.</summary>
    private sealed class StreamStart
    {
        public StreamStart(bool strip)
        {
            Pending = strip;
        }

        public bool Pending { get; set; }
    }

    /// <summary>A bounded, thread-safe text buffer; two pumps share one when standard error is merged.</summary>
    private sealed class CaptureBuffer
    {
        private readonly object _gate = new();

        private readonly StringBuilder _text = new();

        private long _bytes;

        public bool Truncated { get; private set; }

        public string Text
        {
            get
            {
                lock (_gate)
                    return _text.ToString();
            }
        }

        public void Append(Decoder decoder, byte[] buffer, int count, StreamStart start)
        {
            lock (_gate)
            {
                long remaining = RecipeLimits.MaxCaptureBytes - _bytes;
                if (remaining <= 0)
                {
                    Truncated = true;
                    return;
                }

                int take = (int)Math.Min(count, remaining);
                if (take < count)
                    Truncated = true;

                _bytes += take;

                char[] chars = new char[decoder.GetCharCount(buffer, 0, take, flush: false)];
                int length = decoder.GetChars(buffer, 0, take, chars, 0, flush: false);
                int skip = 0;
                if (start.Pending && length > 0)
                {
                    // Exactly one leading byte order mark of the stream is dropped, and only the first.
                    start.Pending = false;
                    skip = chars[0] == ByteOrderMark ? 1 : 0;
                }

                _text.Append(chars, skip, length - skip);
            }
        }
    }
}
