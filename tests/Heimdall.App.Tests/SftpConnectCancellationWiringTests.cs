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
using System.Linq;
using System.Text.RegularExpressions;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// No blocking <c>Connect()</c> survives in Heimdall.Sftp: every connect goes through the
/// SSH side's cancellable connect, whose token reaches the handshake.
/// </summary>
/// <remarks>
/// <para>
/// SSH.NET assigns a client's session only once the whole handshake has run, so
/// <c>Task.Run(() =&gt; client.Connect(), ct)</c> checked the token before the call and never
/// again: cancelling did nothing and the attempt ran on to the connect timeout with the
/// user watching. Four sites did that - the browser, the exec runner, and both sudo paths
/// of the remote editor. They now call
/// <c>SshConnectionFactory.ConnectWithCancellationAsync</c>, whose behavioural oracle lives
/// beside it in Heimdall.Ssh.Tests.
/// </para>
/// <para>
/// The oracle here is the census, and that is deliberate rather than lazy. A presence
/// reading of the four call sites is what this file would naturally have held, and it would
/// have been the weaker test twice over: the repository's own
/// <c>SourceReadingAssertionGuardTests</c> refuses a bare fragment of production source,
/// and the anchor could not be carried through
/// <see cref="ViewSource.IsStatementOfTheMethodBody"/> either, because three of the four
/// sites sit inside a try block and that predicate reads statements of a method body. What
/// the census does catch is the regression that actually threatens this lot over time: not
/// the four known sites being reverted one by one, but a fifth one being written next year
/// by someone who copied an older file.
/// </para>
/// <para>
/// What none of this establishes: that a cancel now interrupts a transfer already in
/// flight. It does not, deliberately. The study behind this lot found that abandoning an
/// in-flight SFTP request would release the client lock with the request still on the wire,
/// leaving the next caller to reuse a session the server is still answering - a worse
/// defect than the one it would fix.
/// </para>
/// </remarks>
public sealed class SftpConnectCancellationWiringTests
{
    private const string BrowserFile = "SftpBrowser.cs";
    private const string ExecRunnerFile = "SftpExecCommandRunner.cs";
    private const string EditorFile = "RemoteFileEditor.cs";

    private const int MinimumSourceFiles = 10;

    [Fact]
    public void NoBlockingConnectSurvivesAnywhereInTheSftpAssembly()
    {
        string[] offenders = SftpSourceFiles()
            .Where(path => HoldsBlockingConnect(ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(path))))
            .Select(path => Path.GetFileName(path) ?? path)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "a blocking Connect() remains in Heimdall.Sftp, so a cancel cannot reach its handshake: "
                + string.Join(", ", offenders));
    }

    [Fact]
    public void NoTaskRunWrapperIsLeftAroundAConnect()
    {
        // The shape, not only the call: a wrapper rebuilt around ConnectAsync would pass
        // the census above while restoring exactly the uninterruptible wait it removed.
        string[] offenders = SftpSourceFiles()
            .Where(path => HoldsTaskRunAroundConnect(ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(path))))
            .Select(path => Path.GetFileName(path) ?? path)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "a connect is wrapped in Task.Run again in Heimdall.Sftp: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheCensusReadsTheAssemblyItClaimsTo()
    {
        // Without this, both readings above are green on an empty file list: a wrong path,
        // a renamed directory, or a build-output filter that swallowed everything looks
        // exactly like a clean assembly.
        string[] files = SftpSourceFiles();

        Assert.True(
            files.Length >= MinimumSourceFiles,
            $"only {files.Length} source file(s) were read from Heimdall.Sftp, so the census measured nothing");
        Assert.Contains(files, path => Path.GetFileName(path) == BrowserFile);
        Assert.Contains(files, path => Path.GetFileName(path) == ExecRunnerFile);
        Assert.Contains(files, path => Path.GetFileName(path) == EditorFile);
    }

    [Fact]
    public void TheCensusReportsAConnectItIsShownOnPurpose()
    {
        // The positive control. Both readings above assert an absence, and an absence is
        // what a broken matcher reports too. These are the exact shapes they must catch.
        Assert.True(HoldsBlockingConnect("            client.Connect();"));
        Assert.True(HoldsBlockingConnect("        sshClient.Connect();"));
        Assert.True(
            HoldsTaskRunAroundConnect(
                "await Task.Run(() =>\r\n{\r\n    ct.ThrowIfCancellationRequested();\r\n    client.Connect();\r\n}, ct);"));

        // And what they must not catch: the cancellable call this lot moved to.
        Assert.False(
            HoldsBlockingConnect(
                "await SshConnectionFactory.ConnectWithCancellationAsync(client, ct).ConfigureAwait(false);"));
        Assert.False(
            HoldsTaskRunAroundConnect(
                "await Task.Run(() => client.ListDirectory(path), ct).ConfigureAwait(false);"));
    }

    /// <summary>Whether the text calls <c>Connect()</c> on any client, blocking.</summary>
    private static bool HoldsBlockingConnect(string source)
        => Regex.IsMatch(source, @"\w+\.Connect\(\)", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>Whether a <c>Task.Run</c> body reaches a connect of either shape.</summary>
    /// <remarks>
    /// The window crosses statement separators on purpose. A first version excluded them,
    /// which read well and matched nothing: the wrapper this guard exists for holds a
    /// <c>ct.ThrowIfCancellationRequested();</c> before the connect, so the semicolon ended
    /// every match before it began. The control below is what caught that, and it is the
    /// reason an absence assertion is never shipped here without one.
    /// </remarks>
    private static bool HoldsTaskRunAroundConnect(string source)
        => Regex.IsMatch(
            source,
            @"Task\.Run\([\s\S]{0,200}?\.Connect(?:Async)?\(",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

    private static string[] SftpSourceFiles()
    {
        string root = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.Sftp");
        Assert.True(Directory.Exists(root), $"Source directory not found: {root}");

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, root))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBuildOutput(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);
    }
}
