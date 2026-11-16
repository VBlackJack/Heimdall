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
/// Runs the bodies of the privileged write and read under a real POSIX <c>sh</c>, as the current
/// user, in a temporary directory. A shell function named <c>mktemp</c> placed in front of the
/// script makes the work directory a different one from the directory just created, which is how
/// the check on it is exercised without any timing. Cases that need real permission bits, real
/// symbolic links or signals are skipped, with a reason, where the shell here cannot give them.
/// </summary>
public sealed class PrivilegedWorkDirectoryRunTests
{
    private const int WriteRefusedStatus = 73;

    private const int ReadRefusedStatus = 74;

    private const int ExistingDestinationStatus = 78;

    private const int SignalStatus = 129;

    private const string Reference = "reference-content";

    private const string Payload = "new-content";

    // The mode of the replacement directory, as one of the cases of the function below.
    private const string ModeNone = "none";

    private const string ModeReplacedDirectory = "replaced-directory";

    private const string ModeReplacedLink = "replaced-link";

    private const string ModeOtherOwner = "other-owner";

    private const string ModeOpenDirectory = "open-directory";

    private const string ModeParentChanged = "parent-changed";

    private const string ModeSignal = "signal";

    private const string ModeEmptyLink = "empty-link";

    private const string ModeClaimTaken = "claim-taken";

    private const string ModeRelativePath = "relative-path";

    // Defined in front of the script. "command mktemp" reaches the real one, so the directory is
    // really created; the case then makes it look like another one, as a directory put in its
    // place would. The lines holding the other cases change what the script is told about the
    // account, about the held parent, or send the script a signal at a fixed point.
    private const string Preamble =
        "root=$PWD; "
        + "mktemp() { made_by_test=$(command mktemp \"$@\") || return 1; "
        + "case \"$HEIMDALL_TEST_MODE\" in "
        + "replaced-directory) rmdir \"$made_by_test\"; mkdir \"$made_by_test\"; "
        + "printf '%s' '" + Reference + "' > \"$made_by_test/payload\"; : > \"$made_by_test/leftover\" ;; "
        + "replaced-link) rmdir \"$made_by_test\"; mkdir \"$root/elsewhere\"; "
        + "ln -s \"$root/outside/reference\" \"$root/elsewhere/payload\"; ln -s \"$root/elsewhere\" \"$made_by_test\" ;; "
        + "empty-link) rmdir \"$made_by_test\"; mkdir \"$root/quiet\"; chmod 700 \"$root/quiet\"; "
        + "ln -s \"$root/quiet\" \"$made_by_test\" ;; "
        + "open-directory) chmod 777 \"$made_by_test\" ;; "
        + "esac; printf '%s\\n' \"$made_by_test\"; }; "
        + "case \"$HEIMDALL_TEST_MODE\" in "
        + "other-owner) id() { printf '%s\\n' 4242; } ;; "
        + "parent-changed) sync() { changed_by_test=1; command sync \"$@\"; }; "
        + "stat() { if [ \"${changed_by_test:-}\" = 1 ] && [ \"$#\" -eq 3 ] && [ \"$3\" = .. ]; "
        + "then printf '%s\\n' 0:0; else command stat \"$@\"; fi; } ;; "
        + "signal) sync() { kill -TERM $$; command sync \"$@\"; } ;; "
        + "claim-taken) mkdir() { if [ \"$1\" = -- ] && [ \"$2\" = claim ]; then return 1; fi; command mkdir \"$@\"; } ;; "
        + "relative-path) PATH=\"..::$PATH\" ;; "
        + "esac; ";

    private static string WriteScript(bool overwrite)
        => PrivilegedWorkDirectoryTextTests.ScriptOf(
            PrivilegedFileCommands.BuildAtomicWriteBody("srv/out.txt", overwrite));

    private static string ReadScript()
        => PrivilegedWorkDirectoryTextTests.ScriptOf(
            PrivilegedFileCommands.BuildNoFollowBase64ReadBody("srv/out.txt"));

    // --- Write ----------------------------------------------------------------------------------

