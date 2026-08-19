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
using Microsoft.Win32.SafeHandles;

namespace Heimdall.App.Tests;

/// <summary>
/// SSH-013. Whether a launcher's first byte proves it already read its password file is a property
/// of that exact build, so it is decided on the executable's bytes - and held to the image that is
/// actually launched, not merely to the bytes that were there when the question was asked.
/// </summary>
/// <remarks>
/// <para>Hashing alone left an unbounded window: the handler can wait on a password dialog between
/// the hash and the launch, and a legitimate update replacing the executable in that window would
/// have handed an unmeasured build the previous verdict. The attestation therefore keeps the file
/// open while it matters, denying writes and deletion.</para>
/// <para>Every experiment below runs on a temporary copy. The tracked asset is only ever read.</para>
/// <para>These oracles claim no resistance to a hostile executable: Heimdall hands the password to
/// whatever it was pointed at. What is pinned is that a measured build's timing conclusion is not
/// applied to a different image.</para>
/// </remarks>
public sealed class PlinkCompatibilityAttestationTests
{
    [Fact]
    public void TheShippedLauncher_IsAttested()
    {
        using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(ShippedLauncherPath());

        // If this ever fails, the shipped binary was replaced without re-measuring its -pwfile
        // timing, and the recorded identity no longer describes what actually runs.
        Assert.True(lease.FirstByteProvesConsumption);
    }

    [Fact]
    public void WhileTheLeaseIsHeld_TheImageCannotBeReplaced()
    {
        string copy = CopyShippedLauncher();
        try
        {
            using (PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(copy))
            {
                Assert.True(lease.FirstByteProvesConsumption);

                // This is the whole point of the lease: the update that would invalidate the digest
                // is refused for as long as the verdict is in force.
                Assert.Throws<IOException>(() => File.Delete(copy));
                Assert.ThrowsAny<IOException>(
                    () => File.Open(copy, FileMode.Open, FileAccess.Write, FileShare.None).Dispose());
            }

            // And released afterwards, so a legitimate update is only held off for the launch.
            File.Delete(copy);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            if (File.Exists(copy))
            {
                File.Delete(copy);
            }
        }
    }

