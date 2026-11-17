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
using System.Text;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Runs the write, read and delete scripts under a real POSIX <c>sh</c>, as the current user, in a
/// temporary directory built by the test. Every scenario is functional: links and folder modes are
/// created before the script runs and nothing changes while it runs. Cases that need real links or
/// permission bits are skipped, with a reason, where the shell here cannot give them.
/// </summary>
public sealed class PrivilegedPathAndLinkRunTests
{
    private const int WriteRefusedStatus = 73;

    private const int ReadRefusedStatus = 74;

    private const string Payload = "new-content";

    private const string Original = "original-content";

    // --- A link in the path ------------------------------------------------------------------------

    [PosixLinkAndModeFact]
    public async Task Write_ThroughALinkTheAccountOwnsInAPrivateFolderIsDoneInTheFolderItNames()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real && ln -s real link");

        Run run = await area.RunAsync(WriteScript("link/out.txt"), "link/out.txt", Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("real/out.txt"));
        Assert.Empty(area.Leftovers("real"));
    }

    [PosixLinkAndModeFact]
    public async Task Write_ThroughARelativeLinkThatClimbsOneFolderIsDoneInTheFolderItNames()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real sub && ln -s ../real sub/link");

        Run run = await area.RunAsync(WriteScript("sub/link/out.txt"), "sub/link/out.txt", Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("real/out.txt"));
        Assert.Empty(area.Leftovers("real"));
    }

    [PosixLinkAndModeFact]
    public async Task Read_ThroughALinkTheAccountOwnsInAPrivateFolderReturnsTheFile()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real && ln -s real link");
        area.Put("real/out.txt", Original);

        Run run = await area.RunAsync(ReadScript("link/out.txt"), "link/out.txt", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Original, Encoding.UTF8.GetString(Convert.FromBase64String(run.Stdout.Trim())));
        Assert.Empty(area.Leftovers("real"));
    }

    [PosixLinkAndModeFact]
    public async Task Write_ThroughALinkInAFolderOthersCanWriteToIsRefusedAndNothingIsCreated()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real sub && ln -s ../real sub/link && chmod 775 sub");

        Run run = await area.RunAsync(WriteScript("sub/link/out.txt"), "sub/link/out.txt", Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.PathLink, PrivilegedFileCommands.ParseTransferRefusal(run.Stderr, run.ExitCode));
        Assert.False(area.Exists("real/out.txt"), "the destination was written.");
        Assert.Empty(area.Leftovers("real"));
        Assert.Empty(area.Leftovers("sub"));
    }

    [PosixLinkAndModeFact]
    public async Task Read_ThroughALinkInAFolderOthersCanWriteToIsRefusedAndNothingIsRead()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real sub && ln -s ../real sub/link && chmod 775 sub");
        area.Put("real/out.txt", Original);

        Run run = await area.RunAsync(ReadScript("sub/link/out.txt"), "sub/link/out.txt", string.Empty);

        Assert.Equal(ReadRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.PathLink, PrivilegedFileCommands.ParseTransferRefusal(run.Stderr, run.ExitCode));
        Assert.Empty(run.Stdout.Trim());
        Assert.Empty(area.Leftovers("real"));
    }

    [PosixLinkAndModeFact]
    public async Task Write_ALoopOfLinksEndsWithARefusalInsteadOfRunningForever()
    {
        using var area = new Area();
        await area.ShellAsync("ln -s b a && ln -s a b");

        Run run = await area.RunAsync(WriteScript("a/out.txt"), "a/out.txt", Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.PathLink, PrivilegedFileCommands.ParseTransferRefusal(run.Stderr, run.ExitCode));
        Assert.False(area.Exists("a/out.txt"), "the destination was written.");
        Assert.Empty(area.FindAll("out.txt"));
        Assert.Empty(area.Leftovers("srv"));
    }

    [PosixLinkAndModeFact]
    public async Task Write_ALinkToAFileInsteadOfAFolderIsRefusedAndTheFileIsUntouched()
    {
        using var area = new Area();
        area.Put("plain", Original);
        await area.ShellAsync("ln -s plain link");

        Run run = await area.RunAsync(WriteScript("link/out.txt"), "link/out.txt", Payload);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal(Original, area.Read("plain"));
    }

    [PosixLinkAndModeFact]
    public async Task Write_APathWithADotDotComponentIsDoneWhenEveryFolderIsClosed()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir srv/sub");

        Run run = await area.RunAsync(WriteScript("srv/sub/../out.txt"), "srv/sub/../out.txt", Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("srv/out.txt"));
        Assert.Empty(area.Leftovers("srv"));
        Assert.Empty(area.Leftovers("srv/sub"));
    }

    [PosixLinkAndModeFact]
    public async Task Chmod_OfAFolderThroughALinkTheAccountOwnsChangesTheFolderItNames()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real real/dir && chmod 755 real/dir && ln -s real link");

        Run run = await area.RunAsync(ChmodScript("700"), "link/dir", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(0, (await area.ShellAsync("[ \"$(stat -c %a real/dir)\" = 700 ]")).ExitCode);
    }

    [PosixLinkAndModeFact]
    public async Task Chmod_OfAFolderThroughALinkInAFolderOthersCanWriteToIsRefusedAndNothingChanges()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real real/dir sub && chmod 755 real/dir && ln -s ../real sub/link && chmod 775 sub");

        Run run = await area.RunAsync(ChmodScript("700"), "sub/link/dir", string.Empty);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.PathLink, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.Equal(0, (await area.ShellAsync("[ \"$(stat -c %a real/dir)\" = 755 ]")).ExitCode);
    }

    // --- Hard links --------------------------------------------------------------------------------

    [PosixHardLinkAndModeFact]
    public async Task Write_AFileWithOtherHardLinksInAFolderOthersCanWriteToIsRefusedAndNothingIsLeftBehind()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Original);
        await area.ShellAsync("ln srv/out.txt srv/other.txt && chmod 775 srv");

        Run run = await area.RunAsync(WriteScript("srv/out.txt"), "srv/out.txt", Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.HardLink, PrivilegedFileCommands.ParseTransferRefusal(run.Stderr, run.ExitCode));
        Assert.Equal(Original, area.Read("srv/out.txt"));
        Assert.Equal(Original, area.Read("srv/other.txt"));
        Assert.Empty(area.Leftovers("srv"));
    }

    [PosixHardLinkAndModeFact]
    public async Task Read_AFileWithOtherHardLinksInAFolderOthersCanWriteToIsRefusedAndNothingIsRead()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Original);
        await area.ShellAsync("ln srv/out.txt srv/other.txt && chmod 775 srv");

        Run run = await area.RunAsync(ReadScript("srv/out.txt"), "srv/out.txt", string.Empty);

        Assert.Equal(ReadRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.HardLink, PrivilegedFileCommands.ParseTransferRefusal(run.Stderr, run.ExitCode));
        Assert.Empty(run.Stdout.Trim());
        Assert.Equal(Original, area.Read("srv/out.txt"));
        Assert.Empty(area.Leftovers("srv"));
    }

    [PosixHardLinkAndModeFact]
    public async Task Write_AFileWithOtherHardLinksInAPrivateFolderIsWrittenAndTheOtherNameKeepsTheOldContent()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Original);
        await area.ShellAsync("ln srv/out.txt srv/other.txt");

        Run run = await area.RunAsync(WriteScript("srv/out.txt"), "srv/out.txt", Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("srv/out.txt"));
        Assert.Equal(Original, area.Read("srv/other.txt"));
        Assert.Empty(area.Leftovers("srv"));
    }

    [PosixHardLinkAndModeFact]
    public async Task Read_AFileWithOtherHardLinksInAPrivateFolderIsRead()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Original);
        await area.ShellAsync("ln srv/out.txt srv/other.txt");

        Run run = await area.RunAsync(ReadScript("srv/out.txt"), "srv/out.txt", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Original, Encoding.UTF8.GetString(Convert.FromBase64String(run.Stdout.Trim())));
        Assert.Empty(area.Leftovers("srv"));
    }

    [PosixHardLinkAndModeFact]
    public async Task Read_AFileWithOneLinkInAFolderOthersCanWriteToIsRead()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Original);
        await area.ShellAsync("chmod 775 srv");

        Run run = await area.RunAsync(ReadScript("srv/out.txt"), "srv/out.txt", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Original, Encoding.UTF8.GetString(Convert.FromBase64String(run.Stdout.Trim())));
    }

    // --- Delete ------------------------------------------------------------------------------------

    [PosixLinkAndModeFact]
    public async Task Delete_ThroughALinkTheAccountOwnsRemovesTheEntryInTheFolderItNames()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real real/entry && : > real/entry/file && ln -s real link");

        Run run = await area.RunAsync(DeleteScript("link/entry"), "link/entry", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("real/entry"), "the entry is still there.");
        Assert.True(area.Exists("real"), "the folder that holds the entry was removed.");
    }

    [PosixLinkAndModeFact]
    public async Task Delete_ThroughALinkInAFolderOthersCanWriteToIsRefusedAndTheEntryIsUntouched()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real real/entry sub && : > real/entry/file && ln -s ../real sub/link && chmod 775 sub");

        Run run = await area.RunAsync(DeleteScript("sub/link/entry"), "sub/link/entry", string.Empty);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.PathLink, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.Contains(PrivilegedFileCommands.EntryRefusedMarker, run.Stderr, StringComparison.Ordinal);
        Assert.True(area.Exists("real/entry/file"), "the entry was removed.");
    }

    [PosixShellFact]
    public async Task Delete_OfAFileRemovesOnlyThatFile()
    {
        using var area = new Area();
        area.Put("srv/file.txt", Original);
        area.Put("srv/keep", Original);

        Run run = await area.RunAsync(DeleteScript("srv/file.txt", recursive: false), "srv/file.txt", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("srv/file.txt"), "the file is still there.");
        Assert.True(area.Exists("srv/keep"), "a sibling was removed.");
    }

    [PosixShellFact]
    public async Task Delete_OfAFileRefusesAFolderFoundAtItsNameAndLeavesItUntouched()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir srv/dir && : > srv/dir/inner");

        Run run = await area.RunAsync(DeleteScript("srv/dir", recursive: false), "srv/dir", string.Empty);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.Changed, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.True(area.Exists("srv/dir/inner"), "the folder content was removed.");
    }

    [PosixLinkAndModeFact]
    public async Task Delete_OfAFileThatIsALinkToAFolderRemovesTheLinkOnly()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real && : > real/file && ln -s ../real srv/link");

        Run run = await area.RunAsync(DeleteScript("srv/link", recursive: false), "srv/link", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.False((await area.ShellAsync("[ -L srv/link ] || [ -e srv/link ]")).ExitCode == 0, "the link is still there.");
        Assert.True(area.Exists("real/file"), "what the link points to was removed.");
    }

    [PosixShellFact]
    public async Task Delete_RemovesAFolderWithItsContentFromThePlainPath()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir srv/entry srv/entry/inner && : > srv/entry/inner/file && : > srv/keep");

        Run run = await area.RunAsync(DeleteScript("srv/entry"), "srv/entry", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("srv/entry"), "the folder is still there.");
        Assert.True(area.Exists("srv/keep"), "a sibling was removed.");
    }

    [PosixLinkAndModeFact]
    public async Task Delete_OfALinkRemovesTheLinkAndNotWhatItPointsTo()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real && : > real/file && ln -s ../real srv/link");

        Run run = await area.RunAsync(DeleteScript("srv/link"), "srv/link", string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.False((await area.ShellAsync("[ -L srv/link ] || [ -e srv/link ]")).ExitCode == 0, "the link is still there.");
        Assert.True(area.Exists("real/file"), "what the link points to was removed.");
    }

    [PosixShellTheory]
    [InlineData("with space")]
    [InlineData("-leading-dash")]
    [InlineData("it's quoted")]
    [InlineData("a;b&c$e")]
    public async Task Delete_TakesOddNamesAsDataAndRemovesOnlyThatEntry(string name)
    {
        using var area = new Area();
        area.Put("srv/" + name, Original);
        area.Put("srv/keep", Original);

        Run run = await area.RunAsync(DeleteScript("srv/" + name), "srv/" + name, string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("srv/" + name), "the entry is still there.");
        Assert.True(area.Exists("srv/keep"), "a sibling was removed.");
    }

    [PosixShellTheory]
    [InlineData("srv/")]
    [InlineData("srv/..")]
    [InlineData("srv/.")]
    public async Task Delete_ANameThatIsEmptyOrADotEntryIsRefusedAndNothingIsRemoved(string target)
    {
        using var area = new Area();
        area.Put("srv/keep", Original);

        Run run = await area.RunAsync(DeleteScript(target), target, string.Empty);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.Invalid, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.True(area.Exists("srv/keep"), "an entry was removed.");
    }

    // --- Helpers -----------------------------------------------------------------------------------

    private static string WriteScript(string target)
        => PrivilegedWorkDirectoryTextTests.ScriptOf(PrivilegedFileCommands.BuildAtomicWriteBody(target));

    private static string ReadScript(string target)
        => PrivilegedWorkDirectoryTextTests.ScriptOf(PrivilegedFileCommands.BuildNoFollowBase64ReadBody(target));

    private static string DeleteScript(string target, bool recursive = true)
        => PrivilegedWorkDirectoryTextTests.ScriptOf(PrivilegedFileCommands.BuildHeldDeleteBody(target, recursive));

    // The script takes the mode and the target as $1 and $2; the run passes the target only.
    private static string ChmodScript(string mode)
        => "set -- " + mode + " \"$1\"; "
            + PrivilegedWorkDirectoryTextTests.ScriptOf(PrivilegedFileCommands.BuildNoFollowChmodBody("/x/y", mode));

    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    /// <summary>A temporary directory holding the parent <c>srv</c>; the scripts run with it as their directory.</summary>
    private sealed class Area : IDisposable
    {
        private const int ProcessTimeoutSeconds = 60;

        private const UnixFileMode PrivateFolderMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        private readonly string root;

        public Area()
        {
            root = Path.Combine(Path.GetTempPath(), "heimdall-links-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "srv"));

            // Folder modes do not depend on the umask of the test run.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(root, PrivateFolderMode);
                File.SetUnixFileMode(Path.Combine(root, "srv"), PrivateFolderMode);
            }
        }

        public void Put(string relative, string content)
            => File.WriteAllText(Path.Combine(root, relative), content, new UTF8Encoding(false));

        public string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));

        public bool Exists(string relative)
            => File.Exists(Path.Combine(root, relative)) || Directory.Exists(Path.Combine(root, relative));

        /// <summary>The work directories (and anything else of ours) still in a folder.</summary>
        public string[] Leftovers(string folder)
            => Directory.GetFileSystemEntries(Path.Combine(root, folder), ".heimdall-*");

        /// <summary>Every file below the area with this name, by relative path.</summary>
        public string[] FindAll(string name)
            => Directory.GetFiles(root, name, SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .ToArray();

        public Task<Run> ShellAsync(string script)
            => StartAsync(["-c", "umask 022; " + script], ReadOnlyMemory<byte>.Empty, target: null);

        /// <summary>Runs a script as the remote shell would: "sh -c script sh target".</summary>
        public Task<Run> RunAsync(string script, string target, string input)
            => StartAsync(["-c", script, "sh"], Encoding.UTF8.GetBytes(input), target);

        private async Task<Run> StartAsync(string[] arguments, ReadOnlyMemory<byte> input, string? target)
        {
            var start = new ProcessStartInfo(PosixShellFactAttribute.FindShell()!)
            {
                WorkingDirectory = root,
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

            if (target is not null)
            {
                start.ArgumentList.Add(target);
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
                Directory.Delete(root, recursive: true);
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

/// <summary>Runs only where <c>ln -s</c> makes real links and this shell keeps real permission bits.</summary>
public sealed class PosixLinkAndModeFactAttribute()
    : PosixProbeFactAttribute(
        "ln -s target link && [ -L link ] && mkdir d && chmod 775 d && [ \"$(stat -c %a d)\" = 775 ]",
        "This sh does not create real symbolic links or keep real permission bits.");

/// <summary>Runs only where <c>ln</c> makes hard links that <c>stat</c> counts and permission bits are real.</summary>
public sealed class PosixHardLinkAndModeFactAttribute()
    : PosixProbeFactAttribute(
        "ln target other && [ \"$(stat -c %h target)\" = 2 ] && mkdir d && chmod 775 d && [ \"$(stat -c %a d)\" = 775 ]",
        "This sh does not keep hard link counts or real permission bits.");
