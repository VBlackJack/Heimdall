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
using FlaUI.Core;
using FlaUI.UIA3;
using Heimdall.App.UiTests.Infrastructure;
namespace Heimdall.App.UiTests.E2E;

[Collection(DesktopUiCollection.Name)]
public sealed class ShellLaunchTests
{
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void MainWindow_Launches_AndTitleContainsHeimdall()
    {
        var exe = LocateHeimdallExe();
        Assert.True(File.Exists(exe), $"Heimdall executable not found at '{exe}'.");

        using var app = Application.Launch(exe);
        try
        {
            using var automation = new UIA3Automation();
            var window = WaitHelpers.WaitFor(
                () => app.GetMainWindow(automation),
                "Heimdall main window",
                TimeSpan.FromSeconds(60));

            WaitHelpers.WaitUntil(
                () => !string.IsNullOrWhiteSpace(window.Title)
                    && window.Title.Contains("Heimdall", StringComparison.OrdinalIgnoreCase),
                "Heimdall main window title",
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                app.Close();
            }
            catch
            {
                // Best effort.
            }

            if (!app.HasExited)
            {
                app.Kill();
            }
        }
    }

    private static string LocateHeimdallExe()
    {
        var debugDir = WpfTestHost.ResolveAppBuildDir("Debug");
        if (debugDir is not null)
        {
            return Path.Combine(debugDir, "Heimdall.exe");
        }

        var releaseDir = WpfTestHost.ResolveAppBuildDir("Release");
        if (releaseDir is not null)
        {
            return Path.Combine(releaseDir, "Heimdall.exe");
        }

        // No built output found; return a deterministic Debug-style path so the caller's
        // File.Exists guard fails cleanly with a sensible diagnostic instead of throwing here.
        return Path.Combine(WpfTestHost.RepoRoot, "src", "Heimdall.App", "bin", "Debug", "Heimdall.exe");
    }
}
