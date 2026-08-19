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
