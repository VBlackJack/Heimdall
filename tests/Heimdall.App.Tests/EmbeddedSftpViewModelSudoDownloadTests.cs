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

using System.IO;
using System.Text;
using System.Threading.Channels;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpViewModelSudoDownloadTests
{
    [Fact]
    public void BuildSudoInvocation_WithoutStdinPassword_UsesPlainSudo()
    {
        string command = EmbeddedSftpViewModel.BuildSudoInvocation(
            "base64 -- '/etc/ssh/config'",
            false);

        Assert.Equal("sudo base64 -- '/etc/ssh/config'", command);
    }

    [Fact]
    public void BuildSudoInvocation_WithStdinPassword_UsesSudoStdinMode()
    {
        string command = EmbeddedSftpViewModel.BuildSudoInvocation(
            "base64 -- '/etc/ssh/config'",
            true);

        Assert.Equal("sudo -S -p '' base64 -- '/etc/ssh/config'", command);
    }

    [Fact]
    public void BuildSudoBase64DownloadBody_EscapesRemotePath()
    {
        string command = EmbeddedSftpViewModel.BuildSudoBase64DownloadBody("/etc/ssh/it's config");

        Assert.StartsWith("sh -c ", command, StringComparison.Ordinal);
        Assert.Contains("ln -P", command, StringComparison.Ordinal);
        Assert.Contains("exec 3< source", command, StringComparison.Ordinal);
        Assert.Contains("base64 <&3", command, StringComparison.Ordinal);
        Assert.EndsWith(@"sh '/etc/ssh/it'\''s config'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("base64 -- '/etc/ssh", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sudo: a terminal is required to read the password; either use the -S option to read from standard input or configure an askpass helper", nameof(SudoFailureKind.PasswordUnavailable))]
    [InlineData("sudo: no tty present and no askpass program specified", nameof(SudoFailureKind.PasswordUnavailable))]
    [InlineData("sudo: a password is required", nameof(SudoFailureKind.PasswordUnavailable))]
    [InlineData("Sorry, try again.", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: 3 incorrect password attempts", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: no password was provided", nameof(SudoFailureKind.PasswordRejected))]
    [InlineData("sudo: unable to resolve host labbox", nameof(SudoFailureKind.None))]
    [InlineData("", nameof(SudoFailureKind.None))]
    [InlineData(null, nameof(SudoFailureKind.None))]
    public void ClassifySudoStderr_ProducerClassifiesAuthenticationFailures(
        string? stderr,
        string expectedName)
    {
        SudoFailureKind expected = Enum.Parse<SudoFailureKind>(expectedName);
        SudoFailureKind actual = EmbeddedSftpViewModel.ClassifySudoStderr(stderr);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task StreamSudoBase64OutputAsync_FragmentedOutput_WritesBeforeCommandCompletesAndDrainsStderr()
    {
        byte[] expected = new byte[196_608];
        for (int index = 0; index < expected.Length; index++)
        {
            expected[index] = (byte)(index % 251);
        }

        byte[] encoded = Encoding.ASCII.GetBytes(WrapEvery76Characters(Convert.ToBase64String(expected)));
        byte[] standardErrorBytes = Enumerable.Repeat((byte)'e', 131_072).ToArray();
        const int firstFragmentLength = 131_072;
        using FragmentedReadStream standardOutput = new();
        using FragmentedReadStream standardError = new();
        using FirstWriteSignalStream destination = new();
        using CancellationTokenSource commandCancellation = new();
        TaskCompletionSource commandCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> streamingTask = EmbeddedSftpViewModel.StreamSudoBase64OutputAsync(
            standardOutput,
            standardError,
            destination,
            commandCompletion.Task,
            commandCancellation,
            CancellationToken.None);

        await standardError.EnqueueAsync(standardErrorBytes);
        standardError.Complete();
        await standardOutput.EnqueueAsync(encoded[..firstFragmentLength]);
        await destination.FirstWrite.WaitAsync(TimeSpan.FromSeconds(5));
        await standardError.FirstRead.WaitAsync(TimeSpan.FromSeconds(5));
        await standardError.EndOfStreamReached.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(commandCompletion.Task.IsCompleted);
        Assert.Equal(standardErrorBytes.Length, standardError.TotalBytesRead);

        await standardOutput.EnqueueAsync(encoded[firstFragmentLength..]);
        standardOutput.Complete();
        commandCompletion.SetResult();

        string standardErrorText = await streamingTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expected, destination.ToArray());
        Assert.Equal(65_536, standardErrorText.Length);
        Assert.All(standardErrorText, character => Assert.Equal('e', character));
        Assert.False(commandCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task StreamSudoBase64OutputAsync_CommandFailure_PreservesCommandFailure()
    {
        using FragmentedReadStream standardOutput = new();
        using FragmentedReadStream standardError = new();
        using MemoryStream destination = new();
        using CancellationTokenSource commandCancellation = new();
        TaskCompletionSource commandCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException expected = new("SSH channel failed");

        Task<string> streamingTask = EmbeddedSftpViewModel.StreamSudoBase64OutputAsync(
            standardOutput,
            standardError,
            destination,
            commandCompletion.Task,
            commandCancellation,
            CancellationToken.None);

        commandCompletion.SetException(expected);

        IOException actual = await Assert.ThrowsAsync<IOException>(() => streamingTask);

        Assert.Same(expected, actual);
        Assert.True(commandCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task CommitSudoDownloadAsync_Success_AtomicallyReplacesFinalFile()
    {
        string directory = CreateTestDirectory();
        string finalPath = Path.Combine(directory, "protected.bin");
        byte[] replacement = [0x00, 0xff, 0xfe, 0x80, 0x01, 0x7f, 0xc3, 0x28, 0x0a];
        await File.WriteAllBytesAsync(finalPath, [0x10, 0x20, 0x30]);

        try
        {
            using MemoryStream standardOutput = new(
                Encoding.ASCII.GetBytes(Convert.ToBase64String(replacement)));
            using MemoryStream standardError = new();
            using CancellationTokenSource commandCancellation = new();

            await EmbeddedSftpViewModel.CommitSudoDownloadAsync(
                standardOutput,
                standardError,
                Task.CompletedTask,
                () => 0,
                commandCancellation,
                finalPath,
                CancellationToken.None);

            Assert.Equal(replacement, await File.ReadAllBytesAsync(finalPath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.False(commandCancellation.IsCancellationRequested);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommitSudoDownloadAsync_InvalidBase64_PreservesFinalFileAndRemovesTemp()
    {
        string directory = CreateTestDirectory();
        string finalPath = Path.Combine(directory, "protected.bin");
        byte[] original = [0x10, 0x20, 0x30];
        await File.WriteAllBytesAsync(finalPath, original);

        try
        {
            using MemoryStream standardOutput = new(Encoding.ASCII.GetBytes("not-base64!"));
            using MemoryStream standardError = new();
            using CancellationTokenSource commandCancellation = new();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EmbeddedSftpViewModel.CommitSudoDownloadAsync(
                    standardOutput,
                    standardError,
                    Task.CompletedTask,
                    () => 0,
                    commandCancellation,
                    finalPath,
                    CancellationToken.None));

            Assert.Equal(original, await File.ReadAllBytesAsync(finalPath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.True(commandCancellation.IsCancellationRequested);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommitSudoDownloadAsync_NonZeroExit_PreservesFinalFileAndRemovesTemp()
    {
        string directory = CreateTestDirectory();
        string finalPath = Path.Combine(directory, "protected.bin");
        byte[] original = [0x10, 0x20, 0x30];
        await File.WriteAllBytesAsync(finalPath, original);

        try
        {
            byte[] replacement = [0xaa, 0xbb, 0xcc];
            using MemoryStream standardOutput = new(Encoding.ASCII.GetBytes(Convert.ToBase64String(replacement)));
            using MemoryStream standardError = new(Encoding.UTF8.GetBytes("permission denied"));
            using CancellationTokenSource commandCancellation = new();

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EmbeddedSftpViewModel.CommitSudoDownloadAsync(
                    standardOutput,
                    standardError,
                    Task.CompletedTask,
                    () => 17,
                    commandCancellation,
                    finalPath,
                    CancellationToken.None));

            Assert.Contains("exit 17", exception.Message, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllBytesAsync(finalPath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommitSudoDownloadAsync_Cancellation_PreservesFinalFileAndRemovesTemp()
    {
        string directory = CreateTestDirectory();
        string finalPath = Path.Combine(directory, "protected.bin");
        byte[] original = [0x10, 0x20, 0x30];
        await File.WriteAllBytesAsync(finalPath, original);

        try
        {
            using FragmentedReadStream standardOutput = new();
            using FragmentedReadStream standardError = new();
            using CancellationTokenSource commandCancellation = new();
            using CancellationTokenSource cancellation = new();
            TaskCompletionSource commandCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = commandCancellation.Token.Register(
                () => commandCompletion.TrySetCanceled(commandCancellation.Token));

            Task downloadTask = EmbeddedSftpViewModel.CommitSudoDownloadAsync(
                standardOutput,
                standardError,
                commandCompletion.Task,
                () => 0,
                commandCancellation,
                finalPath,
                cancellation.Token);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloadTask);
            Assert.Equal(original, await File.ReadAllBytesAsync(finalPath));
            Assert.Empty(Directory.GetFiles(directory, "*.part"));
            Assert.True(commandCancellation.IsCancellationRequested);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WrapEvery76Characters(string input)
    {
        StringBuilder builder = new();
        for (int index = 0; index < input.Length; index += 76)
        {
            int length = Math.Min(76, input.Length - index);
            builder.Append(input, index, length);
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "heimdall-sftp-sudo-download-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FirstWriteSignalStream : MemoryStream
    {
        private readonly TaskCompletionSource _firstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task FirstWrite => _firstWrite.Task;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _firstWrite.TrySetResult();
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _firstWrite.TrySetResult();
            await base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class FragmentedReadStream : Stream
    {
        private readonly Channel<byte[]> _fragments = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });
        private byte[]? _currentFragment;
        private int _currentOffset;
        private int _totalBytesRead;
        private readonly TaskCompletionSource _firstRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _endOfStreamReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal Task FirstRead => _firstRead.Task;

        internal Task EndOfStreamReached => _endOfStreamReached.Task;

        internal int TotalBytesRead => Volatile.Read(ref _totalBytesRead);

        internal ValueTask EnqueueAsync(byte[] fragment)
        {
            return _fragments.Writer.WriteAsync(fragment);
        }

        internal void Complete()
        {
            _fragments.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_currentFragment is null || _currentOffset == _currentFragment.Length)
            {
                if (!await _fragments.Reader.WaitToReadAsync(cancellationToken))
                {
                    _endOfStreamReached.TrySetResult();
                    return 0;
                }

                if (!_fragments.Reader.TryRead(out _currentFragment))
                {
                    continue;
                }

                _currentOffset = 0;
            }

            int count = Math.Min(buffer.Length, _currentFragment.Length - _currentOffset);
            _currentFragment.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            Interlocked.Add(ref _totalBytesRead, count);
            _firstRead.TrySetResult();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Complete();
            }

            base.Dispose(disposing);
        }
    }
}
