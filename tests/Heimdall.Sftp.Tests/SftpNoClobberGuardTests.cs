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

using System.Reflection;
using Heimdall.Ssh;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Pins the guard that keeps the commit mode and the publish runner from disagreeing, the wording of
/// the refusals, and the telemetry emitted when a no-clobber step fails.
/// </summary>
/// <remarks>
/// The guard exists for a mutation, not for a caller: no public entry point can produce a mismatched
/// pair today. What it defends against is a future edit, or a single-token change, that would leave a
/// replacing commit reachable from the no-clobber entry point. So it is exercised directly on the
/// private core, and the discriminator is which exception comes out.
/// <para>
/// A disconnected browser and a local path that does not exist are what make these oracles
/// non-vacuous. The guard sits ahead of the local file access, the temporary path, the lock and every
/// remote call, so a guard that stopped working would not return the same exception: the local
/// lookup would throw about a missing file instead. Nothing here contacts a server.
/// </para>
/// </remarks>
public sealed class SftpNoClobberGuardTests
{
    private const string MissingLocalFileMarker = "no-clobber-guard-absent-local-file.bin";

    // Named, not typed: the commit mode is private and is resolved by reflection from the upload core's
    // own parameter, so these are the enum member names and nothing more.
    private const string PublishingMode = "PublishByExclusiveLink";
    private const string ReplacingMode = "ReplaceExisting";
    private const int UploadCoreParameterCount = 5;
    private const int UploadCoreCommitModeParameterIndex = 2;
    private const string HostKeyCatch = "catch (HostKeyRejectedException hostKeyRejected)";
    private const string TransportCatch = "catch (Exception ex) when (ex is Renci.SshNet.Common.SshException";
    private const string ProbeCatch = "catch (Exception probeFailure) when (probeFailure is";

    [Fact]
    public async Task PrivateUpload_PublishingModeWithoutARunner_RefusesBeforeAnythingIsTouched()
    {
        using SftpBrowser browser = new();

        RemoteNoClobberPublishUnavailableException refusal =
            await Assert.ThrowsAsync<RemoteNoClobberPublishUnavailableException>(
                () => InvokePrivateUploadAsync(
                    browser,
                    MissingLocalPath(),
                    "/srv/data/file.txt",
                    PublishingMode,
                    runner: null));

        Assert.Contains(
            "no publish runner was supplied to the no-clobber upload",
            refusal.Message,
            StringComparison.Ordinal);
        Assert.Equal("/srv/data/file.txt", refusal.RemotePath);
    }

    // The pair the mandatory mutant produces: the public entry point still resolves a runner, but the
    // mode it asks for was changed to the replacing one. Without the second direction of the
    // equivalence this pair is accepted, the metadata preflight runs, and the upload commits by
    // replacing the destination. With it, the call refuses and the runner is never used.
    [Fact]
    public async Task PrivateUpload_ReplacingModeWithARunner_RefusesAndNeverRunsTheRunner()
    {
        using SftpBrowser browser = new();
        CountingExecRunner runner = new();

        RemoteNoClobberPublishUnavailableException refusal =
            await Assert.ThrowsAsync<RemoteNoClobberPublishUnavailableException>(
                () => InvokePrivateUploadAsync(
                    browser,
                    MissingLocalPath(),
                    "/srv/data/file.txt",
                    ReplacingMode,
                    runner));

        Assert.Contains(
            "a publish runner was supplied to a replacing upload",
            refusal.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, runner.Invocations);
    }

