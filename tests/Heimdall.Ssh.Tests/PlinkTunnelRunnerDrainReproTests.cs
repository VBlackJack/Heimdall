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
using System.Globalization;
using System.Text;
using Heimdall.Core.Network;
using Heimdall.Ssh.Plink;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Pins finding C-04 of the SSH audit of 2026-09-06 against a real child process, so the
/// stderr pipe is the one <see cref="Process"/> hands out and not a stand-in: the stderr
/// drain must outlive the caller's connect token. Linked to it, the drain stopped reading
/// once the token was cancelled after the tunnel was up, and a chatty plink then blocked
/// on its next stderr line as soon as the pipe buffer was full, which silently ended the
/// forwarding. (The companion finding C-05, a Stop that would pay the drain join timeout,
/// was refuted on the same child: Stop takes about 50 ms, the pipe read observes cancellation.)
/// </summary>
/// <remarks>
/// The child is <c>cmd.exe /K</c> with its input redirected: the test types commands
/// into it, and <c>echo ... 1&gt;&amp;2</c> is how it is made to write to stderr. A child
/// blocked on a full stderr pipe never reads its next command, so "does it process
/// <c>exit</c>" is the observable.
/// </remarks>
public sealed class PlinkTunnelRunnerDrainReproTests
{
    /// <summary>
    /// Failure bound for the child to process its exit command, not a measure of how
    /// fast it does: a child blocked on a full stderr pipe never exits, so the bound is
    /// only paid when the drain is gone. Under a full parallel test run a 4 s window
    /// was exceeded by a healthy child.
    /// </summary>
    private static readonly TimeSpan ExitVerdictWindow = TimeSpan.FromSeconds(30);
    private const int BurstLines = 200;

    [Fact]
    public async Task C04_Control_WhileTheConnectTokenIsLive_AStderrBurstIsDrainedAndTheChildKeepsRunningCommands()
    {
        using CommandProcessorStandin child = new();
        using CancellationTokenSource connectToken = new();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(1, 2000),
            new OwnedProbe(),
            _ => child);

        PlinkTunnelResult result = await runner.StartAsync(
            CommandProcessorPath,
            "gw.test", 22, "user", null, null,
            "remote", 22, 45150, "SHA256:test",
            connectToken.Token);
        Assert.True(result.Success, result.ErrorMessage);

        child.WriteStderrBurst(BurstLines);
        child.TypeExit();

