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
using Heimdall.App.Services.Handlers;

namespace Heimdall.App.Tests;

/// <summary>
/// SSH-013. Whether a launcher's first byte proves it already read its password file is a property
/// of that exact build, so it is decided on the executable's bytes and on nothing else.
/// </summary>
/// <remarks>
/// <para>The shipped launcher is the build whose <c>-pwfile</c> timing was measured, so it attests.
/// Everything else - a different build, a file named plink.exe, a missing or unreadable path -
/// does not, and the password file then waits for process exit as it always did.</para>
/// <para>These oracles do not claim any resistance to a hostile executable. Heimdall hands the
/// password to whatever it was pointed at, so an executable chosen to steal it has already won.
/// What is pinned here is narrower: a timing conclusion drawn from one measured build is not
/// applied to a binary nobody measured.</para>
/// </remarks>
public sealed class PlinkCompatibilityAttestationTests
{
    [Fact]
    public void TheShippedLauncher_Attests()
    {
        string shipped = ShippedLauncherPath();

        // If this ever fails, the shipped binary was replaced without re-measuring its -pwfile
        // timing, and the recorded identity no longer describes what actually runs.
        Assert.True(
            PlinkCompatibilityAttestation.FirstByteProvesConsumption(shipped),
            $"The shipped launcher at {shipped} no longer matches the measured identity.");
    }

    [Fact]
    public void AnyOtherExecutable_DoesNotAttest()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"plink-{Guid.NewGuid():N}.exe");

        // Named exactly like the real one, in a plausible place, and still not attested: the name
        // and the location say nothing about when the file is read.
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00]);

        try
        {
            Assert.False(PlinkCompatibilityAttestation.FirstByteProvesConsumption(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AShippedLauncherWithOneByteChanged_DoesNotAttest()
    {
        byte[] bytes = File.ReadAllBytes(ShippedLauncherPath());
        bytes[^1] ^= 0xFF;

        string path = Path.Combine(Path.GetTempPath(), $"plink-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, bytes);

        try
        {
            // A rebuild, a patch or a repack is a different build. Nothing about the previous
            // measurement carries over to it.
            Assert.False(PlinkCompatibilityAttestation.FirstByteProvesConsumption(path));
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
    public void AMissingPath_DoesNotAttest(string? path)
    {
        Assert.False(PlinkCompatibilityAttestation.FirstByteProvesConsumption(path));
    }

    [Fact]
    public void APathThatCannotBeRead_DoesNotAttest()
    {
        string absent = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-absent-{Guid.NewGuid():N}",
            "plink.exe");

        // An identity that could not be read is an identity that is not attested. Fail-closed: the
        // connection still proceeds, only the early deletion is withheld.
        Assert.False(PlinkCompatibilityAttestation.FirstByteProvesConsumption(absent));
    }

    [Fact]
    public void ADirectoryNamedLikeTheLauncher_DoesNotAttest()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"heimdall-{Guid.NewGuid():N}", "plink.exe");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.False(PlinkCompatibilityAttestation.FirstByteProvesConsumption(directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    private static string ShippedLauncherPath()
    {
        // Anchored on the solution file, not on .git: in a worktree .git is a FILE, so a
        // Directory.Exists walk climbs past this checkout into the main one and reads the wrong
        // source. This test caught that on itself.
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
