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
using Heimdall.Core.Discovery;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// The system tools started from the core assembly are named by absolute path, and the
/// shared resolver that produces those paths behaves the way the launch sites assume.
/// </summary>
/// <remarks>
/// A bare image name started with <c>UseShellExecute=false</c> is resolved by CreateProcess
/// through the application directory and the process's current directory before the system
/// directory, so an executable of the same name that the process happens to reach is
/// launched with the arguments meant for the system tool.
/// </remarks>
public sealed class SystemExecutablePathTests
{
    private const string ArpImageName = "arp.exe";

    [Fact]
    public void CartographyArpProbe_IsNamedByAbsolutePathUnderTheSystemDirectory()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.False(string.IsNullOrEmpty(systemDirectory), "The system directory must resolve for this test to mean anything.");

        ProcessStartInfo startInfo = CartographyEngine.CreateWindowsArpStartInfo();

        Assert.True(
            Path.IsPathFullyQualified(startInfo.FileName),
            $"FileName '{startInfo.FileName}' is not a fully qualified path, so CreateProcess searches for it.");
        Assert.Equal(systemDirectory, Path.GetDirectoryName(startInfo.FileName), ignoreCase: true);
        Assert.Equal(ArpImageName, Path.GetFileName(startInfo.FileName), ignoreCase: true);
        Assert.Equal(systemDirectory, startInfo.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void InSystemDirectory_RootsTheNameUnderTheSystemDirectory()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);

        string resolved = SystemExecutablePath.InSystemDirectory(ArpImageName);

        Assert.Equal(Path.Combine(systemDirectory, ArpImageName), resolved);
    }

    [Fact]
    public void InWindowsDirectory_RootsTheNameUnderTheWindowsDirectory()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        string resolved = SystemExecutablePath.InWindowsDirectory("explorer.exe");

        Assert.Equal(Path.Combine(windowsDirectory, "explorer.exe"), resolved);
    }

    [Fact]
    public void WindowsPowerShell_NamesTheWindowsHostAndNotPwsh()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        Assert.Equal(expected, SystemExecutablePath.WindowsPowerShell);
    }

    [Fact]
    public void InSystemDirectory_RejectsAnEmptyName()
    {
        Assert.Throws<ArgumentException>(() => SystemExecutablePath.InSystemDirectory("   "));
    }
}
