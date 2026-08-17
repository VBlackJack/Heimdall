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

using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

public sealed class SftpModePreservationTests
{
    // --- Staged upload: the temporary is private while it holds content -------------------------
    // SftpClient.UploadFile created the temporary AND filled it in one call, so its content sat
    // under the server's default mode - typically world-readable - for the whole copy. These
    // oracles pin the order that closes that window; order is the whole contract here, so the fake
    // records every operation rather than only its outcome.

    // The staging reservation is the whole no-clobber guarantee, so the mode the orchestration
    // actually requests is asserted, not the constant it is supposed to come from. A test that reads
    // the constant proves the declaration and stays green when the real call site is changed.
    [Fact]
    public void RunStagedUpload_OpensTheStagingPathExclusively_WithTheModeItActuallyRequests()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null);

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1, 2, 3]),
            recorder.Progress.Add,
            CancellationToken.None);

        Assert.Equal(FileMode.CreateNew, recorder.RequestedStagingFileMode);
        Assert.Equal(FileAccess.Write, recorder.RequestedStagingFileAccess);
    }

    // Exactly one open: a create-then-reopen pair would discard the handle that proved exclusivity
    // and re-resolve the path by name, so the reservation would say nothing about what the writes
    // landed in.
    [Fact]
    public void RunStagedUpload_ReservesTheStagingPathExactlyOnce()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null);

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1, 2, 3]),
            recorder.Progress.Add,
            CancellationToken.None);

        Assert.Single(recorder.Log, entry => entry.StartsWith("OpenExclusive:", StringComparison.Ordinal));
    }

    // A staging path another party already created must stop the upload before any byte is written.
    [Fact]
    public void RunStagedUpload_StagingPathAlreadyExists_WritesNothingAndPropagates()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null)
        {
            StagingPathAlreadyExists = true,
        };

        Assert.Throws<IOException>(() => SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1, 2, 3]),
            recorder.Progress.Add,
            CancellationToken.None));

        Assert.Equal(0, recorder.BytesWritten);
        Assert.DoesNotContain("Commit", recorder.Log);
    }

    [Fact]
    public void RunStagedUpload_PerformsItsOperationsInTheContractedOrder()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null);

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1, 2, 3]),
            recorder.Progress.Add,
            CancellationToken.None);

        Assert.Equal(
            [
                // The exclusive reservation comes first and its handle is held for the whole upload,
                // so the mode work happens on a file this upload provably created. The tightening and
                // its read-back still precede the first byte.
                "OpenExclusive:CreateNew:Write",
                "ReadMode",
                "ApplyTempMode:0x180",
                "ReadMode",
                "Write:3",
                "Flush",
                "Dispose",
                "ReadTarget",
                "ApplyPublicationMode:0x1a4",

                // Creation: no destination existed, so no timestamps are inherited. Dating a new
                // file from a target that was never there would be invention, not preservation.
                "ApplyStamps:a=none,w=none",
                "Commit"
            ],
            recorder.Log);
    }

    [Fact]
    public void RunStagedUpload_Replacement_AppliesTheTargetsModeAndTimestampsBeforeCommit()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: 0x1ED)
        {
            // Deliberately different from each other, so swapping them is visible. Equal stamps
            // would make an atime/mtime inversion undetectable.
            TargetAccessTimeUtc = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            TargetWriteTimeUtc = new DateTime(2022, 8, 9, 10, 11, 12, DateTimeKind.Utc),
        };

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1, 2, 3]),
            recorder.Progress.Add,
            CancellationToken.None);

        Assert.Equal(0x1EDu, recorder.PublishedMode);
        Assert.Equal(recorder.TargetAccessTimeUtc, recorder.PublishedAccessTimeUtc);
        Assert.Equal(recorder.TargetWriteTimeUtc, recorder.PublishedWriteTimeUtc);

        // Order is the contract: the stream is flushed and closed, the destination is read, the
        // attributes are applied, they are read back, and only then is the file published.
        Assert.Equal(
            [
                "Flush",
                "Dispose",
                "ReadTarget",
                "ApplyPublicationMode:0x1ed",
                "ApplyStamps:a=2021-03-04T05:06:07.0000000Z,w=2022-08-09T10:11:12.0000000Z",
                "ReadBack",
                "Commit"
            ],
            recorder.Log.SkipWhile(entry => entry != "Flush").ToArray());
    }

    [Fact]
    public void RunStagedUpload_Replacement_RefusesTheCommitWhenTheReadBackDisagrees()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: 0x1ED)
        {
            // A server that silently ignores the timestamp write: it accepts the call and leaves
            // the file dated now. Reporting success there would claim a preservation that did not
            // happen, which is the whole reason the read-back exists.
            ReadBackWriteTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        IOException exception = Assert.Throws<IOException>(() =>
            SftpModePreservation.RunStagedUpload(
                recorder.Operations,
                new MemoryStream([1, 2, 3]),
                recorder.Progress.Add,
                CancellationToken.None));

        Assert.Contains("would not preserve", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Commit", recorder.Log);
    }

    [Fact]
    public void RunStagedUpload_Replacement_RefusesTheCommitWhenTheAccessTimeAloneDisagrees()
    {
        // Checked separately from the write time: a read-back oracle that only compared mtime
        // would accept an atime that was never applied.
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: 0x1ED)
        {
            ReadBackAccessTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        Assert.Throws<IOException>(() =>
            SftpModePreservation.RunStagedUpload(
                recorder.Operations,
                new MemoryStream([1, 2, 3]),
                recorder.Progress.Add,
                CancellationToken.None));

        Assert.DoesNotContain("Commit", recorder.Log);
    }

    [Fact]
    public void RunStagedUpload_Replacement_PropagatesAnApplyFailureAndDoesNotCommit()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: 0x1ED)
        {
            ApplyPublicationThrows = true,
        };

        Assert.Throws<IOException>(() =>
            SftpModePreservation.RunStagedUpload(
                recorder.Operations,
                new MemoryStream([1, 2, 3]),
                recorder.Progress.Add,
                CancellationToken.None));

        Assert.DoesNotContain("Commit", recorder.Log);
    }

    [Fact]
    public void RunStagedUpload_Creation_InheritsNoTimestamps()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null);

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1, 2, 3]),
            recorder.Progress.Add,
            CancellationToken.None);

        // No destination existed, so there is nothing to restore and nothing to verify.
        Assert.Null(recorder.PublishedAccessTimeUtc);
        Assert.Null(recorder.PublishedWriteTimeUtc);
        Assert.DoesNotContain("ReadBack", recorder.Log);
        Assert.Contains("Commit", recorder.Log);
    }

    [Fact]
    public void RunStagedUpload_StagingChmodFails_WritesNothingAndDoesNotCommit()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null)
        {
            ApplyTempModeThrows = true
        };

        IOException failure = Assert.Throws<IOException>(() => SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1, 2, 3]),
            recorder.Progress.Add,
            CancellationToken.None));

        // The chmod's own failure must reach the caller. Swallowing it and letting the read-back
        // refuse instead would still stop the upload, but the operator would be told the mode came
        // back wrong rather than why setting it failed - a symptom in place of the cause.
        Assert.Equal("chmod refused", failure.Message);
        Assert.Equal(0, recorder.BytesWritten);
        Assert.DoesNotContain("Open", recorder.Log);
        Assert.DoesNotContain("Commit", recorder.Log);
    }

    [Fact]
    public void RunStagedUpload_StagingModeReadsBackWrong_WritesNothingAndDoesNotCommit()
    {
        // A server that accepts the chmod and ignores it must stop the upload, not receive the
        // content anyway. Asserting on the read-back rather than on the call is the whole point.
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null)
        {
            ModeAfterStagingChmod = 0x1A4
        };

        IOException failure = Assert.Throws<IOException>(() => SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1, 2, 3]),
            recorder.Progress.Add,
            CancellationToken.None));

        Assert.Contains("private", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, recorder.BytesWritten);
        Assert.DoesNotContain("Open", recorder.Log);
        Assert.DoesNotContain("Commit", recorder.Log);
    }

    [Fact]
    public void RunStagedUpload_NewFile_PublishesTheModeTheServerAssigned()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null);

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1]),
            recorder.Progress.Add,
            CancellationToken.None);

        // 0644, not the 0600 it was staged with: staging must not quietly make every new upload
        // owner-only.
        Assert.Equal(0x1A4u, recorder.PublishedMode);
        Assert.NotEqual(SftpModePreservation.StagingMode, recorder.PublishedMode);
    }

    [Fact]
    public void RunStagedUpload_Replacement_PublishesTheTargetModeIncludingSpecialBits()
    {
        // 04755: setuid, setgid off, sticky off, rwxr-xr-x - the bits a plain 9-bit copy loses.
        const uint TargetMode = 0x9EDu;
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: TargetMode);

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1]),
            recorder.Progress.Add,
            CancellationToken.None);

        Assert.Equal(TargetMode, recorder.PublishedMode);
    }

    [Fact]
    public void RunStagedUpload_CancelledMidCopy_StopsWithoutCommitting()
    {
        using CancellationTokenSource cts = new();
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null)
        {
            OnWrite = _ => cts.Cancel()
        };

        Assert.ThrowsAny<OperationCanceledException>(() => SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream(new byte[200_000]),
            recorder.Progress.Add,
            cts.Token));

        Assert.DoesNotContain("Commit", recorder.Log);
        Assert.DoesNotContain("ApplyPublicationMode", string.Join('|', recorder.Log));
    }

    [Fact]
    public void RunStagedUpload_Progress_IsCumulativeAndNeverAheadOfWhatWasWritten()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: null);

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream(new byte[200_000]),
            recorder.Progress.Add,
            CancellationToken.None);

        Assert.NotEmpty(recorder.Progress);
        Assert.Equal(recorder.Progress.OrderBy(value => value), recorder.Progress);
        Assert.Equal(200_000, recorder.Progress[^1]);
        Assert.All(recorder.Progress, value => Assert.True(value <= recorder.BytesWritten));
    }

    [Fact]
    public void RunStagedUpload_ClosesTheRemoteStream_BeforeTheFinalModeAndTheCommit()
    {
        StagedUploadRecorder recorder = new(serverAssignedMode: 0x1A4, targetMode: 0x1A0);

        SftpModePreservation.RunStagedUpload(
            recorder.Operations,
            new MemoryStream([1]),
            recorder.Progress.Add,
            CancellationToken.None);

        int dispose = recorder.Log.IndexOf("Dispose");
        int publication = recorder.Log.FindIndex(entry => entry.StartsWith("ApplyPublicationMode", StringComparison.Ordinal));
        int commit = recorder.Log.IndexOf("Commit");

        // Nothing may be published while a write could still be buffered.
        Assert.True(dispose >= 0 && publication > dispose, "The stream was not closed before the final mode.");
        Assert.True(commit > publication, "The commit did not follow the final mode.");
    }

    [Fact]
    public void SftpBrowser_UsesTheStagedUpload_AndNoLongerCallsUploadFile()
    {
        // The oracle above tests an orchestration; this one proves it is the wiring SftpBrowser
        // actually runs, so the policy cannot drift into being correct but unused.
        string source = ReadSftpSource("SftpBrowser.cs");

        Assert.Contains("SftpModePreservation.RunStagedUpload(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("client.UploadFile(", source, StringComparison.Ordinal);
    }

    // The recorder oracles prove what the orchestration REQUESTS. They cannot see what the wiring
    // lambda does with that request: hardcoding a mode inside the lambda leaves every recorder oracle
    // green. This oracle is therefore bounded to the lambda itself, because a global assertion over
    // SftpBrowser.cs would let a second, correct client.Open elsewhere mask the dangerous one.
    [Fact]
    public void SftpBrowser_StagingWiring_ForwardsTheRequestedModeAndHardcodesNothing()
    {
        string lambda = ExtractStagingOpenLambda();

        // Exactly one open, and it opens the staging path with the mode and access it was handed.
        Assert.Equal(1, CountOccurrences(lambda, "client.Open("));
        Assert.Contains("client.Open(tempRemotePath, mode, access)", lambda, StringComparison.Ordinal);

        // No mode may be named inside the lambda: naming one is how the forwarded value gets
        // discarded while the constant and the orchestration still read correctly.
        Assert.DoesNotContain("FileMode.", lambda, StringComparison.Ordinal);

        // And no reopen or truncating create may return through this seam.
        Assert.DoesNotContain("client.OpenWrite(", lambda, StringComparison.Ordinal);
        Assert.DoesNotContain("client.Create(", lambda, StringComparison.Ordinal);
    }

    // Ownership is what allows a cleanup by name. It must be acquired from the open having returned,
    // never asserted in advance, and the rollback must be unreachable without it.
    [Fact]
    public void SftpBrowser_StagingOwnership_IsAcquiredOnlyAfterTheOpenReturns()
    {
        string lambda = ExtractStagingOpenLambda();
        int openIndex = lambda.IndexOf("client.Open(", StringComparison.Ordinal);
        int ownedIndex = lambda.IndexOf("stagingOwned = true", StringComparison.Ordinal);

        Assert.True(openIndex >= 0, "the staging open must remain in the wiring lambda");
        Assert.True(ownedIndex >= 0, "ownership must be recorded in the wiring lambda");
        Assert.True(
            openIndex < ownedIndex,
            "ownership must be recorded only after the exclusive open has returned");

        // The lambda must not be able to claim ownership without having opened: the open's result is
        // assigned first, then ownership, then that result is returned.
        Assert.Contains("Stream stagingStream = client.Open(", lambda, StringComparison.Ordinal);

        string upload = ExtractPrivateUploadFileAsync();
        int guardIndex = upload.IndexOf("if (stagingOwned)", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "the rollback must stay gated on ownership");

        // Nesting, not ordering. An index comparison only shows the guard's text comes first, which
        // an empty guard followed by an unguarded rollback would also satisfy. The cleanup has to be
        // inside the guard's own block, so the block is extracted and searched.
        string guardedBlock = ExtractBracedBlock(upload, guardIndex);
        Assert.Contains("SftpAtomicUpload.Rollback(", guardedBlock, StringComparison.Ordinal);
        Assert.Contains("client.DeleteFile(", guardedBlock, StringComparison.Ordinal);

        // And there must be no second, unguarded copy of either outside that block.
        Assert.Equal(1, CountOccurrences(upload, "SftpAtomicUpload.Rollback("));
        Assert.Equal(1, CountOccurrences(upload, "client.DeleteFile("));
    }

    /// <summary>
    /// Returns the body of the private staged <c>UploadFileAsync</c> overload, so an assertion cannot
    /// be satisfied by an unrelated part of the file.
    /// </summary>
    private static string ExtractPrivateUploadFileAsync()
    {
        string source = ReadSftpSource("SftpBrowser.cs");
        const string Signature = "private async Task UploadFileAsync(";
        int start = source.IndexOf(Signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "the private staged UploadFileAsync overload was not found");

        return ExtractBracedBlock(source, start);
    }

    /// <summary>
    /// Returns just the <c>OpenTempExclusive</c> lambda from the staged upload wiring.
    /// </summary>
    private static string ExtractStagingOpenLambda()
    {
        string upload = ExtractPrivateUploadFileAsync();
        const string Marker = "OpenTempExclusive:";
        int start = upload.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the OpenTempExclusive wiring was not found in UploadFileAsync");

        return ExtractBracedBlock(upload, start);
    }

    private static string ExtractBracedBlock(string source, int start)
    {
        int depth = 0;
        bool opened = false;
        for (int index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                opened = true;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (opened && depth == 0)
                {
                    return source[start..(index + 1)];
                }
            }
        }

        Assert.Fail("unbalanced braces while extracting the block");
        return string.Empty;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static string ReadSftpSource(string fileName)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Heimdall.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!, "src", "Heimdall.Sftp", fileName));
    }

    /// <summary>
    /// Records what the staged upload asks of the remote file system, in order.
    /// </summary>
    private sealed class StagedUploadRecorder
    {
        private readonly uint _serverAssignedMode;
        private readonly uint? _targetMode;
        private uint _currentTempMode;
        private bool _stagingChmodApplied;

        public StagedUploadRecorder(uint serverAssignedMode, uint? targetMode)
        {
            _serverAssignedMode = serverAssignedMode;
            _targetMode = targetMode;
            _currentTempMode = serverAssignedMode;
        }

        public List<string> Log { get; } = [];

        public List<long> Progress { get; } = [];

        public long BytesWritten { get; private set; }

        public uint? PublishedMode { get; private set; }

        public bool ApplyTempModeThrows { get; init; }

        /// <summary>When set, the mode read back after the staging chmod, whatever was requested.</summary>
        public uint? ModeAfterStagingChmod { get; init; }

        public Action<int>? OnWrite { get; init; }

        /// <summary>The file mode the orchestration actually asked the staging open for.</summary>
        public FileMode? RequestedStagingFileMode { get; private set; }

        /// <summary>The file access the orchestration actually asked the staging open for.</summary>
        public FileAccess? RequestedStagingFileAccess { get; private set; }

        /// <summary>
        /// When set, the staging open throws as a server would when the path already exists.
        /// </summary>
        public bool StagingPathAlreadyExists { get; init; }

        public SftpModePreservation.StagedUploadOperations Operations => new(
            OpenTempExclusive: (mode, access) =>
            {
                RequestedStagingFileMode = mode;
                RequestedStagingFileAccess = access;
                Log.Add($"OpenExclusive:{mode}:{access}");

                // A server honouring CREAT|EXCL refuses the open outright; SSH.NET surfaces that as a
                // plain SftpException, so the fake reproduces the shape rather than a bespoke type.
                if (StagingPathAlreadyExists)
                {
                    throw new IOException("staging path already exists");
                }

                return new RecordingStream(this);
            },
            ReadTempMode: () =>
            {
                Log.Add("ReadMode");
                return _stagingChmodApplied ? ModeAfterStagingChmod ?? _currentTempMode : _serverAssignedMode;
            },
            ApplyTempMode: mode =>
            {
                Log.Add($"ApplyTempMode:0x{mode:x}");
                if (ApplyTempModeThrows)
                {
                    throw new IOException("chmod refused");
                }

                _currentTempMode = mode;
                _stagingChmodApplied = true;
            },
            ReadTargetAttributesAfterUpload: () =>
            {
                Log.Add("ReadTarget");
                return _targetMode is { } mode
                    ? new SftpModePreservation.SftpPublicationAttributes(
                        mode,
                        TargetAccessTimeUtc,
                        TargetWriteTimeUtc)
                    : null;
            },
            ApplyPublicationAttributes: desired =>
            {
                Log.Add($"ApplyPublicationMode:0x{desired.Mode:x}");
                Log.Add($"ApplyStamps:a={Stamp(desired.LastAccessTimeUtc)},w={Stamp(desired.LastWriteTimeUtc)}");
                PublishedMode = desired.Mode;
                PublishedAccessTimeUtc = desired.LastAccessTimeUtc;
                PublishedWriteTimeUtc = desired.LastWriteTimeUtc;
                if (ApplyPublicationThrows)
                {
                    throw new IOException("utimes refused");
                }
            },
            ReadTempAttributesAfterApply: () =>
            {
                Log.Add("ReadBack");
                return new SftpModePreservation.SftpPublicationAttributes(
                    ReadBackMode ?? PublishedMode ?? 0,
                    ReadBackAccessTimeUtc ?? PublishedAccessTimeUtc,
                    ReadBackWriteTimeUtc ?? PublishedWriteTimeUtc);
            },
            Commit: () => Log.Add("Commit"));

        private static string Stamp(DateTime? value) =>
            value is { } v ? v.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : "none";

        public DateTime TargetAccessTimeUtc { get; set; } = new(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        public DateTime TargetWriteTimeUtc { get; set; } = new(2022, 8, 9, 10, 11, 12, DateTimeKind.Utc);

        public bool ApplyPublicationThrows { get; set; }

        public uint? ReadBackMode { get; set; }

        public DateTime? ReadBackAccessTimeUtc { get; set; }

        public DateTime? ReadBackWriteTimeUtc { get; set; }

        public DateTime? PublishedAccessTimeUtc { get; private set; }

        public DateTime? PublishedWriteTimeUtc { get; private set; }

        private sealed class RecordingStream(StagedUploadRecorder owner) : Stream
        {
            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => owner.BytesWritten;

            public override long Position
            {
                get => owner.BytesWritten;
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                owner.Log.Add($"Write:{count}");
                owner.BytesWritten += count;
                owner.OnWrite?.Invoke(count);
            }

            public override void Flush() => owner.Log.Add("Flush");

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                owner.Log.Add("Dispose");
                base.Dispose(disposing);
            }
        }
    }

    [Fact]
    public void ResolveModeToApply_ReturnsTargetMode_WhenModesDiffer()
    {
        uint? modeToApply = SftpModePreservation.ResolveModeToApply(
            targetPermissions: 0x81A0,
            tempPermissions: 0x81A4);

        Assert.Equal((uint)0x1A0, modeToApply);
    }

    [Fact]
    public void ResolveModeToApply_ReturnsNull_WhenModesMatch()
    {
        uint? modeToApply = SftpModePreservation.ResolveModeToApply(
            targetPermissions: 0x81A4,
            tempPermissions: 0x81A4);

        Assert.Null(modeToApply);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_ReturnsTrue_WhenTempIsMoreRestrictive()
    {
        bool shouldRefuse = SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
            targetPermissions: 0x1ED,
            tempPermissions: 0x180);

        Assert.True(shouldRefuse);
    }

    [Fact]
    public void ApplyUploadModeBeforeCommit_RefusesCommitAndLeavesExistingFinalUntouched_WhenApplyFails()
    {
        const string FinalRemotePath = "/srv/app.sh";
        const string TempRemotePath = "/srv/.app.sh.heimdall.part";
        Dictionary<string, string> remoteFiles = new(StringComparer.Ordinal)
        {
            [FinalRemotePath] = "old-content",
            [TempRemotePath] = "new-content",
        };
        bool commitCalled = false;

        Assert.Throws<InvalidOperationException>(() =>
        {
            SftpBrowser.ApplyUploadModeBeforeCommit(
                FinalRemotePath,
                targetPermissions: 0x1ED,
                tempPermissions: 0x180,
                applyMode: _ => throw new IOException("SetAttributes refused."));
            commitCalled = true;
            SftpAtomicUpload.CommitRename(
                TempRemotePath,
                FinalRemotePath,
                atomicRename: (source, destination) =>
                {
                    remoteFiles[destination] = remoteFiles[source];
                    remoteFiles.Remove(source);
                },
                plainRename: (_, _) => throw new InvalidOperationException("Fallback was not expected."),
                remoteExists: remoteFiles.ContainsKey);
        });

        Assert.False(commitCalled);
        Assert.Equal("old-content", remoteFiles[FinalRemotePath]);
        Assert.Equal("new-content", remoteFiles[TempRemotePath]);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_ReturnsFalse_WhenModesAreEqual()
    {
        bool shouldRefuse = SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
            targetPermissions: 0x1A0,
            tempPermissions: 0x1A0);

        Assert.False(shouldRefuse);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_ReturnsTrue_WhenTempAddsWorldRead()
    {
        bool shouldRefuse = SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
            targetPermissions: 0x180,
            tempPermissions: 0x184);

        Assert.True(shouldRefuse);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_ReturnsTrue_WhenTempAddsGroupRead()
    {
        bool shouldRefuse = SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(
            targetPermissions: 0x180,
            tempPermissions: 0x1A0);

        Assert.True(shouldRefuse);
    }

    [Fact]
    public void ShouldRefuseCommitAfterApplyFailure_IncludesSetUserSetGroupAndStickyBits()
    {
        Assert.True(SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(0x180, 0x980));
        Assert.True(SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(0x180, 0x580));
        Assert.True(SftpModePreservation.ShouldRefuseCommitAfterApplyFailure(0x180, 0x380));
    }
}