    // Both valid pairs must get past the guard. Otherwise a guard that refused everything would pass
    // the two oracles above while making the whole feature, and every ordinary upload, dead.
    // The parameter is a bool because the mode is a private nested enum the test never names.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrivateUpload_AgreeingPair_IsNotRefusedByTheGuard(bool publishesByLink)
    {
        using SftpBrowser browser = new();

        Exception thrown = await Record.ExceptionAsync(
            () => InvokePrivateUploadAsync(
                browser,
                MissingLocalPath(),
                "/srv/data/file.txt",
                publishesByLink ? PublishingMode : ReplacingMode,
                publishesByLink ? new CountingExecRunner() : null));

        // It still fails, because the local file is absent and no session is connected. What matters
        // is that it is no longer the guard refusing: the call reached the local file access.
        Assert.NotNull(thrown);
        Assert.IsNotType<RemoteNoClobberPublishUnavailableException>(thrown);
    }

    [Fact]
    public void PrivateUpload_GuardIsAnEquivalence_AndPrecedesEveryAccess()
    {
        string upload = ExtractPrivateUploadFileAsync();

        int guardIndex = upload.IndexOf(
            "if (publishesByLink != (noClobberRunner is not null))",
            StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "the guard must compare the two in both directions");

        // The one-directional form must be gone, not merely supplemented: it is what let a replacing
        // mode carry a publish runner through.
        Assert.DoesNotContain(
            "commitMode == UploadCommitMode.PublishByExclusiveLink && noClobberRunner is null",
            upload,
            StringComparison.Ordinal);

        int localAccessIndex = upload.IndexOf(
            "LocalUploadSource.GetRequiredRegularFile(",
            StringComparison.Ordinal);
        int tempPathIndex = upload.IndexOf("CreateRemoteTempPath(", StringComparison.Ordinal);
        int lockIndex = upload.IndexOf("_clientLock.WaitAsync(", StringComparison.Ordinal);
        int openIndex = upload.IndexOf("OpenTempExclusive:", StringComparison.Ordinal);
        int preflightIndex = upload.IndexOf(
            "EnsureReplacementPreservesMetadataAsync(",
            StringComparison.Ordinal);

        Assert.True(guardIndex < localAccessIndex, "the guard must precede the local file access");
        Assert.True(guardIndex < preflightIndex, "the guard must precede the metadata preflight");
        Assert.True(guardIndex < tempPathIndex, "the guard must precede the staging path choice");
        Assert.True(guardIndex < lockIndex, "the guard must precede the client lock");
        Assert.True(guardIndex < openIndex, "the guard must precede the staging open");
    }

