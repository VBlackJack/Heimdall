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
using System.Globalization;
using System.Text;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Runs the privileged write under a real POSIX <c>sh</c>, as the current user, to check the mode
/// a new file is published with. Cases that need real permission bits, supplementary groups,
/// setgid inheritance or POSIX ACLs are skipped, with a reason, where the system here cannot give
/// them.
/// </summary>
public sealed class PrivilegedNewFileModeRunTests
{
    private const string Payload = "new-content";

    // The group of the current user other than its primary one, empty when there is none. Shared by
    // the probe of the setgid case and by the case itself.
    internal const string SupplementaryGroup =
        "g=$(id -G | tr ' ' '\\n' | grep -vx \"$(id -g)\" | head -n 1); ";

    private static string WriteScript(bool overwrite, string target = "srv/out.txt")
        => PrivilegedWorkDirectoryTextTests.ScriptOf(
            PrivilegedFileCommands.BuildAtomicWriteBody(target, overwrite));

    [PosixModeBitsFact]
    public async Task Write_ANewFileIs644WhateverTheCallerUmaskAndWhetherOrNotReplacingIsAllowed()
    {
        foreach (string umask in new[] { "000", "022", "077" })
        {
            foreach (bool overwrite in new[] { true, false })
            {
                using var area = new Area();

                Run run = await area.RunAsync(WriteScript(overwrite), $"umask {umask}; ", Payload);

                string label = $"umask {umask}, overwrite {overwrite}";
                Assert.True(run.ExitCode == 0, $"{label}: exit {run.ExitCode}: {run.Stderr}");
                Assert.Equal(Payload, area.Read("srv/out.txt"));
                Assert.Equal("644", (await area.ShellAsync("stat -c %a srv/out.txt")).Stdout.Trim());
                Assert.Empty(area.Leftovers());
            }
        }
    }

