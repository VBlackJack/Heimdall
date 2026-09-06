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

public sealed class SftpBrowserMetadataPreflightWiringTests
{
    private const string Target = "/srv/app/agent";

    /// <remarks>
    /// A server that allows SFTP but refuses exec - ForceCommand internal-sftp, a chrooted
    /// account, a nologin shell - raised a raw SshException out of the preflight, so no file
    /// could be uploaded at all and the user read the generic "transfer failed". It is the
    /// verdict the vocabulary already names, with its own localized refusal.
    /// </remarks>
    [Theory]
    [InlineData(TransportFailureKind.Ssh)]
    [InlineData(TransportFailureKind.Socket)]
    [InlineData(TransportFailureKind.Io)]
    [InlineData(TransportFailureKind.Timeout)]
    public async Task Replacement_IsRefusedAsExecUnavailable_WhenTheExecChannelFails(TransportFailureKind kind)
    {
        Exception failure = kind switch
        {
            TransportFailureKind.Ssh => new Renci.SshNet.Common.SshException("exec refused"),
            TransportFailureKind.Socket => new System.Net.Sockets.SocketException(),
            TransportFailureKind.Io => new IOException("pipe closed"),
            _ => new TimeoutException("exec timed out"),
        };
        ThrowingExecRunner runner = new(failure);
        using SftpBrowser browser = new(runner);

        SftpMetadataPreservationException exception =
            await Assert.ThrowsAsync<SftpMetadataPreservationException>(
                () => browser.EnsureReplacementPreservesMetadataAsync(Target, CancellationToken.None));

        Assert.Equal(SftpMetadataPreflightVerdict.ExecUnavailable, exception.Verdict);
        Assert.Equal("ErrorSftpReplaceRefusedExecUnavailable", exception.MessageKey);
        Assert.Equal(Target, exception.RemotePath);
    }

    public enum TransportFailureKind
    {
        Ssh,
        Socket,
        Io,
        Timeout,
    }

    private sealed class ThrowingExecRunner : ISftpExecCommandRunner
    {
        private readonly Exception _failure;

        public ThrowingExecRunner(Exception failure) => _failure = failure;

