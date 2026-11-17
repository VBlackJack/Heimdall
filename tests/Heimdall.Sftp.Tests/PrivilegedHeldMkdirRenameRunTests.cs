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
/// Runs the create-folder and rename scripts under a real POSIX <c>sh</c>, as the current user, in a
/// temporary directory built by the test. Links and folder modes are created before the script runs
/// and nothing changes while it runs. Cases that need real links or permission bits are skipped,
/// with a reason, where the shell here cannot give them.
/// </summary>
public sealed class PrivilegedHeldMkdirRenameRunTests
{
    private const int RefusedStatus = 73;

    private const int ExistsStatus = 78;

    private const string Content = "entry-content";

    // --- Create folder -----------------------------------------------------------------------------

    [PosixShellFact]
    public async Task Mkdir_CreatesTheFolderInTheParentWithTheModeTheServerGives()
    {
        using var area = new Area();

        Run run = await area.RunAsync(MkdirScript("srv/fresh"), "srv/fresh");

        Assert.Equal(0, run.ExitCode);
        Assert.True(area.IsFolder("srv/fresh"), "the folder was not created.");
        Assert.Equal("755", await area.ModeOfAsync("srv/fresh"));
    }

    [PosixLinkAndModeFact]
    public async Task Mkdir_ThroughALinkTheAccountOwnsCreatesTheFolderWhereTheLinkLeads()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real && ln -s real link");

        Run run = await area.RunAsync(MkdirScript("link/fresh"), "link/fresh");

