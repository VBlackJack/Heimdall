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
/// Runs the commands that reach the remote account's login shell through a real shell, as the
/// current user, in a temporary tree: <c>sh</c> always, and <c>tcsh</c> or <c>zsh</c> where they are
/// installed (a missing one is left out, not reported as passed). Each command is run exactly as
/// the exec channel would hand it over, <c>shell -c command</c>, with paths relative to the tree.
/// </summary>
public sealed class LoginShellCommandRunTests
{
    private const string SymlinkStatusMismatch = "a published symbolic link must exit with the named status";

    /// <summary>The shells found on this machine, as theory data.</summary>
    public static IEnumerable<object[]> Shells => LoginShells.Available().Select(name => new object[] { name });

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task Publish_LinksTheStagingFileAndRemovesIt(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/f.part", "new");

        ShellRun run = await tree.RunAsync(
            shell,
            ServerSideNoClobberPublishCommand.BuildFilePublish("srv/f.part", "srv/f"));

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Equal("new", tree.Read("srv/f"));
        Assert.False(tree.Exists("srv/f.part"), "the staging path must be removed after a publication");
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task Publish_RefusesAnExistingDestination_AndStillRemovesTheStagingPath(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/f", "old");
        tree.Write("srv/f.part", "new");

        ShellRun run = await tree.RunAsync(
            shell,
            ServerSideNoClobberPublishCommand.BuildFilePublish("srv/f.part", "srv/f"));

        Assert.True(run.ExitCode != 0, run.Describe());
        Assert.Equal("old", tree.Read("srv/f"));
        Assert.False(tree.Exists("srv/f.part"), "a refused publication still discards the staging path");
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task Publish_HandlesAQuoteASpaceAndABangInTheNames(string shell)
    {
        using LoginShellTree tree = new();
        const string Destination = "srv/it's a!file";
        tree.Write(Destination + ".part", "new");

        ShellRun run = await tree.RunAsync(
            shell,
            ServerSideNoClobberPublishCommand.BuildFilePublish(Destination + ".part", Destination));

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Equal("new", tree.Read(Destination));
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task CopyFile_PublishesTheCopyAndLeavesNoStagingFile(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/a.txt", "payload");

        ShellRun run = await tree.RunAsync(shell, ServerSideCopyCommand.Build("srv/a.txt", "srv/b.txt", recursive: false));

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Equal("payload", tree.Read("srv/b.txt"));
        Assert.Equal("payload", tree.Read("srv/a.txt"));
        Assert.Empty(tree.Entries("srv", "*.part"));
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task CopyFile_RefusesAnExistingDestination_AndRemovesItsOwnStagingFile(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/a.txt", "payload");
        tree.Write("srv/b.txt", "kept");

        ShellRun run = await tree.RunAsync(shell, ServerSideCopyCommand.Build("srv/a.txt", "srv/b.txt", recursive: false));

        Assert.True(run.ExitCode != 0, run.Describe());
        Assert.Equal("kept", tree.Read("srv/b.txt"));
        Assert.Empty(tree.Entries("srv", "*.part"));
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task CopyFile_HandlesAQuoteASpaceAndABangInTheNames(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/my dir/it's a!file.txt", "payload");

        ShellRun run = await tree.RunAsync(
            shell,
            ServerSideCopyCommand.Build("srv/my dir/it's a!file.txt", "srv/o'brien!", recursive: false));

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Equal("payload", tree.Read("srv/o'brien!"));
    }

    [PosixSymlinkFact]
    public async Task CopyFile_ADestinationThatCameOutASymlink_IsRemovedAndReportedWithTheNamedStatus()
    {
        foreach (string shell in LoginShells.Available())
        {
            using LoginShellTree tree = new();
            tree.Write("srv/a.txt", "payload");
            tree.Write("srv/evil", "elsewhere");

            // A cp that, once it has copied, swaps its target for a symbolic link: what a staging file
            // replaced between the copy and the link looks like to the chain.
            string shimDirectory = tree.CreateScriptDirectory(
                "cp",
                "#!/bin/sh\n"
                + "PATH=$HEIMDALL_REAL_PATH\n"
                + "cp \"$@\" || exit $?\n"
                + "for last; do :; done\n"
                + "rm -f -- \"$last\"\n"
                + "ln -s \"$PWD/srv/evil\" \"$last\"\n");

            ShellRun run = await tree.RunAsync(
                shell,
                ServerSideCopyCommand.Build("srv/a.txt", "srv/b.txt", recursive: false),
                prependPath: shimDirectory);

            Assert.True(run.ExitCode == ServerSideCopyCommand.PublishedASymlinkStatus, $"{shell}: {SymlinkStatusMismatch}. {run.Describe()}");
            Assert.False(tree.Exists("srv/b.txt"), $"{shell}: the published link must be removed");
            Assert.Equal("elsewhere", tree.Read("srv/evil"));
            Assert.Empty(tree.Entries("srv", "*.part"));
        }
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task CopyDirectory_CopiesTheTree(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/data/one.txt", "1");
        tree.Write("srv/data/sub/two.txt", "2");

        ShellRun run = await tree.RunAsync(shell, ServerSideCopyCommand.Build("srv/data", "srv/copy", recursive: true));

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.Equal("1", tree.Read("srv/copy/one.txt"));
        Assert.Equal("2", tree.Read("srv/copy/sub/two.txt"));
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task CopyDirectory_RefusesAnExistingDestination_AndLeavesItAlone(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/data/one.txt", "1");
        tree.Write("srv/copy/mine.txt", "kept");

        ShellRun run = await tree.RunAsync(shell, ServerSideCopyCommand.Build("srv/data", "srv/copy", recursive: true));

        Assert.True(run.ExitCode != 0, run.Describe());
        Assert.Equal("kept", tree.Read("srv/copy/mine.txt"));
        Assert.False(tree.Exists("srv/copy/one.txt"));
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task CopyDirectory_FailedCopy_RemovesTheReservedRoot(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/data/one.txt", "1");

        // A cp that always fails, so the reservation succeeded and the copy did not.
        string shimDirectory = tree.CreateScriptDirectory("cp", "#!/bin/sh\n: > shim-ran\nexit 7\n");

        ShellRun run = await tree.RunAsync(
            shell,
            ServerSideCopyCommand.Build("srv/data", "srv/copy", recursive: true),
            prependPath: shimDirectory);

        Assert.True(tree.Exists("shim-ran"), $"the cp stand-in did not run, so this case proves nothing here. {run.Describe()}");
        Assert.True(run.ExitCode == 7, run.Describe());
        Assert.False(tree.Exists("srv/copy"), "the reserved root must be removed when the copy fails");
        Assert.Equal("1", tree.Read("srv/data/one.txt"));
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task DirectoryReservation_CreatesOnce_AndRefusesTheSecondTime(string shell)
    {
        using LoginShellTree tree = new();
        string command = ServerSideNoClobberPublishCommand.BuildDirectoryReservation("srv/it's a!dir");

        ShellRun first = await tree.RunAsync(shell, command);
        ShellRun second = await tree.RunAsync(shell, command);

        Assert.True(first.ExitCode == 0, first.Describe());
        Assert.True(tree.Exists("srv/it's a!dir"));
        Assert.True(second.ExitCode != 0, second.Describe());
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task Delete_RemovesTheTree_IncludingAnAwkwardName(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/it's a!tree/one.txt", "1");
        tree.Write("srv/it's a!tree/sub/two.txt", "2");
        tree.Write("srv/keep.txt", "keep");

        ShellRun run = await tree.RunAsync(shell, RemoteDeleteCommand.Build("srv/it's a!tree"));

        Assert.True(run.ExitCode == 0, run.Describe());
        Assert.False(tree.Exists("srv/it's a!tree"));
        Assert.Equal("keep", tree.Read("srv/keep.txt"));
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task Preflight_ReportsAnAbsentTarget_AndAgreesWithShForAPresentOne(string shell)
    {
        using LoginShellTree tree = new();
        tree.Write("srv/present.txt", "x");

        ShellRun absent = await tree.RunAsync(shell, SftpMetadataPreflight.Build("srv/absent.txt"));
        ShellRun present = await tree.RunAsync(shell, SftpMetadataPreflight.Build("srv/present.txt"));
        ShellRun reference = await tree.RunAsync(LoginShells.PosixShellName, SftpMetadataPreflight.Build("srv/present.txt"));

        Assert.True(absent.ExitCode == SftpMetadataPreflight.NoExistingTargetStatus, absent.Describe());
        Assert.True(present.ExitCode == reference.ExitCode, $"{shell}: {present.Describe()} versus sh: {reference.Describe()}");
        Assert.InRange(present.ExitCode, 0, SftpMetadataPreflight.OwnershipStatus);
    }

    [PosixShellTheory]
    [MemberData(nameof(Shells))]
    public async Task Preflight_ParsesAnAwkwardNameLikeSh(string shell)
    {
        using LoginShellTree tree = new();

        ShellRun absent = await tree.RunAsync(shell, SftpMetadataPreflight.Build("srv/it's a!name"));

        Assert.True(absent.ExitCode == SftpMetadataPreflight.NoExistingTargetStatus, absent.Describe());
    }
}

/// <summary>
/// The privileged root listing as sudo runs it under a restrictive policy: a stand-in sudo that
/// refuses any <c>VAR=value</c> argument, allows <c>find</c> only and drops the locale variables
/// before it executes, with a French locale in the caller's own environment.
/// </summary>
public sealed class SudoListingPolicyRunTests
{
    private const string FrenchLocale = "fr_FR.UTF-8";

    // Written with explicit line feeds: a CRLF script would not run under sh.
    private const string RestrictedSudo =
        "#!/bin/sh\n"
        + "for argument in \"$@\"; do\n"
        + "  case \"$argument\" in\n"
        + "    [A-Za-z_]*=*) echo \"sudo: sorry, you are not allowed to set the following environment variables: ${argument%%=*}\" >&2; exit 1 ;;\n"
        + "  esac\n"
        + "done\n"
        + "case \"$1\" in\n"
        + "  find) ;;\n"
        + "  *) echo \"sudo: Sorry, user is not allowed to execute '$*' as root\" >&2; exit 1 ;;\n"
        + "esac\n"
        + "unset LC_ALL LANG LANGUAGE\n"
        + "exec \"$@\"\n";

    [GnuFindFact]
    public async Task Listing_RunsUnderASudoRuleLimitedToFind_AndParsesFileFolderAndLink()
    {
        using LoginShellTree tree = new();
        tree.Write("srv/file.txt", "content");
        Directory.CreateDirectory(Path.Combine(tree.Root, "srv", "folder"));
        bool hasLink = tree.TryCreateSymbolicLink("srv/link", "file.txt");

        string sudoDirectory = tree.CreateScriptDirectory("sudo", RestrictedSudo);
        string findDirectory = GnuFindFactAttribute.FindDirectory()!;
        string listing = SudoDirectoryListing.Build("srv");
        Dictionary<string, string> environment = new()
        {
            ["LC_ALL"] = FrenchLocale,
            ["LANG"] = FrenchLocale,
        };

        // The old form is what the stand-in must refuse, or the test below proves nothing.
        ShellRun refused = await tree.RunAsync(
            LoginShells.PosixShellName,
            $"sudo LC_ALL=C find -H './srv' -mindepth 1 -maxdepth 1 -printf x",
            environment,
            prependPath: sudoDirectory + Path.PathSeparator + findDirectory);
        Assert.True(refused.ExitCode != 0, refused.Describe());

        ShellRun run = await tree.RunAsync(
            LoginShells.PosixShellName,
            $"{PrivilegedFileCommands.SudoProgram} {listing}",
            environment,
            prependPath: sudoDirectory + Path.PathSeparator + findDirectory);

        Assert.True(run.ExitCode == 0, run.Describe());
        IReadOnlyList<SftpFileInfo> entries = SudoDirectoryListing.Parse(run.StandardOutput, "srv");

        Assert.Equal(hasLink ? 3 : 2, entries.Count);
        Assert.Equal(RemoteEntryKind.File, entries.Single(entry => entry.Name == "file.txt").Kind);
        Assert.Equal(RemoteEntryKind.Directory, entries.Single(entry => entry.Name == "folder").Kind);
        if (hasLink)
        {
            Assert.Equal(RemoteEntryKind.SymbolicLink, entries.Single(entry => entry.Name == "link").Kind);
        }
    }
}

/// <summary>What a shell run printed and how it ended.</summary>
internal sealed record ShellRun(int ExitCode, string StandardOutput, string StandardError)
{
    public string Describe() => $"exit {ExitCode}; stdout: {StandardOutput}; stderr: {StandardError}";
}

/// <summary>The shells a login-shell test can use on this machine.</summary>
internal static class LoginShells
{
    internal const string PosixShellName = "sh";

    private static readonly string[] OptionalShells = ["tcsh", "zsh"];

    /// <summary>The names usable here: <c>sh</c> when found, then <c>tcsh</c> and <c>zsh</c> when installed.</summary>
    internal static IReadOnlyList<string> Available()
    {
        List<string> names = [];
        if (PosixShellLocator.Find() is not null)
        {
            names.Add(PosixShellName);
        }

        foreach (string name in OptionalShells)
        {
            if (FindOnPath(name) is not null)
            {
                names.Add(name);
            }
        }

        return names;
    }

    internal static string? Resolve(string name)
        => name == PosixShellName ? PreferUsrBin(PosixShellLocator.Find()) : FindOnPath(name);

    /// <summary>
    /// Git for Windows ships a bin\sh.exe launcher that can put usr\bin ahead of the inherited
    /// PATH; its sibling usr\bin\sh.exe keeps PATH as given.
    /// </summary>
    private static string? PreferUsrBin(string? shell)
    {
        if (shell is null || !OperatingSystem.IsWindows())
        {
            return shell;
        }

        string sibling = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shell)!, "..", "usr", "bin", "sh.exe"));
        return File.Exists(sibling) ? sibling : shell;
    }

    /// <summary>The arguments that make the shell run one command without reading user start-up files.</summary>
    internal static string[] Arguments(string name, string command)
        => name == PosixShellName ? ["-c", command] : ["-f", "-c", command];

    internal static string? FindOnPath(string name)
    {
        string executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>A temporary tree and the means to run one command in it through a named shell.</summary>
internal sealed class LoginShellTree : IDisposable
{
    private const int ProcessTimeoutSeconds = 60;

    public LoginShellTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "heimdall-loginshell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "srv"));
    }

    public string Root { get; }

    public void Write(string relative, string content)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    public bool Exists(string relative)
    {
        string path = Path.Combine(Root, relative);
        return File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
    }

    public string[] Entries(string relative, string pattern)
        => Directory.GetFileSystemEntries(Path.Combine(Root, relative), pattern);

    public bool TryCreateSymbolicLink(string relative, string target)
    {
        try
        {
            File.CreateSymbolicLink(Path.Combine(Root, relative), target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Creates a directory holding one executable script and returns it.</summary>
    public string CreateScriptDirectory(string name, string script)
    {
        string directory = Path.Combine(Root, "bin-" + name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), script, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Path.Combine(directory, name),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return directory;
    }

    public async Task<ShellRun> RunAsync(
        string shell,
        string command,
        IReadOnlyDictionary<string, string>? environment = null,
        string? prependPath = null)
    {
        string shellPath = LoginShells.Resolve(shell) ?? throw new InvalidOperationException($"{shell} was not found.");
        ProcessStartInfo start = new(shellPath)
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in LoginShells.Arguments(shell, command))
        {
            start.ArgumentList.Add(argument);
        }

        // The POSIX shell's own directory next, so the nested sh the commands start and the
        // coreutils resolve even when the shell was found by its install path.
        string originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string shellDirectory = Path.GetDirectoryName(LoginShells.Resolve(LoginShells.PosixShellName)!)!;
        List<string> segments = [];
        if (!string.IsNullOrEmpty(prependPath))
        {
            segments.Add(prependPath);
        }

        segments.Add(shellDirectory);
        segments.Add(originalPath);
        start.Environment["PATH"] = string.Join(Path.PathSeparator, segments);
        start.Environment["HEIMDALL_REAL_PATH"] = string.Join(Path.PathSeparator, shellDirectory, originalPath);
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                start.Environment[key] = value;
            }
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(ProcessTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{shell} did not finish within {ProcessTimeoutSeconds} seconds: {command}");
        }

        return new ShellRun(process.ExitCode, await stdout, await stderr);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}

/// <summary>A fact that runs only where a POSIX <c>sh</c> and a GNU <c>find</c> can be found.</summary>
public sealed class GnuFindFactAttribute : FactAttribute
{
    public GnuFindFactAttribute()
    {
        if (PosixShellLocator.Find() is null)
        {
            Skip = "No POSIX sh was found on PATH.";
        }
        else if (FindDirectory() is null)
        {
            Skip = "No GNU find was found next to sh or on PATH.";
        }
    }

    /// <summary>
    /// The directory of a GNU <c>find</c>: next to the shell first, then its sibling <c>usr/bin</c>
    /// (Git for Windows), then PATH. The Windows system <c>find.exe</c> is a different program.
    /// </summary>
    internal static string? FindDirectory()
    {
        string? shell = PosixShellLocator.Find();
        List<string> directories = [];
        if (shell is not null)
        {
            string shellDirectory = Path.GetDirectoryName(shell)!;
            directories.Add(shellDirectory);
            directories.Add(Path.GetFullPath(Path.Combine(shellDirectory, "..", "usr", "bin")));
        }

        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        string executable = OperatingSystem.IsWindows() ? "find.exe" : "find";
        foreach (string directory in directories)
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate) && IsGnuFind(candidate))
            {
                return directory;
            }
        }

        return null;
    }

    private static bool IsGnuFind(string path)
    {
        try
        {
            ProcessStartInfo start = new(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return output.Contains("GNU findutils", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