    [PosixModeBitsFact]
    public async Task Write_AtTheSyncTheWorkDirectoryIsPrivateThePayloadIs644AndNothingIsPublishedYet()
    {
        using var area = new Area();
        string shadow =
            "sync() { if [ \"$1\" = -f ] && [ \"$2\" = payload ]; then "
            + "printf '%s %s\\n' \"$(stat -c %a .)\" \"$(stat -c %a payload)\" > \"$root/at-sync.txt\"; "
            + "if [ -e \"$root/srv/out.txt\" ] || [ -L \"$root/srv/out.txt\" ]; then "
            + ": > \"$root/published-early.txt\"; fi; fi; command sync \"$@\"; }; ";

        Run run = await area.RunAsync(WriteScript(true), "umask 022; " + shadow, Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("700 644", area.Read("at-sync.txt").Trim());
        Assert.False(area.Exists("published-early.txt"), "the target existed before the sync.");
        Assert.Equal("644", (await area.ShellAsync("stat -c %a srv/out.txt")).Stdout.Trim());
    }

    [PosixAttributeCopyAndModeBitsFact]
    public async Task Write_ReplacingA600FileKeepsItsMode()
    {
        using var area = new Area();
        area.Put("srv/out.txt", "old-content");
        await area.ShellAsync("chmod 600 srv/out.txt");

        Run run = await area.RunAsync(WriteScript(true), "umask 022; ", Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("srv/out.txt"));
        Assert.Equal("600", (await area.ShellAsync("stat -c %a srv/out.txt")).Stdout.Trim());
    }

    [PosixModeBitsFact]
    public async Task Write_ANewFileInAPlainFolderTakesTheGroupOfTheAccount()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), "umask 022; ", Payload);

        Assert.Equal(0, run.ExitCode);
        string group = (await area.ShellAsync("stat -c %g srv/out.txt")).Stdout.Trim();
        string expected = (await area.ShellAsync("id -g")).Stdout.Trim();
        Assert.Equal(expected, group);
    }

    [PosixSetgidFolderFact]
    public async Task Write_ANewFileInASetgidFolderTakesTheGroupOfTheFolderAndIs644()
    {
        using var area = new Area();
        Run setup = await area.ShellAsync(
            SupplementaryGroup + "mkdir sg && chgrp \"$g\" sg && chmod 2775 sg && printf '%s' \"$g\"");
        Assert.Equal(0, setup.ExitCode);
        string folderGroup = setup.Stdout.Trim();

        Run run = await area.RunAsync(WriteScript(true, "sg/out.txt"), "umask 022; ", Payload, "sg/out.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("sg/out.txt"));
        Assert.Equal(
            folderGroup + " 644",
            (await area.ShellAsync("stat -c '%g %a' sg/out.txt")).Stdout.Trim());
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(area.Root, "sg"), ".heimdall-*"));
    }

    [PosixDefaultAclFolderFact]
    public async Task Write_ANewFileInAFolderWithADefaultAclIsNotWidenedAndNoChmodRuns()
    {
        using var area = new Area();
        Run setup = await area.ShellAsync(
            "mkdir acl && setfacl -d -m \"u:$(id -u):rw-,o::---\" acl");
        Assert.Equal(0, setup.ExitCode);
        string shadow =
            "chmod() { printf '%s\\n' \"$*\" >> \"$root/chmod.log\"; command chmod \"$@\"; }; ";

        Run run = await area.RunAsync(
            WriteScript(true, "acl/out.txt"), "umask 022; " + shadow, Payload, "acl/out.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("acl/out.txt"));
        string listing = (await area.ShellAsync("ls -ld acl/out.txt")).Stdout.Trim();
        Assert.Matches("^.{10}\\+", listing);
        string mode = (await area.ShellAsync("stat -c %a acl/out.txt")).Stdout.Trim();
        Assert.EndsWith("0", mode, StringComparison.Ordinal);
        Assert.False(area.Exists("chmod.log"), "a chmod ran: " + (area.Exists("chmod.log") ? area.Read("chmod.log") : string.Empty));
    }

    [PosixDefaultAclFolderFact]
    public async Task Write_ANewFileInAFolderWithABaseOnlyDefaultAclIsNotWidenedAndNoChmodRuns()
    {
        using var area = new Area();
        Run setup = await area.ShellAsync("mkdir acl && setfacl -d -m u::rwx,g::r-x,o::--- acl");
        Assert.Equal(0, setup.ExitCode);
        string shadow =
            "chmod() { printf '%s\\n' \"$*\" >> \"$root/chmod.log\"; command chmod \"$@\"; }; ";

        Run run = await area.RunAsync(
            WriteScript(true, "acl/out.txt"), "umask 022; " + shadow, Payload, "acl/out.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("acl/out.txt"));
        Assert.Equal("640", (await area.ShellAsync("stat -c %a acl/out.txt")).Stdout.Trim());
        Assert.False(area.Exists("chmod.log"), "a chmod ran: " + (area.Exists("chmod.log") ? area.Read("chmod.log") : string.Empty));
    }

    [PosixDefaultAclFolderFact]
    public async Task Write_ANewFileInAFolderWithABaseOnlyDefaultAclThatGives600IsNotWidenedAndNoChmodRuns()
    {
        using var area = new Area();
        Run setup = await area.ShellAsync("mkdir acl && setfacl -d -m u::rwx,g::---,o::--- acl");
        Assert.Equal(0, setup.ExitCode);

        Run run = await area.RunAsync(
            WriteScript(true, "acl/out.txt"), "umask 022; " + ChmodLog, Payload, "acl/out.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("acl/out.txt"));
        Assert.Equal("600", (await area.ShellAsync("stat -c %a acl/out.txt")).Stdout.Trim());
        AssertNoChmod(area);
    }

    [PosixDefaultAclFolderFact]
    public async Task Write_ANewFileInAFolderWithANamedDefaultAclThatGives600IsNotWidenedAndNoChmodRuns()
    {
        using var area = new Area();
        Run setup = await area.ShellAsync(
            "mkdir acl && setfacl -d -m \"u::rwx,g::---,o::---,m::---,u:$(id -u):r--\" acl");
        Assert.Equal(0, setup.ExitCode);

        Run run = await area.RunAsync(
            WriteScript(true, "acl/out.txt"), "umask 022; " + ChmodLog, Payload, "acl/out.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("acl/out.txt"));
        Assert.Matches("^.{10}\\+", (await area.ShellAsync("ls -ld acl/out.txt")).Stdout.Trim());
        Assert.Equal("600", (await area.ShellAsync("stat -c %a acl/out.txt")).Stdout.Trim());
        AssertNoChmod(area);
    }

    [PosixModeBitsFact]
    public async Task Write_APayloadThatListsWithAnAclIsLeftAloneWhateverTheWorkDirectoryShows()
    {
        using var area = new Area();
        string shadow =
            "ls() { if [ \"$1\" = -ld ] && [ \"$3\" = payload ]; then "
            + "printf '%s\\n' '-rw-------+ 1 u g 0 Jan 1 00:00 payload'; else command ls \"$@\"; fi; }; ";

        Run run = await area.RunAsync(WriteScript(true), "umask 022; " + ChmodLog + shadow, Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("600", (await area.ShellAsync("stat -c %a srv/out.txt")).Stdout.Trim());
        AssertNoChmod(area);
    }

    [PosixModeBitsFact]
    public async Task Write_APayloadWhoseModeIsNot600IsLeftAlone()
    {
        using var area = new Area();
        string shadow =
            "stat() { if [ \"$1\" = -c ] && [ \"$2\" = %a ] && [ \"$4\" = payload ]; then "
            + "printf '%s\\n' 640; else command stat \"$@\"; fi; }; ";

        Run run = await area.RunAsync(WriteScript(true), "umask 022; " + ChmodLog + shadow, Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("600", (await area.ShellAsync("stat -c %a srv/out.txt")).Stdout.Trim());
        AssertNoChmod(area);
    }

    [PosixModeBitsFact]
    public async Task Write_AnLsThatFailsLeavesTheModeAloneAndTheWriteSucceeds()
    {
        using var area = new Area();
        string shadow = "ls() { if [ \"$1\" = -ld ]; then return 1; fi; command ls \"$@\"; }; ";

        Run run = await area.RunAsync(WriteScript(true), "umask 022; " + ChmodLog + shadow, Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("srv/out.txt"));
        Assert.Equal("600", (await area.ShellAsync("stat -c %a srv/out.txt")).Stdout.Trim());
        AssertNoChmod(area);
    }

    [PosixModeBitsFact]
    public async Task Write_AChmodThatIsRefusedDoesNotFailTheWrite()
    {
        using var area = new Area();
        string shadow = "chmod() { return 1; }; ";

        Run run = await area.RunAsync(WriteScript(true), "umask 022; " + shadow, Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("srv/out.txt"));
        Assert.Empty(area.Leftovers());
    }

    private const string ChmodLog =
        "chmod() { printf '%s\\n' \"$*\" >> \"$root/chmod.log\"; command chmod \"$@\"; }; ";

    private static void AssertNoChmod(Area area)
        => Assert.False(area.Exists("chmod.log"), "a chmod ran: " + (area.Exists("chmod.log") ? area.Read("chmod.log") : string.Empty));
    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    /// <summary>A temporary directory holding the parent <c>srv</c>.</summary>
    private sealed class Area : IDisposable
    {
        private const int ProcessTimeoutSeconds = 60;

        public Area()
        {
            Root = Path.Combine(Path.GetTempPath(), "heimdall-newmode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "srv"));
        }

        public string Root { get; }

        public void Put(string relative, string content)
            => File.WriteAllText(Path.Combine(Root, relative), content, new UTF8Encoding(false));

        public string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

        public bool Exists(string relative)
            => File.Exists(Path.Combine(Root, relative)) || Directory.Exists(Path.Combine(Root, relative));

        public string[] Leftovers()
            => Directory.GetFileSystemEntries(Path.Combine(Root, "srv"), ".heimdall-*");

        public Task<Run> ShellAsync(string script) => StartAsync(["-c", script], ReadOnlyMemory<byte>.Empty);

        /// <summary>Runs a script as the remote shell would: "sh -c script sh target".</summary>
        public Task<Run> RunAsync(string script, string prefix, string input, string target = "srv/out.txt")
            => StartAsync(["-c", "root=$PWD; " + prefix + script, "sh", target], Encoding.UTF8.GetBytes(input));

        private async Task<Run> StartAsync(string[] arguments, ReadOnlyMemory<byte> input)
        {
            var start = new ProcessStartInfo(PosixShellFactAttribute.FindShell()!)
            {
                WorkingDirectory = Root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            // The shell's own directory first, so cat, stat and the like resolve even when the
            // shell was found by its install path rather than through PATH.
            start.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                Path.GetDirectoryName(start.FileName),
                Environment.GetEnvironmentVariable("PATH"));

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            await using (Stream stdin = process.StandardInput.BaseStream)
            {
                try
                {
                    await stdin.WriteAsync(input);
                }
                catch (IOException)
                {
                    // The script ended before reading it all; the status says why.
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(ProcessTimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            await Task.WhenAll(stdout, stderr);
            return new Run(process.ExitCode, await stdout, await stderr);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A temporary directory left behind is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}

/// <summary>
/// Runs only where the account has a supplementary group and a folder it owns, given that group
/// and the setgid bit, passes the group on to what is created in it.
/// </summary>
public sealed class PosixSetgidFolderFactAttribute()
    : PosixProbeFactAttribute(
        PrivilegedNewFileModeRunTests.SupplementaryGroup
        + "[ -n \"$g\" ] && mkdir d && chgrp \"$g\" d && chmod 2775 d && mkdir d/s "
        + "&& [ \"$(stat -c %g d/s)\" = \"$g\" ] && [ \"$(stat -c %a d/s | cut -c1)\" = 2 ]",
        "This system has no supplementary group for the account, or does not inherit setgid folders.");

/// <summary>
/// Runs only where <c>setfacl</c> is installed and a default ACL with a named entry is handed to a
/// file created in the folder (the file then lists with a <c>+</c>).
/// </summary>
public sealed class PosixDefaultAclFolderFactAttribute()
    : PosixProbeFactAttribute(
        "command -v setfacl >/dev/null && mkdir d && setfacl -d -m \"u:$(id -u):rw-,o::---\" d "
        + "&& : > d/f && ls -ld d/f | grep -q '^.\\{10\\}+'",
        "This system has no setfacl or its filesystem does not inherit default ACLs.");
