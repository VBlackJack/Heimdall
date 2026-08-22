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
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// Where the relauncher transcript is written, which decides whether anyone can ever
/// read it.
/// </summary>
/// <remarks>
/// The transcript is the only account of what happened after the application exited.
/// It used to be a random GUID under %TEMP% whose name was recorded nowhere, so an
/// update that failed left an explanation that existed and could not be found. The
/// application already displays its log directory in the About panel and already opens
/// it on request; putting the transcript there costs no new interface.
/// </remarks>
public sealed class SystemUpdateInstallerHostLogPathTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        "heimdall-bl0080-logpath",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateLogPath_ReturnsAPathInsideTheDirectoryTheAboutPanelSurfaces()
    {
        var host = new SystemUpdateInstallerHost(_dataRoot);

        string logPath = host.CreateLogPath();

        string expected = ApplicationDataPathResolver.GetLogsDirectory(_dataRoot);
        Assert.Equal(expected, Path.GetDirectoryName(logPath));
        Assert.True(Directory.Exists(expected), "the transcript directory must exist before the script runs");

        // Deriving from the data root is the whole assertion: it is what makes the path
        // the same one the About panel names. Restoring Path.GetTempPath() in the
        // implementation breaks this equality, which is the mutant this test exists for.
        // Asserting "not under %TEMP%" would not work here and would not mean anything
        // either - this test's own data root is a temporary directory, precisely so the
        // operator's real profile is never touched.
    }

    [Fact]
    public void CreateLogPath_NamesSuccessiveAttemptsInAnOrderThatSorts()
    {
        var host = new SystemUpdateInstallerHost(_dataRoot);

        string first = Path.GetFileName(host.CreateLogPath());

        // A timestamp, not a GUID: two attempts must be tellable apart by reading the
        // directory, which is the whole point of putting them somewhere visible.
        Assert.Matches(@"^Heimdall_relaunch_\d{8}-\d{6}\.log$", first);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }
}
