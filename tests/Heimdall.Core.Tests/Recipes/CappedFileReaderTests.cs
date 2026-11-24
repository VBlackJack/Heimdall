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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>A file over its cap is refused after one byte more than the cap has been read, never read whole.</summary>
public sealed class CappedFileReaderTests : IDisposable
{
    private const int Cap = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "heimdall-capped-reader-tests", Guid.NewGuid().ToString("N"));

    public CappedFileReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temporary folder.
        }
    }

    /// <summary>An endless stream that counts what was asked of it.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public bool Seekable { get; init; }

        public long ReportedLength { get; init; } = 10;

        public override bool CanRead => true;

        public override bool CanSeek => Seekable;

        public override bool CanWrite => false;

        public override long Length => Seekable ? ReportedLength : throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void AStreamOfManyBytes_IsRefusedAfterReadingAtMostOneByteMoreThanTheCap()
    {
        EndlessStream stream = new();

        CappedRead read = CappedFileReader.Read(stream, Cap);

        Assert.True(read.TooLarge);
        Assert.Null(read.Bytes);
        Assert.True(stream.BytesRead <= Cap + 1, $"read {stream.BytesRead}");
        Assert.Equal(Cap + 1, read.Length);
    }

    [Fact]
    public void AHandleThatReportsItsLength_GivesThatLengthWhenTheFileIsTooLarge()
    {
        EndlessStream stream = new() { Seekable = true, ReportedLength = 5_000_000 };

        CappedRead read = CappedFileReader.Read(stream, Cap);

        Assert.True(read.TooLarge);
        Assert.Equal(5_000_000, read.Length);
        Assert.True(stream.BytesRead <= Cap + 1);
    }

    [Fact]
    public void AFileThatReportsASmallLengthButHoldsMore_IsStillRefused()
    {
        // The length said at open time and the content that follows do not have to agree: only the read counts.
        EndlessStream stream = new() { Seekable = true, ReportedLength = 10 };

        CappedRead read = CappedFileReader.Read(stream, Cap);

        Assert.True(read.TooLarge);
        Assert.True(stream.BytesRead <= Cap + 1);
    }

    [Fact]
    public void AFileOfExactlyTheCap_IsReadWhole_AndOneByteMoreIsTooLarge()
    {
        string exact = Path.Combine(_root, "exact.bin");
        string over = Path.Combine(_root, "over.bin");
        File.WriteAllBytes(exact, new byte[Cap]);
        File.WriteAllBytes(over, new byte[Cap + 1]);

        CappedRead whole = CappedFileReader.Read(exact, Cap);
        CappedRead refused = CappedFileReader.Read(over, Cap);

        Assert.False(whole.TooLarge);
        Assert.Equal(Cap, whole.Bytes!.Length);
        Assert.True(refused.TooLarge);
        Assert.Equal(Cap + 1, refused.Length);
    }

    [Fact]
    public void AnEmptyFile_IsReadAsNoBytes()
    {
        string path = Path.Combine(_root, "empty.bin");
        File.WriteAllBytes(path, []);

        Assert.Empty(CappedFileReader.Read(path, Cap).Bytes!);
    }

    [Fact]
    public void AFileThatGrowsBetweenOpenAndRead_IsRefused_BecauseOnlyWhatIsReadCounts()
    {
        string path = Path.Combine(_root, "grows.bin");
        File.WriteAllBytes(path, new byte[10]);
        using FileStream opened = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal(10, opened.Length);

        using (FileStream appender = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            appender.Write(new byte[Cap * 5]);
        }

        CappedRead read = CappedFileReader.Read(opened, Cap);

        Assert.True(read.TooLarge);
        Assert.Equal(Cap * 5 + 10, read.Length);
    }

    [Fact]
    public void AFileWhoseWriterIsStillOpen_CannotBeOpenedForTheRead()
    {
        string path = Path.Combine(_root, "busy.bin");
        File.WriteAllBytes(path, new byte[10]);
        using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.ThrowsAny<IOException>(() => CappedFileReader.Read(path, Cap));
    }

    [Fact]
    public void AMissingFile_ThrowsAnIoException_ForTheCallerToTurnIntoItsOwnOutcome()
        => Assert.ThrowsAny<IOException>(() => CappedFileReader.Read(Path.Combine(_root, "missing.bin"), Cap));
}
