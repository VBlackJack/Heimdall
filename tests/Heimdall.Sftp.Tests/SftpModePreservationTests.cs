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
                "Create",
                "ReadMode",
                "ApplyTempMode:0x180",
                "ReadMode",
                "Open",
                "Write:3",
                "Flush",
                "Dispose",
                "ReadTarget",
                "ApplyPublicationMode:0x1a4",
                "Commit"
            ],
            recorder.Log);
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

        public SftpModePreservation.StagedUploadOperations Operations => new(
            CreateEmptyTemp: () => Log.Add("Create"),
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
            OpenTempForWrite: () =>
            {
                Log.Add("Open");
                return new RecordingStream(this);
            },
            ReadTargetModeAfterUpload: () =>
            {
                Log.Add("ReadTarget");
                return _targetMode;
            },
            ApplyPublicationMode: mode =>
            {
                Log.Add($"ApplyPublicationMode:0x{mode:x}");
                PublishedMode = mode;
            },
            Commit: () => Log.Add("Commit"));

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
