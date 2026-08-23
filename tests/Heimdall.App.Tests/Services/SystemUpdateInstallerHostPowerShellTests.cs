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

namespace Heimdall.App.Tests.Services;

/// <summary>
/// Which PowerShell host the relauncher runs under, and that it is always the same one.
/// </summary>
/// <remarks>
/// This used to prefer pwsh.exe when the PATH offered it and fall back to the Windows
/// host otherwise. Nothing gave a reason for the preference, and it had a cost that only
/// appeared in support: whether an update behaved one way or the other depended on
/// whether the user happened to have installed PowerShell 7 - a difference invisible from
/// inside the application and impossible to ask a user about.
/// <para>
/// The test asserts the choice does not depend on the environment, which is the property
/// that matters. A single equality would pass on a machine with no pwsh installed even
/// with the old resolver still in place.
/// </para>
/// </remarks>
public sealed class SystemUpdateInstallerHostPowerShellTests
{
    [Fact]
    public void ResolvePowerShellExecutable_NamesTheHostEveryWindowsCarries()
    {
        var host = new SystemUpdateInstallerHost();

        Assert.Equal("powershell.exe", host.ResolvePowerShellExecutable());
    }

    [Fact]
    public void ResolvePowerShellExecutable_DoesNotDependOnWhatThePathOffers()
    {
        var host = new SystemUpdateInstallerHost();
        string? originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            // A directory that really does contain a pwsh.exe. Under the previous
            // resolver this alone flipped the answer, so it is the discriminating case
            // rather than a decorative one.
            string probeDirectory = Path.Combine(
                Path.GetTempPath(),
                "heimdall-bl0091",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probeDirectory);
            File.WriteAllText(Path.Combine(probeDirectory, "pwsh.exe"), "");

            try
            {
                Environment.SetEnvironmentVariable("PATH", probeDirectory);

                Assert.Equal("powershell.exe", host.ResolvePowerShellExecutable());
            }
            finally
            {
                Directory.Delete(probeDirectory, recursive: true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