    [Fact]
    public void WhileTheLeaseIsHeld_TheImageCanStillBeLaunched()
    {
        string copy = CopyShippedLauncher();
        try
        {
            using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(copy);
            Assert.True(lease.FirstByteProvesConsumption);

            // A lease that blocked the launch would be worse than no lease at all, so the sharing
            // mode is measured rather than assumed: the real launcher starts while it is held.
            using Process process = Process.Start(new ProcessStartInfo(copy, "-V")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            Assert.Contains("plink", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void AnImageChangedBeforeTheAttestation_IsNotAttested()
    {
        string copy = CopyShippedLauncher();
        try
        {
            // Stands in for an update that lands while the password dialog is open: the bytes the
            // attestation sees are the changed ones, so the verdict is refused.
            byte[] bytes = File.ReadAllBytes(copy);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(copy, bytes);

            using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(copy);

            Assert.False(lease.FirstByteProvesConsumption);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void AnUnattestedLauncher_LeavesNothingPinned()
    {
        string copy = CopyShippedLauncher();
        try
        {
            byte[] bytes = File.ReadAllBytes(copy);
            bytes[0] ^= 0xFF;
            File.WriteAllBytes(copy, bytes);

            using (PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(copy))
            {
                Assert.False(lease.FirstByteProvesConsumption);

                // A refused verdict must not leave a handle behind either: nothing is being
                // protected, so nothing may be locked.
                File.Delete(copy);
            }

            Assert.False(File.Exists(copy));
        }
        finally
        {
            if (File.Exists(copy))
            {
                File.Delete(copy);
            }
        }
    }

    [Fact]
    public void AnExecutableAlreadyOpenForWriting_IsNotAttested()
    {
        string copy = CopyShippedLauncher();
        try
        {
            // The image cannot be pinned because something else is holding it writable. That is an
            // acquisition failure, and an acquisition failure is not an attestation.
            using FileStream blocker = new(copy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(copy);

            Assert.False(lease.FirstByteProvesConsumption);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void AnExecutableNamedLikeTheLauncher_IsNotAttested()
    {
        string path = Path.Combine(Path.GetTempPath(), $"plink-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00]);

        try
        {
            using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(path);

            // Named exactly like the real one, in a plausible place, and still refused: the name and
            // the location say nothing about when the file is read.
            Assert.False(lease.FirstByteProvesConsumption);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingPath_IsNotAttested(string? path)
    {
        using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(path);

        Assert.False(lease.FirstByteProvesConsumption);
    }

    [Fact]
    public void APathThatCannotBeOpened_IsNotAttested()
    {
        string absent = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-absent-{Guid.NewGuid():N}",
            "plink.exe");

        using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(absent);

        // Fail-closed: the connection still proceeds, only the early deletion is withheld.
        Assert.False(lease.FirstByteProvesConsumption);
    }

    [Fact]
    public void ADirectoryNamedLikeTheLauncher_IsNotAttested()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"heimdall-{Guid.NewGuid():N}", "plink.exe");
        Directory.CreateDirectory(directory);

        try
        {
            using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(directory);

            Assert.False(lease.FirstByteProvesConsumption);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void TheNotAttestedLease_IsSafeToDisposeRepeatedly()
    {
        PlinkAttestationLease lease = PlinkAttestationLease.NotAttested;

        lease.Dispose();
        lease.Dispose();

        Assert.False(lease.FirstByteProvesConsumption);
    }

    // The heart of it. An open handle pins the FILE, not the directories named on the way to it, so
    // a junction in the path can be repointed while the lease is held and the very same absolute
    // string then reaches a different executable. Reproduced here rather than argued.
    [Fact]
    public void AJunctionRepointedUnderTheLease_DoesNotChangeWhatTheLeaseLaunches()
    {
        string root = Path.Combine(Path.GetTempPath(), $"heimdall-reparse-{Guid.NewGuid():N}");
        string targetA = Path.Combine(root, "target-a");
        string targetB = Path.Combine(root, "target-b");
        string current = Path.Combine(root, "current");
        Directory.CreateDirectory(targetA);
        Directory.CreateDirectory(targetB);

        try
        {
            // A holds the attested launcher; B holds something else entirely, standing in for an
            // unmeasured build.
            File.Copy(ShippedLauncherPath(), Path.Combine(targetA, "plink.exe"));
            File.Copy(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Path.Combine(targetB, "plink.exe"));
            CreateJunction(current, targetA);

            string logical = Path.Combine(current, "plink.exe");
            using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(logical);

            Assert.True(lease.FirstByteProvesConsumption);
            Assert.NotNull(lease.LaunchPath);

            // Proof that the launch path came from the handle and not from string arithmetic:
            // GetFullPath keeps the junction in the path, the handle does not.
            Assert.Contains("target-a", lease.LaunchPath!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("current", lease.LaunchPath!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("current", Path.GetFullPath(logical), StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(Path.GetFullPath(logical), lease.LaunchPath);

            // The rebind, performed while the lease is held.
            Directory.Delete(current);
            CreateJunction(current, targetB);

            // The logical path now reaches B. This is the defect, and it is asserted so the test
            // fails loudly if the rebind ever stops working and the rest becomes vacuous.
            Assert.Equal(
                new FileInfo(Path.Combine(targetB, "plink.exe")).Length,
                new FileInfo(logical).Length);

            // The lease's path still reaches A, the image that was actually hashed.
            Assert.Equal(
                new FileInfo(Path.Combine(targetA, "plink.exe")).Length,
                new FileInfo(lease.LaunchPath!).Length);
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(targetA, "plink.exe")),
                File.ReadAllBytes(lease.LaunchPath!));
        }
        finally
        {
            if (Directory.Exists(current))
            {
                Directory.Delete(current);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheLaunchPathReachesTheSameBytesThatWereHashed()
    {
        string copy = CopyShippedLauncher();
        try
        {
            using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(copy);

            Assert.True(lease.FirstByteProvesConsumption);
            Assert.Equal(File.ReadAllBytes(copy), File.ReadAllBytes(lease.LaunchPath!));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void ARelativePath_YieldsAnAbsoluteLaunchPath()
    {
        string copy = CopyShippedLauncher();
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(copy)!;
            string relative = Path.GetFileName(copy);

            using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(relative);

            Assert.True(lease.FirstByteProvesConsumption);
            Assert.NotNull(lease.LaunchPath);

            // A relative string would be resolved once here and once again by the launcher, against
            // whatever the current directory happened to be by then. The handle removes the question.
            Assert.NotEqual(relative, lease.LaunchPath);
            Assert.Equal(File.ReadAllBytes(copy), File.ReadAllBytes(lease.LaunchPath!));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            File.Delete(copy);
        }
    }

    // The two halves of the verdict must never disagree: a lease that grants the early deletion
    // without naming the image it was granted for would leave the caller launching the string it
    // already had. Checked across every outcome the suite produces, not on one happy case.
    [Fact]
    public void TheVerdictAndTheLaunchPath_AlwaysAgree()
    {
        string attested = CopyShippedLauncher();
        string foreign = Path.Combine(Path.GetTempPath(), $"plink-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(foreign, [0x4D, 0x5A, 0x90, 0x00]);
        string absent = Path.Combine(Path.GetTempPath(), $"heimdall-absent-{Guid.NewGuid():N}", "plink.exe");

        try
        {
            foreach (string? candidate in new[] { attested, foreign, absent, null, "", "   " })
            {
                using PlinkAttestationLease lease = PlinkCompatibilityAttestation.Acquire(candidate);

                Assert.Equal(lease.FirstByteProvesConsumption, lease.LaunchPath is not null);
            }
        }
        finally
        {
            File.Delete(attested);
            File.Delete(foreign);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // Directory.CreateSymbolicLink needs developer mode or elevation; a junction does not, which
        // is exactly why it is the reachable form of this defect.
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

        process.WaitForExit(30_000);
        Assert.True(Directory.Exists(link), $"Could not create a junction at {link}.");
    }

    // GetFinalPathNameByHandle does not fail on a handle that was just opened, so the failure
    // branch is unreachable from outside and the resolution is supplied here instead. What matters
    // is the consequence: no launch path means no verdict, and nothing left pinned.
    [Fact]
    public void WhenTheFinalPathCannotBeResolved_NothingIsAttestedAndNothingStaysPinned()
    {
        string copy = CopyShippedLauncher();
        try
        {
            using (PlinkAttestationLease lease =
                PlinkCompatibilityAttestation.Acquire(copy, _ => null))
            {
                Assert.False(lease.FirstByteProvesConsumption);
                Assert.Null(lease.LaunchPath);

                // The pin must have been released, or a legitimate update would be blocked by a
                // lease that grants nothing.
                File.Delete(copy);
            }

            Assert.False(File.Exists(copy));
        }
        finally
        {
            if (File.Exists(copy))
            {
                File.Delete(copy);
            }
        }
    }

    // The launch path must come from the handle that stays open, and the file must be opened once.
    // Re-opening the path to resolve it would reintroduce exactly the second resolution this whole
    // mechanism exists to remove, and that is invisible from outside: a re-open in the same instant
    // yields the same answer. So it is pinned on the source instead, and said to be so.
    [Fact]
    public void TheLaunchPathIsResolvedFromTheHandleThatStaysOpen()
    {
        string body = ExtractAcquireBody();

        Assert.Contains("resolveFinalPath(pin.SafeFileHandle)", body, StringComparison.Ordinal);

        // Exactly one open in the whole method: the pin.
        Assert.Equal(1, CountOccurrences(body, "new FileStream("));
        Assert.DoesNotContain("Path.GetFullPath", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FileInfo(", body, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while (true)
        {
            int found = haystack.IndexOf(needle, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }

            count++;
            index = found + needle.Length;
        }
    }

    private static string ExtractAcquireBody()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "Heimdall.App",
            "Services",
            "Handlers",
            "PlinkCompatibilityAttestation.cs"));

        const string Signature = "Func<SafeFileHandle, string?> resolveFinalPath)";
        int start = source.IndexOf(Signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "The seam overload was not found.");

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
                    return source[open..(i + 1)];
                }
            }
        }

        Assert.Fail("Unbalanced body for Acquire.");
        return string.Empty;
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

    private static string CopyShippedLauncher()
    {
        // Always a copy. The experiments here replace and lock the file, and the tracked asset must
        // never be the subject of that.
        string copy = Path.Combine(Path.GetTempPath(), $"heimdall-plink-{Guid.NewGuid():N}.exe");
        File.Copy(ShippedLauncherPath(), copy);
        return copy;
    }

    private static string ShippedLauncherPath()
    {
        // Anchored on the solution file, not on .git: in a worktree .git is a FILE, so a
        // Directory.Exists walk climbs past this checkout into the main one and reads the wrong
        // source.
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "Heimdall.App", "Assets", "Tools", "plink.exe");
    }
}