        public Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct) => throw _failure;
    }

    [Fact]
    public async Task Replacement_IsRefused_WhenNoTrustedExecChannelIsAvailable()
    {
        // No injected runner, no connection params, no pinned verifier: the question cannot be
        // asked. An unasked question is a refusal, never a pass.
        using SftpBrowser browser = new();

        SftpMetadataPreservationException exception =
            await Assert.ThrowsAsync<SftpMetadataPreservationException>(
                () => browser.EnsureReplacementPreservesMetadataAsync(Target, CancellationToken.None));

        Assert.Equal(SftpMetadataPreflightVerdict.ExecUnavailable, exception.Verdict);

        // Its own reason, not the tooling one: that message states getcap, getfattr or getfacl is
        // missing from the server, which would misdiagnose an unavailable route entirely.
        Assert.Equal("ErrorSftpReplaceRefusedExecUnavailable", exception.MessageKey);
        Assert.NotEqual(
            SftpMetadataPreflight.GetRefusalLocaleKey(SftpMetadataPreflightVerdict.ToolingUnavailable),
            exception.MessageKey);
    }

    [Theory]
    [InlineData(SftpMetadataPreflight.CapabilitiesStatus, SftpMetadataPreflightVerdict.CapabilitiesPresent)]
    [InlineData(SftpMetadataPreflight.SecurityXattrStatus, SftpMetadataPreflightVerdict.SecurityXattrsPresent)]
    [InlineData(SftpMetadataPreflight.AclStatus, SftpMetadataPreflightVerdict.AclPresent)]
    [InlineData(SftpMetadataPreflight.ToolingStatus, SftpMetadataPreflightVerdict.ToolingUnavailable)]
    [InlineData(SftpMetadataPreflight.UnreadableStatus, SftpMetadataPreflightVerdict.MetadataUnreadable)]
    [InlineData(SftpMetadataPreflight.ExtendedAttributeStatus, SftpMetadataPreflightVerdict.ExtendedAttributesPresent)]
    [InlineData(SftpMetadataPreflight.OwnershipStatus, SftpMetadataPreflightVerdict.OwnershipNotReproducible)]
    [InlineData(42, SftpMetadataPreflightVerdict.MetadataUnreadable)]
    public async Task Replacement_IsRefused_ForEveryVerdictThatCannotBeReproduced(
        int exitStatus,
        SftpMetadataPreflightVerdict expected)
    {
        ScriptedExecRunner runner = new(exitStatus);
        using SftpBrowser browser = new(runner);

        SftpMetadataPreservationException exception =
            await Assert.ThrowsAsync<SftpMetadataPreservationException>(
                () => browser.EnsureReplacementPreservesMetadataAsync(Target, CancellationToken.None));

        Assert.Equal(expected, exception.Verdict);
        Assert.Equal(Target, exception.RemotePath);
        Assert.Equal(SftpMetadataPreflight.GetRefusalLocaleKey(expected), exception.MessageKey);

        // The remote shell's own diagnostic is unlocalized and may quote server paths the operator
        // cannot act on, so it must not reach the message the user is shown.
        Assert.DoesNotContain(ScriptedExecRunner.RemoteDiagnostic, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SftpMetadataPreflight.NoExistingTargetStatus)]
    public async Task Replacement_Proceeds_WhenNothingPreventsAnExactRestore(int exitStatus)
    {
        // NoExistingTarget is the ordinary creation case and must stay usable: a preflight that
        // refused creations would break every first upload into a directory.
        ScriptedExecRunner runner = new(exitStatus);
        using SftpBrowser browser = new(runner);

        await browser.EnsureReplacementPreservesMetadataAsync(Target, CancellationToken.None);

        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task SecondPreflight_RefusesWhenTheTargetGainsMetadataDuringTheUpload()
    {
        // The TOCTOU this closes: the first probe clears the destination, the upload takes time,
        // and the destination acquires an ACL before the rename. Publishing on the first verdict
        // would authorise destroying metadata that did not exist when it was taken.
        ScriptedExecRunner runner = new(0, SftpMetadataPreflight.AclStatus);
        using SftpBrowser browser = new(runner);

        await browser.EnsureReplacementPreservesMetadataAsync(Target, CancellationToken.None);

        SftpMetadataPreservationException exception =
            await Assert.ThrowsAsync<SftpMetadataPreservationException>(
                () => browser.EnsureReplacementPreservesMetadataAsync(Target, CancellationToken.None));

        Assert.Equal(SftpMetadataPreflightVerdict.AclPresent, exception.Verdict);
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task Preflight_PropagatesCancellation()
    {
        ScriptedExecRunner runner = new(0);
        using SftpBrowser browser = new(runner);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => browser.EnsureReplacementPreservesMetadataAsync(Target, cts.Token));

        // Cancelled before the channel was used, not after paying for a round trip.
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public void UploadPath_RunsThePreflightBeforeStagingAndAgainBeforeCommit()
    {
        // Source-scoped, because proving "no rename happened" end to end would need a fake
        // SftpClient and SSH.NET exposes a concrete one. What is checked here is that both call
        // sites exist and that the second one precedes the publication it guards.
        string source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Heimdall.Sftp", "SftpBrowser.cs"));

        string uploadMethod = ExtractMethod(source, "private async Task UploadFileAsync(");

        const string call = "EnsureReplacementPreservesMetadataAsync(";
        List<int> callSites = [];
        for (int i = uploadMethod.IndexOf(call, StringComparison.Ordinal);
             i >= 0;
             i = uploadMethod.IndexOf(call, i + call.Length, StringComparison.Ordinal))
        {
            callSites.Add(i);
        }

        // Counted, not searched from an offset. Looking for a "second" occurrence starting just
        // past the first one finds the tail of the first call itself, so removing the commit-time
        // probe left that search satisfied and the mutant survived.
        Assert.Equal(2, callSites.Count);

        int staging = uploadMethod.IndexOf("CreateRemoteTempPath(", StringComparison.Ordinal);
        int commit = uploadMethod.IndexOf("CommitUploadedTemp(", StringComparison.Ordinal);

        Assert.True(staging > callSites[0], "the first preflight must precede the temporary path");
        Assert.True(commit > callSites[1], "the second preflight must precede the commit it guards");
        Assert.True(callSites[1] > staging, "the second preflight must be the commit-time one");

        // Both call sites are gated on a real replacement. An ungated preflight would make every
        // ordinary creation demand an exec channel and refuse without one.
        Assert.Equal(
            2,
            CountOccurrences(uploadMethod, "commitMode == UploadCommitMode.ReplaceExisting"));
    }

    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        for (int i = value.IndexOf(fragment, StringComparison.Ordinal);
             i >= 0;
             i = value.IndexOf(fragment, i + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method signature was not found: {signature}");

        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Method closing brace was not found: {signature}");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    /// <summary>
    /// Returns a scripted exit status per call, so a destination can change between the staging
    /// probe and the commit probe without any timing dependency.
    /// </summary>
    private sealed class ScriptedExecRunner : ISftpExecCommandRunner
    {
        internal const string RemoteDiagnostic = "getfacl: /srv/app/agent: Permission denied";

        private readonly int[] _statuses;

        public ScriptedExecRunner(params int[] statuses) => _statuses = statuses;

        public int CallCount { get; private set; }

        public Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int index = Math.Min(CallCount, _statuses.Length - 1);
            CallCount++;
            return Task.FromResult(new SftpExecResult(_statuses[index], RemoteDiagnostic));
        }
    }
}
