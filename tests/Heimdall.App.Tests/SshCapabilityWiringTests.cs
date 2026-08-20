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
using System.IO;
using Heimdall.App.Services.Handlers;

namespace Heimdall.App.Tests;

/// <summary>
/// Ties the forwarding claim to the code that has to make it true.
/// </summary>
/// <remarks>
/// <para>X11 forwarding and compression are profile settings the user can switch on. The in-process
/// transport cannot honor either, so it says so; the two external transports carry them as command
/// line flags and therefore say nothing.</para>
/// <para>Those are two halves of one promise, and nothing used to hold them together. The capability
/// tests assert that PuTTY and Plink need no notice, and the launcher tests only ever built command
/// lines with both settings switched off. Dropping the flag from a launcher would leave the user
/// with no forwarding and no notice - the original defect - while the whole suite stayed green and
/// the capability tests vouched for it.</para>
/// <para>So the claim is not asserted here a second time. It is derived: a path allowed to stay
/// silent has to be a path whose launcher actually passes the flags.</para>
/// </remarks>
public sealed class SshCapabilityWiringTests
{
    /// <summary>
    /// The transports whose launcher this test knows how to build.
    /// </summary>
    /// <remarks>
    /// Compared against the enum below, so a forwarding-capable path added later fails here rather
    /// than inheriting silence it was never shown to deserve.
    /// </remarks>
    private static readonly SshResolvedPath[] LaunchablePaths =
    [
        SshResolvedPath.ExternalPutty,
        SshResolvedPath.PlinkPipe,
    ];

    // The paths are swept inside the bodies rather than carried as theory arguments: the enum is
    // internal to the app assembly, and a public test signature cannot expose it. Every assertion
    // names the path it failed on, so nothing is lost but the case count.

    [Fact]
    public void APathExcusedFromNoticingPassesTheFlagsItWasExcusedFor()
    {
        foreach (SshResolvedPath path in LaunchablePaths)
        {
            // The premise being relied on, stated where it is relied on: this path is excused from
            // warning the user. The rest is what has to be true for that to be honest.
            Assert.True(SshCapabilityScope.SupportsForwarding(path), $"{path} is expected to support forwarding.");
            Assert.Null(SshCapabilityScope.Evaluate(path, x11Forwarding: true, compression: true));

            IReadOnlyList<string> arguments = BuildLaunchArguments(path, x11Forwarding: true, compression: true);

            Assert.True(arguments.Contains("-X"), $"{path} is excused from noticing but never passes -X.");
            Assert.True(arguments.Contains("-C"), $"{path} is excused from noticing but never passes -C.");
        }
    }

    /// <summary>
    /// Guards the guard: a launcher that always passed the flags would satisfy the test above
    /// without carrying the request at all.
    /// </summary>
    [Fact]
    public void APathPassesNeitherFlagWhenNeitherWasRequested()
    {
        foreach (SshResolvedPath path in LaunchablePaths)
        {
            IReadOnlyList<string> arguments = BuildLaunchArguments(path, x11Forwarding: false, compression: false);

            Assert.False(arguments.Contains("-X"), $"{path} passes -X without being asked to.");
            Assert.False(arguments.Contains("-C"), $"{path} passes -C without being asked to.");
        }
    }

    [Fact]
    public void EachFlagFollowsItsOwnSetting()
    {
        foreach (SshResolvedPath path in LaunchablePaths)
        {
            IReadOnlyList<string> x11Only = BuildLaunchArguments(path, x11Forwarding: true, compression: false);
            IReadOnlyList<string> compressionOnly = BuildLaunchArguments(path, x11Forwarding: false, compression: true);

            Assert.True(x11Only.Contains("-X"), $"{path} drops -X when only X11 is requested.");
            Assert.False(x11Only.Contains("-C"), $"{path} adds -C when only X11 is requested.");

            Assert.True(compressionOnly.Contains("-C"), $"{path} drops -C when only compression is requested.");
            Assert.False(compressionOnly.Contains("-X"), $"{path} adds -X when only compression is requested.");
        }
    }