    [PosixShellFact]
    public async Task Write_ANewFileLandsWithItsContentAndNothingIsLeftBehind()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeNone, Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("srv/out.txt"));
        Assert.Empty(area.Leftovers());
    }

    [PosixAttributeCopyFact]
    public async Task Write_AnExistingFileIsReplacedWithItsNewContent()
    {
        using var area = new Area();
        area.Put("srv/out.txt", "old-content");

        Run run = await area.RunAsync(WriteScript(true), ModeNone, Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("srv/out.txt"));
        Assert.Empty(area.Leftovers());
    }

    [PosixShellFact]
    public async Task Write_AnExistingFileIsKeptWhenReplacingIsNotAllowed()
    {
        using var area = new Area();
        area.Put("srv/out.txt", "old-content");

        Run run = await area.RunAsync(WriteScript(false), ModeNone, Payload);

        Assert.Equal(ExistingDestinationStatus, run.ExitCode);
        Assert.Equal("old-content", area.Read("srv/out.txt"));
        Assert.Empty(area.Leftovers());
    }

    [PosixAttributeCopyAndModeBitsFact]
    public async Task Write_ReplacingAFileKeepsItsMode()
    {
        using var area = new Area();
        area.Put("srv/out.txt", "old-content");
        await area.ShellAsync("chmod 640 srv/out.txt");

        Run run = await area.RunAsync(WriteScript(true), ModeNone, Payload);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("640", (await area.ShellAsync("stat -c %a srv/out.txt")).Stdout.Trim());
    }

    [PosixSymlinkFact]
    public async Task Write_ADestinationThatIsASymbolicLinkIsRefusedAndWhatItPointsToIsUntouched()
    {
        using var area = new Area();
        await area.ShellAsync("ln -s \"$PWD/outside/reference\" srv/out.txt");

        Run run = await area.RunAsync(WriteScript(true), ModeNone, Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(Reference, area.Read("outside/reference"));
        Assert.Equal(0, (await area.ShellAsync("[ -L srv/out.txt ]")).ExitCode);
        Assert.Empty(area.Leftovers());
    }

    [PosixShellFact]
    public async Task Write_AWorkDirectoryThatIsNotTheOneJustCreatedIsRefusedAndNothingInItIsTouched()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeReplacedDirectory, Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.False(area.Exists("srv/out.txt"), "the destination was written.");

        // The directory in place of ours is not ours to clean: its content stays as it was.
        string other = area.OnlyWorkDirectory();
        Assert.Equal(Reference, area.Read("srv/" + other + "/payload"));
        Assert.True(area.Exists("srv/" + other + "/leftover"));
    }

    [PosixShellFact]
    public async Task Write_AWorkDirectoryThatIsNotEmptyIsRefusedWhateverItHolds()
    {
        using var area = new Area();
        area.Put("srv/out.txt", "old-content");

        Run run = await area.RunAsync(WriteScript(true), ModeReplacedDirectory, Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal("old-content", area.Read("srv/out.txt"));
        Assert.Contains("not empty", run.Stderr, StringComparison.Ordinal);
    }

    [PosixShellFact]
    public async Task Write_AWorkDirectoryOfAnotherAccountIsRefused()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeOtherOwner, Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.False(area.Exists("srv/out.txt"), "the destination was written.");
        Assert.Contains("not private", run.Stderr, StringComparison.Ordinal);
    }

    [PosixModeBitsFact]
    public async Task Write_AWorkDirectoryOpenToOthersIsRefused()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeOpenDirectory, Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.False(area.Exists("srv/out.txt"), "the destination was written.");
        Assert.Contains("not private", run.Stderr, StringComparison.Ordinal);
    }

    [PosixSymlinkFact]
    public async Task Write_AWorkDirectoryReplacedByALinkToAnotherDirectoryIsRefusedAndTheFileItReachesIsUntouched()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeReplacedLink, Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Equal(Reference, area.Read("outside/reference"));
        Assert.False(area.Exists("srv/out.txt"), "the destination was written.");
    }

    [PosixSymlinkFact]
    public async Task Write_AWorkDirectoryReplacedByALinkToAnEmptyPrivateDirectoryIsRefusedByItsParent()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeEmptyLink, Payload);

        // The directory reached is owned by the account, private and empty: only the check of its
        // parent can refuse it.
        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Contains("could not be verified", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("not empty", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("not private", run.Stderr, StringComparison.Ordinal);
        Assert.False(area.Exists("srv/out.txt"), "the destination was written.");
        Assert.True(area.Exists("quiet"), "the directory reached was removed.");
    }

    [PosixShellFact]
    public async Task Write_AWorkDirectoryAnotherRunHoldsIsRefusedAndNothingIsPublished()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeClaimTaken, Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Contains("in use", run.Stderr, StringComparison.Ordinal);
        Assert.False(area.Exists("srv/out.txt"), "the destination was written.");
    }

    [PosixShellFact]
    public async Task Write_AFileNamedLikeAToolInARelativeSearchPathEntryIsNeverRun()
    {
        using var area = new Area();
        area.Put("srv/stat", "#!/bin/sh\n: > \"$(dirname \"$0\")/tool-ran\"\nexit 1\n");
        await area.ShellAsync("chmod +x srv/stat");

        Run run = await area.RunAsync(WriteScript(true), ModeRelativePath, Payload);

        Assert.False(area.Exists("srv/tool-ran"), "a file found through a relative entry was run.");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, area.Read("srv/out.txt"));
    }

    [PosixShellTheory]
    [InlineData("srv/")]
    [InlineData("srv/..")]
    [InlineData("srv/.")]
    public async Task Write_ATargetWithNoEntryNameIsRefused(string target)
    {
        using var area = new Area();
        string script = PrivilegedWorkDirectoryTextTests.ScriptOf(PrivilegedFileCommands.BuildAtomicWriteBody(target));

        Run run = await area.RunAsync(script, ModeNone, Payload, target);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Contains("no entry name", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(area.Leftovers());
    }

    [PosixShellFact]
    public async Task Write_AParentThatIsNotTheHeldOneRightBeforeThePublicationIsRefusedAndNothingIsPublished()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeParentChanged, Payload);

        Assert.Equal(WriteRefusedStatus, run.ExitCode);
        Assert.Contains("changed during the write", run.Stderr, StringComparison.Ordinal);
        Assert.False(area.Exists("srv/out.txt"), "the destination was published.");
        Assert.Empty(area.Leftovers());
    }

    [PosixSignalFact]
    public async Task Write_ASignalEndsTheScriptAfterItsCleanup()
    {
        using var area = new Area();

        Run run = await area.RunAsync(WriteScript(true), ModeSignal, Payload);

        Assert.Equal(SignalStatus, run.ExitCode);
        Assert.False(area.Exists("srv/out.txt"), "the script went on after the signal.");
        Assert.Empty(area.Leftovers());
    }

    // --- Read -----------------------------------------------------------------------------------

    [PosixShellFact]
    public async Task Read_ReturnsTheFileAndLeavesNothingBehind()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Payload);

        Run run = await area.RunAsync(ReadScript(), ModeNone, string.Empty);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Payload, Encoding.UTF8.GetString(Convert.FromBase64String(run.Stdout.Trim())));
        Assert.Empty(area.Leftovers());
    }

    [PosixShellFact]
    public async Task Read_AWorkDirectoryThatIsNotEmptyIsRefusedAndNothingIsRead()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Payload);

        Run run = await area.RunAsync(ReadScript(), ModeReplacedDirectory, string.Empty);

        Assert.Equal(ReadRefusedStatus, run.ExitCode);
        Assert.Empty(run.Stdout.Trim());
        Assert.Contains("not empty", run.Stderr, StringComparison.Ordinal);
    }

    [PosixShellFact]
    public async Task Read_AWorkDirectoryOfAnotherAccountIsRefusedAndNothingIsRead()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Payload);

        Run run = await area.RunAsync(ReadScript(), ModeOtherOwner, string.Empty);

        Assert.Equal(ReadRefusedStatus, run.ExitCode);
        Assert.Empty(run.Stdout.Trim());
    }

    [PosixSymlinkFact]
    public async Task Read_AWorkDirectoryReplacedByALinkToAnotherDirectoryIsRefusedAndNothingIsRead()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Payload);

        Run run = await area.RunAsync(ReadScript(), ModeReplacedLink, string.Empty);

        Assert.Equal(ReadRefusedStatus, run.ExitCode);
        Assert.Empty(run.Stdout.Trim());
    }

    [PosixSymlinkFact]
    public async Task Read_AWorkDirectoryReplacedByALinkToAnEmptyPrivateDirectoryIsRefusedByItsParent()
    {
        using var area = new Area();
        area.Put("srv/out.txt", Payload);

        Run run = await area.RunAsync(ReadScript(), ModeEmptyLink, string.Empty);

        Assert.Equal(ReadRefusedStatus, run.ExitCode);
        Assert.Contains("could not be verified", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Stdout.Trim());
    }

    [PosixSymlinkFact]
    public async Task Read_ASourceThatIsASymbolicLinkIsRefusedAndNothingIsRead()
    {
        using var area = new Area();
        await area.ShellAsync("ln -s \"$PWD/outside/reference\" srv/out.txt");

        Run run = await area.RunAsync(ReadScript(), ModeNone, string.Empty);

        Assert.Equal(ReadRefusedStatus, run.ExitCode);
        Assert.Empty(run.Stdout.Trim());
        Assert.Empty(area.Leftovers());
    }

    [PosixShellFact]
    public async Task Read_AMissingSourceIsRefusedAndNothingIsLeftBehind()
    {
        using var area = new Area();

        Run run = await area.RunAsync(ReadScript(), ModeNone, string.Empty);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(run.Stdout.Trim());
        Assert.Empty(area.Leftovers());
    }

    // --- Runner ---------------------------------------------------------------------------------

    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    /// <summary>A temporary directory holding the parent <c>srv</c> and a reference file outside it.</summary>
    private sealed class Area : IDisposable
    {
        private const int ProcessTimeoutSeconds = 60;

        private readonly string root;

        public Area()
        {
            root = Path.Combine(Path.GetTempPath(), "heimdall-workdir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "srv"));
            Directory.CreateDirectory(Path.Combine(root, "outside"));
            Put("outside/reference", Reference);
        }

        public void Put(string relative, string content)
            => File.WriteAllText(Path.Combine(root, relative), content, new UTF8Encoding(false));

        public string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));

        public bool Exists(string relative)
            => File.Exists(Path.Combine(root, relative)) || Directory.Exists(Path.Combine(root, relative));

        /// <summary>The work directories (and anything else of ours) still in the parent.</summary>
        public string[] Leftovers()
            => Directory.GetFileSystemEntries(Path.Combine(root, "srv"), ".heimdall-*");

        public string OnlyWorkDirectory()
        {
            string[] entries = Leftovers();
            Assert.Single(entries);
            return Path.GetFileName(entries[0]);
        }

        public Task<Run> ShellAsync(string script) => StartAsync(["-c", script], ModeNone, ReadOnlyMemory<byte>.Empty);

        /// <summary>Runs a script as the remote shell would: "sh -c script sh target".</summary>
        public Task<Run> RunAsync(string script, string mode, string input, string target = "srv/out.txt")
            => StartAsync(["-c", Preamble + script, "sh", target], mode, Encoding.UTF8.GetBytes(input));

        private async Task<Run> StartAsync(string[] arguments, string mode, ReadOnlyMemory<byte> input)
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

            // The shell's own directory first, so cat, stat and the like resolve even when the
            // shell was found by its install path rather than through PATH.
            start.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                Path.GetDirectoryName(start.FileName),
                Environment.GetEnvironmentVariable("PATH"));
            start.Environment["HEIMDALL_TEST_MODE"] = mode;

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

