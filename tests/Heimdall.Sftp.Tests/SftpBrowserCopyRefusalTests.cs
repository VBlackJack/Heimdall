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

using System.Text.RegularExpressions;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Proves <see cref="SftpBrowser.CopyAsync"/> refuses instead of falling back when the server-side
/// copy did not happen, and that containment is decided before anything reaches the server.
/// </summary>
/// <remarks>
/// The containment cases run the real method: the guard precedes every probe, so they complete without
/// a connection and the runner call count proves nothing was sent. The outcome-to-refusal wiring cannot
/// run the same way, because the destination probe that follows the guard requires a live client; it is
/// pinned on the source instead, which is also what makes a reintroduced fallback detectable.
/// </remarks>
public sealed class SftpBrowserCopyRefusalTests
{
    [Theory]
    // Destination is the source, or sits inside it at any depth. Trailing separators must not matter.
    [InlineData("/srv/data", "/srv/data")]
    [InlineData("/srv/data", "/srv/data/sub")]
    [InlineData("/srv/data/", "/srv/data/sub/deeper")]
    public async Task CopyAsync_DestinationInsideSource_RefusesBeforeReachingTheServer(
        string source,
        string destination)
    {
        FakeExecRunner runner = new(
            static (_, _) => Task.FromResult(new SftpExecResult(0, string.Empty)));
        using SftpBrowser browser = new(runner);

        IOException refusal = await Assert.ThrowsAnyAsync<IOException>(
            () => browser.CopyAsync(source, destination, recursive: true));

        Assert.Contains("destination is the source or lies inside it", refusal.Message, StringComparison.Ordinal);
        // Nothing was sent: the guard runs ahead of every probe and every command.
        Assert.Equal(0, runner.CallCount);
    }

    // A sibling that merely shares a textual prefix is not inside the source and must get past the
    // guard. Without this case a guard that refused everything would look correct.
    [Fact]
    public async Task CopyAsync_DestinationSharesPrefixButIsNotInside_PassesTheContainmentGuard()
    {
        FakeExecRunner runner = new(
            static (_, _) => Task.FromResult(new SftpExecResult(0, string.Empty)));
        using SftpBrowser browser = new(runner);

        // Getting past the guard means reaching the destination probe, which needs a live client. That
        // it fails there rather than on containment is precisely the proof the sibling was allowed.
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => browser.CopyAsync("/srv/data", "/srv/database", recursive: true));

        Assert.DoesNotContain("lies inside it", failure.Message, StringComparison.Ordinal);
        Assert.IsNotType<RemoteCopyUnsupportedException>(failure);
    }

    [Fact]
    public void CopyAsync_RefusesOnAnyNonSuccessOutcome_AndHasNoRoundtripFallback()
    {
        string source = ReadSftpBrowserSource();
        string copyAsync = ExtractMethod(source, "public async Task CopyAsync(");

        // The refusal, and the outcome that carries the reason.
        Assert.Contains("ServerSideCopyOutcome outcome = await TryServerSideCopyAsync(", copyAsync, StringComparison.Ordinal);
        Assert.Contains("if (outcome == ServerSideCopyOutcome.Succeeded)", copyAsync, StringComparison.Ordinal);
        Assert.Contains("throw new RemoteCopyUnsupportedException(sourcePath, destinationPath, outcome);", copyAsync, StringComparison.Ordinal);

        // The containment guard precedes the destination probe.
        int guardIndex = copyAsync.IndexOf("RemoteCopyPathGuard.IsSameOrDescendantPath", StringComparison.Ordinal);
        int probeIndex = copyAsync.IndexOf("RemoteExistsAsync(destinationPath", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0, "the containment guard must be applied in CopyAsync");
        Assert.True(probeIndex >= 0, "the destination probe must remain in CopyAsync");
        Assert.True(guardIndex < probeIndex, "containment must be decided before the destination probe");

        // No route back to the removed best-effort fallback anywhere in the type.
        Assert.DoesNotContain("RunRoundtripCopyAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyFileViaRoundtripAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishIfAbsent", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SftpRefusal_CarriesItsOwnReason_AndDoesNotClaimTheProtocolLacksASafeCommit()
    {
        RemoteCopyUnsupportedException sftpRefusal =
            new("/srv/a", "/srv/b", ServerSideCopyOutcome.NonZeroExit);
        RemoteCopyUnsupportedException ftpRefusal = new("/srv/a", "/srv/b", "FTP");

        Assert.Equal(ServerSideCopyOutcome.NonZeroExit, sftpRefusal.Outcome);
        Assert.Equal("SFTP", sftpRefusal.Transport);
        // The FTP wording ("the protocol offers no commit...") is false for SFTP, which does have one.
        Assert.DoesNotContain("offers no commit", sftpRefusal.Message, StringComparison.Ordinal);
        Assert.Null(ftpRefusal.Outcome);
        Assert.NotEqual(ftpRefusal.Message, sftpRefusal.Message);
    }

    private static string ReadSftpBrowserSource()
    {
        string path = Path.Combine(
            RepositoryRoot(),
            "src",
            "Heimdall.Sftp",
            "SftpBrowser.cs");
        Assert.True(File.Exists(path), $"SftpBrowser.cs not found at {path}");
        return File.ReadAllText(path);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string ExtractMethod(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method not found: {signature}");

        int braceDepth = 0;
        bool seenOpening = false;
        for (int index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                braceDepth++;
                seenOpening = true;
            }
            else if (source[index] == '}')
            {
                braceDepth--;
                if (seenOpening && braceDepth == 0)
                {
                    return source[start..(index + 1)];
                }
            }
        }

        Assert.Fail($"unbalanced braces after {signature}");
        return string.Empty;
    }

    private sealed class FakeExecRunner : ISftpExecCommandRunner
    {
        private readonly Func<string, CancellationToken, Task<SftpExecResult>> _executeAsync;

        public FakeExecRunner(Func<string, CancellationToken, Task<SftpExecResult>> executeAsync)
        {
            _executeAsync = executeAsync;
        }

        public int CallCount { get; private set; }

        public Task<SftpExecResult> ExecuteAsync(string command, CancellationToken ct)
        {
            CallCount++;
            return _executeAsync(command, ct);
        }
    }
}