    /// <summary>
    /// Every path excused from noticing has to be one this test can actually launch.
    /// </summary>
    /// <remarks>
    /// Without this, adding a fourth transport that reports <c>SupportsForwarding</c> would silence
    /// the notice for it and no test would ask whether its launcher passes anything.
    /// </remarks>
    [Fact]
    public void EveryPathExcusedFromNoticingIsCoveredHere()
    {
        List<SshResolvedPath> excused = [];
        foreach (SshResolvedPath path in Enum.GetValues<SshResolvedPath>())
        {
            if (SshCapabilityScope.SupportsForwarding(path))
            {
                excused.Add(path);
            }
        }

        Assert.Equal(LaunchablePaths.Order().ToArray(), excused.Order().ToArray());
    }

    /// <summary>
    /// The in-process transport is the one that has to speak up, and it is the only one.
    /// </summary>
    [Fact]
    public void TheInProcessTransportIsTheOnlyOneThatNotices()
    {
        Assert.NotNull(SshCapabilityScope.Evaluate(
            SshResolvedPath.Direct,
            x11Forwarding: true,
            compression: true));

        Assert.DoesNotContain(SshResolvedPath.Direct, LaunchablePaths);
    }

    /// <summary>
    /// The notice has to reach the user, not merely be computed.
    /// </summary>
    /// <remarks>
    /// <para>Read from source because neither handler can be constructed without a live connection
    /// stack. Coarse, and deliberately so: what it rules out is the notice being computed and then
    /// dropped, which is the shape a refactor is most likely to leave behind.</para>
    /// <para>The shell handler pushes it to the status line; the file handler carries it out on the
    /// connection result, because an SFTP session shows no status line of its own.</para>
    /// </remarks>
    [Theory]
    [InlineData("SshHandler.cs", "SetStatusText?.Invoke(_localizer[capabilityNotice.StatusLocalizationKey]);")]
    [InlineData("SftpHandler.cs", "Warning: capabilityWarning);")]
    public void TheNoticeIsRoutedOutOfTheHandlerThatComputesIt(string fileName, string routingLine)
    {
        string source = ReadHandlerSource(fileName);

        Assert.Contains("SshCapabilityScope.Evaluate(", source, StringComparison.Ordinal);
        Assert.Contains(routingLine, source, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> BuildLaunchArguments(
        SshResolvedPath path,
        bool x11Forwarding,
        bool compression)
    {
        // Every path-like argument is left out so the Plink command line, which is one string,
        // splits into exact tokens. With a key path or a fingerprint present it would carry quoted
        // text, and a flag could then be matched inside a value rather than as an argument.
        if (path == SshResolvedPath.ExternalPutty)
        {
            ProcessStartInfo startInfo = SshHandler.BuildPuttyStartInfo(
                @"C:\tools\putty.exe",
                keyPath: null,
                compression: compression,
                agentForwarding: false,
                x11Forwarding: x11Forwarding,
                port: 22,
                target: "user@example.com",
                hostKeyFingerprint: null);

            return [.. startInfo.ArgumentList];
        }

        if (path == SshResolvedPath.PlinkPipe)
        {
            string arguments = SshHandler.BuildPipeModeArguments(
                keyPath: null,
                compression: compression,
                agentForwarding: false,
                x11Forwarding: x11Forwarding,
                port: 22,
                target: "user@example.com",
                hostKeyFingerprint: null,
                passwordFilePath: null);

            return arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        throw new ArgumentOutOfRangeException(
            nameof(path),
            path,
            "This path has no launcher here, so it must not be excused from noticing.");
    }

    private static string ReadHandlerSource(string fileName)
    {
        string path = Path.Combine(
            FindRepoRoot(),
            "src",
            "Heimdall.App",
            "Services",
            "Handlers",
            fileName);

        Assert.True(File.Exists(path), $"Handler source not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
