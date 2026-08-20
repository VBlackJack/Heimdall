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
using System.Threading;
using System.Threading.Tasks;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers the cleanup helper that stops a temporary file outliving its test from failing it.
/// </summary>
public sealed class TemporaryFileCleanupTests
{
    [Fact]
    public void Delete_RemovesAFileNobodyHolds()
    {
        string path = NewTempFile();

        TemporaryFileCleanup.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_FileStillHeld_GivesUpQuietlyInsteadOfThrowing()
    {
        string path = NewTempFile();
        using FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // The point of the helper: this is what used to reach the test runner as a failure, from a
        // cleanup block, after the assertions had already passed. A short budget here rather than
        // the default, so proving the give-up path costs milliseconds instead of seconds.
        TemporaryFileCleanup.Delete(path, TimeSpan.FromMilliseconds(100));

        Assert.True(File.Exists(path));
    }

    // The exception the runner actually threw was UnauthorizedAccessException, not IOException, and
    // a helper catching only the latter would have left the original failure exactly as it was. The
    // real cause there was a running process still holding its own image, which cannot be staged
    // deterministically from inside a test, so the read-only attribute is used to make Windows
    // refuse the delete the same way. What is being covered is the catch arm, not the cause.
    [Fact]
    public void Delete_RefusedWithUnauthorizedAccess_GivesUpQuietlyInsteadOfThrowing()
    {
        string path = NewTempFile();
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(path));

            TemporaryFileCleanup.Delete(path, TimeSpan.FromMilliseconds(100));

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    // The retry has to actually do something. Without it this passes only if the release happens to
    // land before the single attempt, which is the race the helper exists to absorb.
    [Fact]
    public async Task Delete_FileReleasedWhileRetrying_StillRemovesIt()
    {
        string path = NewTempFile();
        FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        using ManualResetEventSlim deleteStarted = new(false);
        Task releaser = Task.Run(() =>
        {
            deleteStarted.Wait(TimeSpan.FromSeconds(5));
            Thread.Sleep(150);
            held.Dispose();
        });

        Stopwatch elapsed = Stopwatch.StartNew();
        deleteStarted.Set();
        TemporaryFileCleanup.Delete(path);
        elapsed.Stop();

        await releaser.WaitAsync(TimeSpan.FromSeconds(10));
        held.Dispose();

        Assert.False(File.Exists(path));

        // And it got there by waiting rather than by the handle never having been held: a helper
        // that gave up immediately could not have deleted a file released 150 ms later.
        Assert.True(
            elapsed.ElapsedMilliseconds >= 100,
            $"the delete returned after {elapsed.ElapsedMilliseconds} ms, so it cannot have waited "
                + "for the release and this test is not exercising the retry");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Delete_NothingToDelete_DoesNothing(string? path)
    {
        TemporaryFileCleanup.Delete(path);
    }

    [Fact]
    public void Delete_AlreadyGone_DoesNothing()
    {
        string path = NewTempFile();
        File.Delete(path);

        TemporaryFileCleanup.Delete(path);

        Assert.False(File.Exists(path));
    }

    private static string NewTempFile()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-cleanup-{System.Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "x");
        return path;
    }
}