    // The commit is where the guarantee is either kept or thrown away. A publishing upload that
    // reaches the renaming commit replaces the destination silently, which is the original defect
    // wearing the new API's name, and every other oracle in this lot stays green while it does.
    [Fact]
    public void PublishingCommit_LinksExclusively_AndNeverReachesTheRenamingCommit()
    {
        string upload = ExtractPrivateUploadFileAsync();

        int commitIndex = upload.IndexOf("Commit:", StringComparison.Ordinal);
        Assert.True(commitIndex >= 0, "the staged upload must wire a commit");

        int branchIndex = upload.IndexOf(
            "if (commitMode == UploadCommitMode.PublishByExclusiveLink)",
            commitIndex,
            StringComparison.Ordinal);
        Assert.True(branchIndex >= 0, "the commit must branch on the publishing mode");

        string publishBranch = ExtractBracedBlock(upload, branchIndex);

        Assert.Contains("PublishByExclusiveLink(", publishBranch, StringComparison.Ordinal);

        // The renaming commit and the rename itself must be unreachable from this branch. A rename
        // onto an existing name is not specified to fail, and on most servers it does not.
        Assert.DoesNotContain("CommitUploadedTemp(", publishBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameFile(", publishBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveTo(", publishBranch, StringComparison.Ordinal);

        // And the branch must leave, so control cannot fall through into the replacing commit that
        // follows it.
        Assert.Contains("return;", publishBranch, StringComparison.Ordinal);
    }

    // Naming a method PublishByExclusiveLink does not make it one. The branch oracle above only proves
    // which method is called, so a replacing commit inserted INSIDE that method leaves the call site,
    // the command builder and all their oracles untouched while the destination is overwritten. This
    // reads the helper's own body.
    [Fact]
    public void PublishByExclusiveLinkBody_RunsOnlyTheLinkCommand()
    {
        string helper = ExtractMethodBody(
            ReadSftpBrowserSource(),
            "private async Task PublishByExclusiveLink(");

        // The command is built from the staging path and the destination, once, by the builder whose
        // escaping and primitive choice are pinned elsewhere.
        Assert.Equal(1, CountOccurrences(helper, "ServerSideNoClobberPublishCommand.BuildFilePublish("));
        // Whitespace-insensitive: what must hold is which builder is called and with which arguments,
        // not how the call happens to be wrapped. Pinning the layout would fail a test about the
        // primitive because somebody reformatted a line.
        Assert.Contains(
            "ServerSideNoClobberPublishCommand.BuildFilePublish(stagingRemotePath,remotePath);",
            CollapseWhitespace(helper),
            StringComparison.Ordinal);

        // One execution, of that command, on the linked token: a second execution or a different
        // command would be a mutation of the destination this method does not account for.
        Assert.Equal(1, CountOccurrences(helper, "runner.ExecuteAsync("));
        Assert.Contains("runner.ExecuteAsync(command, commandCts.Token)", helper, StringComparison.Ordinal);

        // The runner is the one resolved before staging. Resolving a fresh one here would reopen the
        // window the pre-resolution exists to close.
        Assert.Equal(0, CountOccurrences(helper, "ResolveNoClobberExecRunner("));

        // No replacing commit, in any form, anywhere in the body.
        Assert.Equal(0, CountOccurrences(helper, "CommitUploadedTemp("));
        Assert.Equal(0, CountOccurrences(helper, "RenameFile("));
        Assert.Equal(0, CountOccurrences(helper, "MoveTo("));
    }

    // Kills the mandatory single-token mutant at its source: the public no-clobber entry point must
    // ask for the publishing mode. The guard above turns that mutation into a refusal; this makes it
    // a failing test rather than a silent behaviour change.
    [Fact]
    public void PublishFileIfAbsentAsync_RequestsThePublishingMode_AndSuppliesTheResolvedRunner()
    {
        string source = ReadSftpBrowserSource();
        int entryIndex = source.IndexOf(
            "public async Task PublishFileIfAbsentAsync(",
            StringComparison.Ordinal);
        Assert.True(entryIndex >= 0, "the public no-clobber entry point must exist");

        string body = source[entryIndex..source.IndexOf(
            "public async Task CreateDirectoryExclusiveAsync(",
            entryIndex,
            StringComparison.Ordinal)];

        Assert.Contains("UploadCommitMode.PublishByExclusiveLink", body, StringComparison.Ordinal);
        Assert.DoesNotContain("UploadCommitMode.ReplaceExisting", body, StringComparison.Ordinal);
        Assert.Contains("ResolveNoClobberExecRunner(", body, StringComparison.Ordinal);

        // The runner must be handed to the same call, not resolved and dropped.
        int callIndex = body.IndexOf("UploadFileAsync(", StringComparison.Ordinal);
        Assert.True(callIndex >= 0, "the entry point must delegate to the upload core");
        Assert.Contains("runner", body[callIndex..], StringComparison.Ordinal);
    }

    [Fact]
    public void HostKeyRefusal_DistinguishesMismatchFromFirstUse_AndNamesTheEndpointOnly()
    {
        string mismatch = DescribeHostKeyRefusal(
            new HostKeyRejectedException("srv.example", 2222, "ssh-ed25519", "SHA256:PRESENTED", "SHA256:STORED"));
        string firstUse = DescribeHostKeyRefusal(
            new HostKeyRejectedException("srv.example", 2222, "ssh-ed25519", "SHA256:PRESENTED", null));

        Assert.Contains("mismatch", mismatch, StringComparison.Ordinal);
        Assert.Contains("possible MITM", mismatch, StringComparison.Ordinal);

        // A key that was never trusted is not evidence of interception. Reporting a first use as a
        // possible MITM is factually wrong and trains operators to ignore the real signal.
        Assert.DoesNotContain("MITM", firstUse, StringComparison.Ordinal);
        Assert.Contains("first use", firstUse, StringComparison.Ordinal);

        // Both must place the failure: without the endpoint the trace cannot be acted on.
        foreach (string description in new[] { mismatch, firstUse })
        {
            Assert.Contains("srv.example:2222", description, StringComparison.Ordinal);
            Assert.Contains("SFTP", description, StringComparison.Ordinal);

            // Never a fingerprint, presented or stored.
            Assert.DoesNotContain("SHA256:", description, StringComparison.Ordinal);
        }
    }

    // A global occurrence count cannot tell a trace inside the failing catch from one anywhere else in
    // the file, so each catch is extracted and read on its own. The names are the two host-key catches,
    // the two transport catches and the probe catch: the five places a no-clobber step can fail
    // remotely.
    [Theory]
    [InlineData("public async Task CreateDirectoryExclusiveAsync(", "no-clobber directory reservation")]
    [InlineData("private async Task PublishByExclusiveLink(", "no-clobber file publication")]
    public void TransportCatch_TracesExactlyOnce_UnderTheFailureItHandles(
        string methodSignature,
        string expectedOperation)
    {
        string body = ExtractMethodBody(ReadSftpBrowserSource(), methodSignature);
        string transportCatch = ExtractCatchBlock(body, TransportCatch, methodSignature);

        Assert.Equal(1, CountOccurrences(transportCatch, "LogNoClobberFailure("));
        Assert.Contains(
            $"LogNoClobberFailure(\"{expectedOperation}\", ex)",
            transportCatch,
            StringComparison.Ordinal);

        // The refusal itself must still be raised from the same catch: a trace that replaced the throw
        // would turn an unconfirmed outcome into a silent success.
        Assert.Contains(
            "throw new RemoteNoClobberPublishUnavailableException(",
            transportCatch,
            StringComparison.Ordinal);
        Assert.Contains("ct.ThrowIfCancellationRequested();", transportCatch, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeCatch_TracesExactlyOnce_WithTheExitStatus()
    {
        string body = ExtractMethodBody(
            ReadSftpBrowserSource(),
            "private Exception ClassifyFailedPublication(");
        string probeCatch = ExtractCatchBlock(
            body,
            ProbeCatch,
            "private Exception ClassifyFailedPublication(");

        Assert.Equal(1, CountOccurrences(probeCatch, "LogNoClobberFailure("));
        Assert.Contains(
            "\"no-clobber destination classification probe\"",
            probeCatch,
            StringComparison.Ordinal);

        // The exit status is the one detail worth keeping: it separates a link that refused from a
        // channel that broke, and it says nothing about the payload.
        Assert.Contains("exitStatus", probeCatch, StringComparison.Ordinal);
        Assert.Contains(
            "return new RemoteNoClobberPublishUnavailableException(",
            probeCatch,
            StringComparison.Ordinal);
    }

    // The description helper can be correct while the catch that reports the event ignores it. Both
    // catches must go through it, and neither may state the verdict itself.
    [Theory]
    [InlineData("public async Task CreateDirectoryExclusiveAsync(")]
    [InlineData("private async Task PublishByExclusiveLink(")]
    public void HostKeyCatch_DerivesItsVerdictFromTheDescriptionHelper(string methodSignature)
    {
        string body = ExtractMethodBody(ReadSftpBrowserSource(), methodSignature);

        // Comments stripped: the concern is code that states the verdict itself, and the prose here
        // legitimately explains what a mismatch means. Measuring the prose would make an accurate
        // comment a build failure and teach the next reader to delete the explanation.
        string hostKeyCatch = StripLineComments(ExtractCatchBlock(body, HostKeyCatch, methodSignature));

        Assert.Equal(1, CountOccurrences(hostKeyCatch, "DescribeHostKeyRefusal(hostKeyRejected)"));

        // A catch that hardcodes the verdict reports a first-use rejection as an interception while
        // the helper and its own test stay green.
        Assert.DoesNotContain("mismatch", hostKeyCatch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MITM", hostKeyCatch, StringComparison.Ordinal);
        Assert.DoesNotContain("first use", hostKeyCatch, StringComparison.OrdinalIgnoreCase);

        // The derived description must reach both the operator, through the log, and the caller,
        // through the wrapped refusal.
        int warnIndex = hostKeyCatch.IndexOf("FileLogger.Warn(", StringComparison.Ordinal);
        int throwIndex = hostKeyCatch.IndexOf(
            "throw new RemoteNoClobberPublishUnavailableException(",
            StringComparison.Ordinal);
        Assert.True(warnIndex >= 0, "a refused host key must be logged");
        Assert.True(throwIndex > warnIndex, "the refusal must be raised after it is logged");

        Assert.Contains("{refusal}", hostKeyCatch[warnIndex..throwIndex], StringComparison.Ordinal);
        Assert.Contains("{refusal}", hostKeyCatch[throwIndex..], StringComparison.Ordinal);
        Assert.Contains("hostKeyRejected);", hostKeyCatch[throwIndex..], StringComparison.Ordinal);
    }

    [Fact]
    public void TraceHelper_WarnsOnce_AndNamesNoRemotePath()
    {
        string helper = ExtractMethodBody(
            ReadSftpBrowserSource(),
            "private void LogNoClobberFailure(");

        // One emission, at warning level. A helper downgraded to Debug or pointed at an inert sink
        // leaves every call site intact and every other oracle green while the trace disappears.
        Assert.Equal(1, CountOccurrences(helper, "FileLogger.Warn("));
        Assert.Equal(1, CountOccurrences(helper, "FileLogger."));

        Assert.Contains("failure.GetType().Name", helper, StringComparison.Ordinal);
        Assert.Contains("connectionParams.Host", helper, StringComparison.Ordinal);
        Assert.Contains("connectionParams.Port", helper, StringComparison.Ordinal);

        // A trace is written to a file on disk. The destination path is the one piece of the operation
        // that names the operator's data, so it must never appear in it.
        Assert.DoesNotContain("remotePath", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("localPath", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Fingerprint", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableRefusal_StatesTheOutcomeIsUnconfirmed_AndPromisesNoRollback()
    {
        RemoteNoClobberPublishUnavailableException refusal = new("/srv/d/f.txt", "the channel dropped");

        Assert.Equal(
            "Publication of '/srv/d/f.txt' could not be confirmed (the channel dropped). The "
            + "destination may or may not have been created, an existing destination was not replaced, "
            + "and Heimdall does not fall back to a transfer that could replace one.",
            refusal.Message);
        Assert.Equal("the channel dropped", refusal.Reason);
        Assert.IsAssignableFrom<IOException>(refusal);
    }

    [Fact]
    public void ExistsRefusal_SaysTheDestinationWasThere_AndNothingWasReplaced()
    {
        RemoteDestinationExistsException collision = new("/srv/d/f.txt");

        Assert.Equal("Refused to publish: destination already exists: /srv/d/f.txt", collision.Message);
        Assert.Equal("/srv/d/f.txt", collision.RemotePath);
        Assert.IsAssignableFrom<IOException>(collision);
    }

    // A summary that says the publication was "refused because no exclusive publish primitive was
    // available" tells a maintainer the destination is untouched, which is exactly the inference that
    // deletes a cut source after an ambiguous failure. Anchored on the two declarations rather than on
    // the file as a whole, so a corrected paragraph elsewhere cannot satisfy it.
    [Fact]
    public void UnavailableExceptionDocumentation_DescribesAnUnconfirmedOutcome()
    {
        string source = ReadPublisherSource();

        string classSummary = ExtractPrecedingSummary(
            source,
            "public sealed class RemoteNoClobberPublishUnavailableException : IOException");
        string constructorSummary = ExtractPrecedingSummary(
            source,
            "    public RemoteNoClobberPublishUnavailableException(");

        Assert.Contains("could not be confirmed", classSummary, StringComparison.Ordinal);
        Assert.Contains("unconfirmed", constructorSummary, StringComparison.Ordinal);

        foreach (string summary in new[] { classSummary, constructorSummary })
        {
            Assert.DoesNotContain("refused because", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("no exclusive publish primitive was available", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("could not be made safe", summary, StringComparison.Ordinal);
        }
    }

    // FTP has no commit-time primitive that fails when the destination exists: everything reduces to a
    // client-side existence check followed by a rename, and the window between them is exactly the
    // hazard. So it must not advertise the capability, and the caller refuses instead of pasting.
    [Fact]
    public void FtpBrowser_DoesNotAdvertiseNoClobberPublication()
    {
        Assert.False(typeof(IRemoteNoClobberCapability).IsAssignableFrom(typeof(FtpBrowser)));
        Assert.False(typeof(IRemoteNoClobberPublisher).IsAssignableFrom(typeof(FtpBrowser)));

        // And the SFTP browser must, or the feature is dead on every transport.
        Assert.True(typeof(IRemoteNoClobberCapability).IsAssignableFrom(typeof(SftpBrowser)));
    }

    private static string MissingLocalPath()
        => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), MissingLocalFileMarker);

    /// <summary>
    /// Invokes the private upload core with the commit mode named by <paramref name="commitModeName"/>.
    /// </summary>
    /// <remarks>
    /// The mode stays a private nested enum: the commit strategy is this class's to choose, and a test
    /// that needed it visible would be buying its own convenience with production surface. The type is
    /// recovered from the core's own parameter, so nothing here names it.
    /// </remarks>
    private static Task InvokePrivateUploadAsync(
        SftpBrowser browser,
        string localPath,
        string remotePath,
        string commitModeName,
        ISftpExecCommandRunner? runner)
    {
        MethodInfo method = ResolvePrivateUploadCore();
        Type modeType = method.GetParameters()[UploadCoreCommitModeParameterIndex].ParameterType;

        // Enum.Parse throws on an unknown name rather than yielding a default, so a renamed member
        // fails loudly here instead of quietly exercising whichever mode happens to be zero.
        object commitMode = Enum.Parse(modeType, commitModeName);

        return (Task)method.Invoke(
            browser,
            [localPath, remotePath, commitMode, CancellationToken.None, runner])!;
    }

    private static MethodInfo ResolvePrivateUploadCore()
    {
        MethodInfo[] candidates = [.. typeof(SftpBrowser)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(static method => method.Name == "UploadFileAsync")
            .Where(static method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == UploadCoreParameterCount
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(string)
                    && parameters[UploadCoreCommitModeParameterIndex].ParameterType.IsEnum
                    && parameters[3].ParameterType == typeof(CancellationToken);
            })];

        // Exactly one, or these oracles could be exercising an overload production never calls.
        Assert.Single(candidates);

        return candidates[0];
    }

    private static string DescribeHostKeyRefusal(HostKeyRejectedException hostKeyRejected)
    {
        MethodInfo method = typeof(SftpBrowser).GetMethod(
            "DescribeHostKeyRefusal",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "the host-key description was not found; this oracle would be vacuous");

        return (string)method.Invoke(null, [hostKeyRejected])!;
    }

    private static string ReadSftpBrowserSource() => ReadSource("src/Heimdall.Sftp/SftpBrowser.cs");

    private static string ReadPublisherSource()
        => ReadSource("src/Heimdall.Sftp/IRemoteNoClobberPublisher.cs");

    private static string ReadSource(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "the repository root was not found; a source oracle that reads nothing proves nothing");
        }

        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }

    /// <summary>
    /// Returns the body of the private upload core, bounded to that method.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. Returning the rest of the file would make the ordering assertions read
    /// "the guard precedes the first occurrence anywhere below", which happens to hold today only
    /// because every token's first occurrence falls inside this method.
    /// </remarks>
    private static string ExtractPrivateUploadFileAsync()
        => ExtractMethodBody(ReadSftpBrowserSource(), "private async Task UploadFileAsync(");

    /// <summary>
    /// Returns the braced body of the method whose declaration starts with
    /// <paramref name="signature"/>, and nothing outside it.
    /// </summary>
    /// <remarks>
    /// The bound is what makes the assertions mean anything: a count taken over the whole file cannot
    /// distinguish a call inside the method that must make it from one anywhere else.
    /// </remarks>
    private static string ExtractMethodBody(string source, string signature)
    {
        int index = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(index >= 0, $"the method must exist, or this oracle reads nothing: {signature}");

        // A single declaration, so the body cannot be the wrong overload's.
        Assert.Equal(1, CountOccurrences(source, signature));

        return ExtractBracedBlock(source, index);
    }

    /// <summary>
    /// Removes single-line comments, leaving string literals untouched.
    /// </summary>
    /// <remarks>
    /// A <c>//</c> inside a string is not a comment, so the scan tracks quoting rather than matching
    /// the delimiter blindly.
    /// </remarks>
    private static string StripLineComments(string code)
    {
        System.Text.StringBuilder stripped = new(code.Length);
        bool inString = false;

        for (int index = 0; index < code.Length; index++)
        {
            char current = code[index];

            if (inString)
            {
                stripped.Append(current);
                if (current == '\\' && index + 1 < code.Length)
                {
                    stripped.Append(code[++index]);
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                stripped.Append(current);
                continue;
            }

            if (current == '/' && index + 1 < code.Length && code[index + 1] == '/')
            {
                int lineEnd = code.IndexOf('\n', index);
                if (lineEnd < 0)
                {
                    break;
                }

                index = lineEnd - 1;
                continue;
            }

            stripped.Append(current);
        }

        return stripped.ToString();
    }

    /// <summary>
    /// Returns the braced body of the single catch clause whose header starts with the given text.
    /// </summary>
    private static string ExtractCatchBlock(string methodBody, string catchHeader, string context)
    {
        int index = methodBody.IndexOf(catchHeader, StringComparison.Ordinal);
        Assert.True(index >= 0, $"the catch must exist in {context}: {catchHeader}");

        // One such clause per method: two would make "exactly once" ambiguous about which one traced.
        Assert.Equal(1, CountOccurrences(methodBody, catchHeader));

        return ExtractBracedBlock(methodBody, index);
    }

    /// <summary>
    /// Returns the XML summary block that immediately precedes <paramref name="declaration"/>.
    /// </summary>
    private static string ExtractPrecedingSummary(string source, string declaration)
    {
        int declarationIndex = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(declarationIndex >= 0, $"the declaration must exist: {declaration}");

        int summaryEnd = source.LastIndexOf("</summary>", declarationIndex, StringComparison.Ordinal);
        int summaryStart = source.LastIndexOf("<summary>", declarationIndex, StringComparison.Ordinal);
        Assert.True(
            summaryStart >= 0 && summaryEnd > summaryStart,
            $"the declaration must carry a summary: {declaration}");

        return source[summaryStart..summaryEnd];
    }

    private static string ExtractBracedBlock(string source, int startIndex)
    {
        int open = source.IndexOf('{', startIndex);
        Assert.True(open >= 0, "the block must have a body");

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException("the block was not closed; the oracle would read too much");
    }

    /// <summary>
    /// Removes every whitespace character, so an assertion pins code rather than formatting.
    /// </summary>
    private static string CollapseWhitespace(string code)
        => string.Concat(code.Where(static character => !char.IsWhiteSpace(character)));

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

    private sealed class CountingExecRunner : ISftpExecCommandRunner
    {
        public int Invocations { get; private set; }

        public Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new SftpExecResult(0, string.Empty));
        }
    }
}