/// <summary>Runs only where <c>chmod</c> and <c>stat</c> of this shell read real permission bits.</summary>
public sealed class PosixModeBitsFactAttribute()
    : PosixProbeFactAttribute(
        "chmod 640 target && [ \"$(stat -c %a target)\" = 640 ] && mkdir d && chmod 777 d && [ \"$(stat -c %a d)\" = 777 ]",
        "This sh does not keep real permission bits.");

/// <summary>Runs only where a signal sent by a script to itself reaches the script's own trap.</summary>
public sealed class PosixSignalFactAttribute()
    : PosixProbeFactAttribute(
        "trap 'exit 0' TERM; kill -TERM $$; exit 3",
        "This sh does not deliver a signal sent to itself to its trap.");

/// <summary>
/// Runs only where this shell's <c>cp</c> can carry the attributes the script copies from the
/// replaced file (Git for Windows builds <c>cp</c> without extended attribute support).
/// </summary>
public sealed class PosixAttributeCopyFactAttribute()
    : PosixProbeFactAttribute(
        ": > copy && cp --attributes-only --preserve=mode,ownership,timestamps,xattr -- target copy",
        "The cp of this sh cannot copy the extended attributes of a file.");

/// <summary>The attribute copy of this shell works and its permission bits are real.</summary>
public sealed class PosixAttributeCopyAndModeBitsFactAttribute()
    : PosixProbeFactAttribute(
        ": > copy && cp --attributes-only --preserve=mode,ownership,timestamps,xattr -- target copy "
        + "&& chmod 640 target && [ \"$(stat -c %a target)\" = 640 ]",
        "This sh cannot copy the extended attributes or does not keep real permission bits.");