        bool exited = child.WaitForExit(ExitVerdictWindow);
        Assert.True(
            exited,
            exited
                ? string.Empty
                : "control failed: the child did not reach its exit command although the drain was live. "
                    + child.DescribeDrainState());
    }

    [Fact]
    public async Task C04_CancellingTheConnectTokenAfterEstablishment_LeavesTheDrainReadingAndTheChildRunning()
    {
        using CommandProcessorStandin child = new();
        using CancellationTokenSource connectToken = new();
        using PlinkTunnelRunner runner = new(
            new PlinkTunnelRunnerOptions(1, 2000),
            new OwnedProbe(),
            _ => child);

        PlinkTunnelResult result = await runner.StartAsync(
            CommandProcessorPath,
            "gw.test", 22, "user", null, null,
            "remote", 22, 45151, "SHA256:test",
            connectToken.Token);
        Assert.True(result.Success, result.ErrorMessage);

        // The caller's connect token is cancelled after the tunnel is up. Nothing about the
        // tunnel changed; the child keeps running and keeps writing diagnostics to stderr.
        connectToken.Cancel();
        await Task.Delay(200);

        child.WriteStderrBurst(BurstLines);
        child.TypeExit();

        bool exited = child.WaitForExit(ExitVerdictWindow);
        Assert.True(
            exited,
            "after the connect token was cancelled the child never reached its exit command: "
            + "it is blocked writing to a stderr pipe nobody drains any more. "
            + (exited ? string.Empty : child.DescribeDrainState()));
    }

    private static string CommandProcessorPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    private sealed class OwnedProbe : ITcpListenerOwnershipProbe
    {
        public TcpListenerOwnership Probe(string bindHost, int port, int expectedProcessId)
            => TcpListenerOwnership.OwnedByExpectedProcess;
    }

    /// <summary>
    /// A real <c>cmd.exe /K</c> presented to the runner as the plink process. The runner
    /// reads the process's own stderr pipe; the test drives the child through its stdin.
    /// </summary>
    private sealed class CommandProcessorStandin : IPlinkProcess
    {
        private const int StandardErrorReaderBufferBytes = 4096;
        private const int MillisecondsPerSecond = 1000;
        private static readonly TimeSpan PoolProbeBackstop = TimeSpan.FromSeconds(5);
        private static readonly string Line = new string('x', 100);

        private ObservedReadStream? _observedStream;
        private StreamReader? _standardError;
        private long _burstTimestamp;
        private bool _burstRecorded;
        private PoolSnapshot _poolAtBurst;
        private volatile bool _exitTyped;

        private readonly Process _process = new()
        {
            StartInfo = new ProcessStartInfo(CommandProcessorPath, "/K")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            },
            EnableRaisingEvents = true
        };

        public event EventHandler? Exited
        {
            add => _process.Exited += value;
            remove => _process.Exited -= value;
        }

        public int Id => _process.Id;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public StreamReader StandardError => _standardError ?? _process.StandardError;

        public bool Start()
        {
            bool started = _process.Start();
            _observedStream = new ObservedReadStream(_process.StandardError.BaseStream);
            _standardError = new StreamReader(
                _observedStream,
                _process.StandardError.CurrentEncoding,
                false,
                StandardErrorReaderBufferBytes);
            // Stdout is never read by the runner; discard it so it can never be the pipe that blocks.
            _process.BeginOutputReadLine();
            return started;
        }

        public void Kill()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
            => _process.WaitForExitAsync(cancellationToken);

        public bool WaitForExit(TimeSpan window) => _process.WaitForExit((int)window.TotalMilliseconds);

        /// <summary>Makes the child write <paramref name="lines"/> lines of 100 characters to stderr.</summary>
        public void WriteStderrBurst(int lines)
        {
            _poolAtBurst = PoolSnapshot.Capture();
            Interlocked.Exchange(ref _burstTimestamp, Stopwatch.GetTimestamp());
            _burstRecorded = true;
            _process.StandardInput.WriteLine($"for /L %i in (1,1,{lines}) do @echo {Line} 1>&2");
            _process.StandardInput.Flush();
        }

        public void TypeExit()
        {
            _exitTyped = true;
            _process.StandardInput.WriteLine("exit");
            _process.StandardInput.Flush();
        }

        /// <summary>
        /// One diagnostic line naming where the stderr drain stands, so a failed verdict says
        /// whether the drain was starved, stuck on a read, or never started.
        /// </summary>
        public string DescribeDrainState()
        {
            long now = Stopwatch.GetTimestamp();
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder text = new("drain state: ");

            if (_burstRecorded)
            {
                text.Append(inv, $"elapsed since burst {Millis(Interlocked.Read(ref _burstTimestamp), now)} ms; ");
            }
            else
            {
                text.Append("burst not written; ");
            }

            ObservedReadStream? stream = _observedStream;
            if (stream is null)
            {
                text.Append("no observed stream (process never started); ");
            }
            else
            {
                long started = stream.ReadsStarted;
                long completed = stream.ReadsCompleted;
                long lastStart = stream.LastStartTimestamp;
                long lastCompletion = stream.LastCompletionTimestamp;
                text.Append(inv, $"reads started {started}, completed {completed}, bytes {stream.BytesRead}; ");
                long faulted = stream.ReadsFaulted;
                long cancelled = stream.ReadsCancelled;
                long lastAbort = stream.LastAbortTimestamp;
                text.Append(inv, $"faulted {faulted}, cancelled {cancelled}, end-of-stream {stream.EndOfStreamReads}; ");
                if (faulted + cancelled > 0)
                {
                    text.Append(inv, $"last faulted or cancelled read {Millis(lastAbort, now)} ms ago; ");
                }

                if (started > completed + faulted + cancelled)
                {
                    text.Append(inv, $"a read has been pending for {Millis(lastStart, now)} ms; ");
                }

                if (lastCompletion == 0)
                {
                    text.Append("no read ever completed; ");
                }
                else
                {
                    text.Append(inv, $"last completion {Millis(lastCompletion, now)} ms ago; ");
                }
            }

            PoolSnapshot current = PoolSnapshot.Capture();
            text.Append(inv, $"pool at burst [{_poolAtBurst}], pool now [{current}], completed items delta {current.Completed - _poolAtBurst.Completed}; ");

            using ManualResetEventSlim probeRan = new(false);
            long probeStart = Stopwatch.GetTimestamp();
            ThreadPool.UnsafeQueueUserWorkItem(static gate => gate.Set(), probeRan, preferLocal: false);
            bool dispatched = probeRan.Wait(PoolProbeBackstop);
            long probeEnd = Stopwatch.GetTimestamp();
            if (dispatched)
            {
                text.Append(inv, $"pool probe dispatched in {Millis(probeStart, probeEnd)} ms; ");
            }
            else
            {
                text.Append(inv, $"pool probe NOT dispatched within {PoolProbeBackstop.TotalMilliseconds:0} ms; ");
            }

            try
            {
                bool hasExited = _process.HasExited;
                text.Append(inv, $"child HasExited {hasExited}");
                if (!hasExited)
                {
                    text.Append(inv, $", TotalProcessorTime {_process.TotalProcessorTime.TotalMilliseconds:0} ms");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                text.Append(inv, $"child state unavailable ({ex.GetType().Name})");
            }

            text.Append(inv, $"; exit typed {_exitTyped}");
            return text.ToString();
        }

        private static long Millis(long fromTimestamp, long toTimestamp)
            => (toTimestamp - fromTimestamp) * MillisecondsPerSecond / Stopwatch.Frequency;

        public void Dispose()
        {
            try
            {
                Kill();
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }

    private readonly record struct PoolSnapshot(int Threads, long Pending, long Completed)
    {
        public static PoolSnapshot Capture()
            => new(ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, ThreadPool.CompletedWorkItemCount);

        public override string ToString()
            => string.Create(CultureInfo.InvariantCulture, $"threads {Threads}, pending {Pending}, completed {Completed}");
    }

    /// <summary>Read-only pass-through that counts the reads made on the child's stderr pipe.</summary>
    private sealed class ObservedReadStream(Stream inner) : Stream
    {
        private long _readsStarted;

        public long ReadsStarted => Interlocked.Read(ref _readsStarted);

        private long _readsCompleted;

        public long ReadsCompleted => Interlocked.Read(ref _readsCompleted);

        private long _bytesRead;

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        private long _lastStartTimestamp;

        public long LastStartTimestamp => Interlocked.Read(ref _lastStartTimestamp);

        private long _readsFaulted;

        public long ReadsFaulted => Interlocked.Read(ref _readsFaulted);

        private long _readsCancelled;

        public long ReadsCancelled => Interlocked.Read(ref _readsCancelled);

        private long _endOfStreamReads;

        public long EndOfStreamReads => Interlocked.Read(ref _endOfStreamReads);

        private long _lastAbortTimestamp;

        public long LastAbortTimestamp => Interlocked.Read(ref _lastAbortTimestamp);

        private long _lastCompletionTimestamp;

        public long LastCompletionTimestamp => Interlocked.Read(ref _lastCompletionTimestamp);


        public override bool CanRead => inner.CanRead;

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
            Begin();
            try
            {
                int count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                End(count);
                return count;
            }
            catch (Exception ex)
            {
                Abort(ex);
                throw;
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Begin();
            try
            {
                int read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                End(read);
                return read;
            }
            catch (Exception ex)
            {
                Abort(ex);
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Begin();
            try
            {
                int read = inner.Read(buffer, offset, count);
                End(read);
                return read;
            }
            catch (Exception ex)
            {
                Abort(ex);
                throw;
            }
        }

        public override int Read(Span<byte> buffer)
        {
            Begin();
            try
            {
                int read = inner.Read(buffer);
                End(read);
                return read;
            }
            catch (Exception ex)
            {
                Abort(ex);
                throw;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Begin()
        {
            Interlocked.Increment(ref _readsStarted);
            Interlocked.Exchange(ref _lastStartTimestamp, Stopwatch.GetTimestamp());
        }

        private void Abort(Exception ex)
        {
            Interlocked.Increment(ref ex is OperationCanceledException ? ref _readsCancelled : ref _readsFaulted);
            Interlocked.Exchange(ref _lastAbortTimestamp, Stopwatch.GetTimestamp());
        }

        private void End(int count)
        {
            if (count == 0)
            {
                Interlocked.Increment(ref _endOfStreamReads);
            }

            Interlocked.Add(ref _bytesRead, count);
            Interlocked.Exchange(ref _lastCompletionTimestamp, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _readsCompleted);
        }
    }
}