        Assert.Equal(0, run.ExitCode);
        Assert.True(area.IsFolder("real/fresh"), "the folder was not created where the link leads.");
    }

    [PosixLinkAndModeFact]
    public async Task Mkdir_ThroughALinkInAFolderOthersCanWriteToIsRefusedAndNothingIsCreated()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real sub && ln -s ../real sub/link && chmod 775 sub");

        Run run = await area.RunAsync(MkdirScript("sub/link/fresh"), "sub/link/fresh");

        Assert.Equal(RefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.PathLink, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.False(area.Exists("real/fresh"), "a folder was created.");
    }

    [PosixShellFact]
    public async Task Mkdir_AnExistingFolderOrFileAtTheNameEndsWithStatus78AndIsUntouched()
    {
        using var area = new Area();
        area.Put("srv/file", Content);
        await area.ShellAsync("mkdir srv/dir && : > srv/dir/inner");

        Run onFile = await area.RunAsync(MkdirScript("srv/file"), "srv/file");
        Run onFolder = await area.RunAsync(MkdirScript("srv/dir"), "srv/dir");

        Assert.Equal(ExistsStatus, onFile.ExitCode);
        Assert.Equal(ExistsStatus, onFolder.ExitCode);
        Assert.Equal(Content, area.Read("srv/file"));
        Assert.True(area.Exists("srv/dir/inner"), "the folder was changed.");
    }

    [PosixLinkAndModeFact]
    public async Task Mkdir_ALinkAtTheNameEndsWithStatus78AndWhereItLeadsIsUntouched()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real && ln -s ../real srv/link && ln -s nowhere srv/dangling");

        Run onLink = await area.RunAsync(MkdirScript("srv/link"), "srv/link");
        Run onDangling = await area.RunAsync(MkdirScript("srv/dangling"), "srv/dangling");

        Assert.Equal(ExistsStatus, onLink.ExitCode);
        Assert.Equal(ExistsStatus, onDangling.ExitCode);
        Assert.False(area.Exists("srv/nowhere"), "a folder was created where the link leads.");
        Assert.Empty(area.Entries("real"));
    }

    [PosixShellFact]
    public async Task Mkdir_AMissingParentIsRefusedAsChangedAndNothingIsCreated()
    {
        using var area = new Area();

        Run run = await area.RunAsync(MkdirScript("srv/gone/fresh"), "srv/gone/fresh");

        Assert.Equal(RefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.Changed, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.False(area.Exists("srv/gone"), "a folder was created.");
    }

    [PosixShellTheory]
    [InlineData("srv/..")]
    [InlineData("srv/.")]
    [InlineData("srv/")]
    public async Task Mkdir_AnEmptyOrDotNameIsRefusedAsInvalid(string target)
    {
        using var area = new Area();

        Run run = await area.RunAsync(MkdirScript(target), target);

        Assert.Equal(RefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.Invalid, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.Empty(area.Entries("srv"));
    }

    [PosixShellTheory]
    [InlineData("with space")]
    [InlineData("-leading-dash")]
    [InlineData("it's quoted")]
    [InlineData("a;b&c$e")]
    public async Task Mkdir_TakesOddNamesAsData(string name)
    {
        using var area = new Area();

        Run run = await area.RunAsync(MkdirScript("srv/" + name), "srv/" + name);

        Assert.Equal(0, run.ExitCode);
        Assert.True(area.IsFolder("srv/" + name), "the folder was not created.");
        Assert.Single(area.Entries("srv"));
    }

    // --- Rename ------------------------------------------------------------------------------------

    [PosixShellFact]
    public async Task Rename_OfAFileInTheSameFolderMovesItUnderTheNewName()
    {
        using var area = new Area();
        area.Put("srv/old.txt", Content);
        area.Put("srv/keep", Content);

        Run run = await area.RunAsync(RenameScript("srv/old.txt", "new.txt", replace: false), "srv/old.txt", "new.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("srv/old.txt"), "the old name is still there.");
        Assert.Equal(Content, area.Read("srv/new.txt"));
        Assert.True(area.Exists("srv/keep"), "a sibling was moved.");
    }

    [PosixShellFact]
    public async Task Rename_OfAFolderKeepsItsContent()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir srv/old srv/old/in && : > srv/old/in/file");

        Run run = await area.RunAsync(RenameScript("srv/old", "new", replace: false), "srv/old", "new");

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("srv/old"), "the old name is still there.");
        Assert.True(area.Exists("srv/new/in/file"), "the content did not follow the folder.");
    }

    [PosixShellTheory]
    [InlineData("with space", "to space")]
    [InlineData("-leading-dash", "-other-dash")]
    [InlineData("it's quoted", "it's other")]
    [InlineData("a;b&c$e", "x;y&z$w")]
    [InlineData("a*", "ab")]
    [InlineData("[ab]", "a")]
    public async Task Rename_TakesOddNamesAsDataAndMovesOnlyThatEntry(string from, string to)
    {
        // A name with a wildcard cannot exist on a Windows host folder; the case runs where the file system allows it.
        if (from.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return;
        }

        using var area = new Area();
        area.Put("srv/" + from, Content);
        area.Put("srv/keep", Content);

        Run run = await area.RunAsync(RenameScript("srv/" + from, to, replace: false), "srv/" + from, to);

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("srv/" + from), "the old name is still there.");
        Assert.Equal(Content, area.Read("srv/" + to));
        Assert.True(area.Exists("srv/keep"), "a sibling was moved.");
    }

    [PosixLinkAndModeFact]
    public async Task Rename_OfALinkRenamesTheLinkAndNotWhatItLeadsTo()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real && : > real/file && ln -s ../real srv/link");

        Run run = await area.RunAsync(RenameScript("srv/link", "moved", replace: false), "srv/link", "moved");

        Assert.Equal(0, run.ExitCode);
        Assert.False((await area.ShellAsync("[ -L srv/link ] || [ -e srv/link ]")).ExitCode == 0, "the old link name is still there.");
        Assert.Equal(0, (await area.ShellAsync("[ -L srv/moved ]")).ExitCode);
        Assert.True(area.Exists("real/file"), "what the link leads to was moved.");
    }

    [PosixShellFact]
    public async Task Rename_OntoATakenNameWithoutReplaceEndsWithStatus78AndLeavesBothUntouched()
    {
        using var area = new Area();
        area.Put("srv/old.txt", "old-content");
        area.Put("srv/new.txt", "new-content");

        Run run = await area.RunAsync(RenameScript("srv/old.txt", "new.txt", replace: false), "srv/old.txt", "new.txt");

        Assert.Equal(ExistsStatus, run.ExitCode);
        Assert.Equal("old-content", area.Read("srv/old.txt"));
        Assert.Equal("new-content", area.Read("srv/new.txt"));
    }

    [PosixLinkAndModeFact]
    public async Task Rename_OntoATakenLinkNameWithoutReplaceEndsWithStatus78AndLeavesBothUntouched()
    {
        using var area = new Area();
        area.Put("srv/old.txt", "old-content");
        await area.ShellAsync("ln -s nowhere srv/taken");

        Run run = await area.RunAsync(RenameScript("srv/old.txt", "taken", replace: false), "srv/old.txt", "taken");

        Assert.Equal(ExistsStatus, run.ExitCode);
        Assert.Equal("old-content", area.Read("srv/old.txt"));
        Assert.Equal(0, (await area.ShellAsync("[ -L srv/taken ]")).ExitCode);
    }

    [PosixShellFact]
    public async Task Rename_WithReplaceTakesOverAnExistingFile()
    {
        using var area = new Area();
        area.Put("srv/old.txt", "old-content");
        area.Put("srv/new.txt", "new-content");

        Run run = await area.RunAsync(RenameScript("srv/old.txt", "new.txt", replace: true), "srv/old.txt", "new.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("srv/old.txt"), "the old name is still there.");
        Assert.Equal("old-content", area.Read("srv/new.txt"));
    }

    [PosixShellFact]
    public async Task Rename_WithReplaceOntoANonEmptyFolderFailsAndLeavesBothUntouched()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir srv/old srv/new && : > srv/old/a && : > srv/new/b");

        Run run = await area.RunAsync(RenameScript("srv/old", "new", replace: true), "srv/old", "new");

        Assert.NotEqual(0, run.ExitCode);
        Assert.NotEqual(ExistsStatus, run.ExitCode);
        Assert.True(area.Exists("srv/old/a"), "the source folder was changed.");
        Assert.True(area.Exists("srv/new/b"), "the destination folder was changed.");
        Assert.False(area.Exists("srv/new/a"), "the source went into the destination folder.");
    }

    [PosixLinkAndModeFact]
    public async Task Rename_ThroughALinkTheAccountOwnsRenamesTheEntryWhereTheLinkLeads()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real && ln -s real link");
        area.Put("real/old.txt", Content);

        Run run = await area.RunAsync(RenameScript("link/old.txt", "new.txt", replace: false), "link/old.txt", "new.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.False(area.Exists("real/old.txt"), "the old name is still there.");
        Assert.Equal(Content, area.Read("real/new.txt"));
    }

    [PosixLinkAndModeFact]
    public async Task Rename_ThroughALinkInAFolderOthersCanWriteToIsRefusedAndNothingIsMoved()
    {
        using var area = new Area();
        await area.ShellAsync("mkdir real sub && ln -s ../real sub/link && chmod 775 sub");
        area.Put("real/old.txt", Content);

        Run run = await area.RunAsync(RenameScript("sub/link/old.txt", "new.txt", replace: false), "sub/link/old.txt", "new.txt");

        Assert.Equal(RefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.PathLink, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.Equal(Content, area.Read("real/old.txt"));
        Assert.False(area.Exists("real/new.txt"), "the entry was moved.");
    }

    [PosixShellTheory]
    [InlineData("srv/old.txt", "")]
    [InlineData("srv/old.txt", ".")]
    [InlineData("srv/old.txt", "..")]
    [InlineData("srv/old.txt", "sub/new")]
    [InlineData("srv/old.txt", "old.txt")]
    [InlineData("srv/", "new")]
    [InlineData("srv/..", "new")]
    public async Task Rename_ANameThatIsInvalidOrUnchangedIsRefusedAndNothingIsMoved(string target, string name)
    {
        using var area = new Area();
        area.Put("srv/old.txt", Content);

        // The script is built for a valid pair; the run passes the pair under test as its parameters.
        Run run = await area.RunAsync(RenameScript("srv/old.txt", "valid", replace: false), target, name);

        Assert.Equal(RefusedStatus, run.ExitCode);
        Assert.Equal(EntryRefusalReason.Invalid, PrivilegedFileCommands.ParseEntryRefusalReason(run.Stderr));
        Assert.Equal(Content, area.Read("srv/old.txt"));
        Assert.Single(area.Entries("srv"));
    }

    // --- Helpers -----------------------------------------------------------------------------------

    // The scripts take their data as parameters; "umask 022" first makes the folder modes of the
    // run independent of the umask of the test host.
    private static string MkdirScript(string target)
        => "umask 022; " + PrivilegedWorkDirectoryTextTests.ScriptOf(PrivilegedFileCommands.BuildHeldMkdirBody(target));

    private static string RenameScript(string target, string name, bool replace)
        => "umask 022; " + PrivilegedWorkDirectoryTextTests.ScriptOf(PrivilegedFileCommands.BuildHeldRenameBody(target, name, replace));

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
            root = Path.Combine(Path.GetTempPath(), "heimdall-held-" + Guid.NewGuid().ToString("N"));
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

        public bool IsFolder(string relative) => Directory.Exists(Path.Combine(root, relative));

        public string[] Entries(string folder)
            => Directory.GetFileSystemEntries(Path.Combine(root, folder));

        public async Task<string> ModeOfAsync(string relative)
            => (await ShellAsync("stat -c %a -- '" + relative + "'")).Stdout.Trim();

        public Task<Run> ShellAsync(string script)
            => StartAsync(["-c", "umask 022; " + script], []);

        /// <summary>Runs a script as the remote shell would: "sh -c script sh arguments".</summary>
        public Task<Run> RunAsync(string script, params string[] arguments)
            => StartAsync(["-c", script, "sh"], arguments);

        private async Task<Run> StartAsync(string[] shellArguments, string[] parameters)
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
            foreach (string argument in shellArguments.Concat(parameters))
            {
                start.ArgumentList.Add(argument);
            }

            // The shell's own directory first, so stat, mv and the like resolve even when the shell
            // was found by its install path rather than through PATH.
            start.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                Path.GetDirectoryName(start.FileName),
                Environment.GetEnvironmentVariable("PATH"));

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Close();

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
