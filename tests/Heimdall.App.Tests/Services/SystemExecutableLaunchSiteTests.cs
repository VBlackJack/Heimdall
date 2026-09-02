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
using Heimdall.App.Services;
using Heimdall.App.Views;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// Every launch site that starts a Windows system tool names its image by absolute path
/// and pins the child's working directory to the folder that image lives in.
/// </summary>
/// <remarks>
/// A bare image name started with <c>UseShellExecute=false</c> is resolved by CreateProcess
/// through the application directory and the process's current directory before the system
/// directory. The current directory is whatever folder the user last browsed in a file
/// dialog, so an executable dropped there alongside a downloaded archive receives the
/// arguments meant for the system tool. Naming the image by path removes the search, and
/// pinning the working directory keeps the child from inheriting the browsed folder.
/// <para>
/// The expected directory is read from <see cref="Environment.GetFolderPath"/> here rather
/// than from the production helper, so the assertion cannot pass by agreeing with itself.
/// </para>
/// </remarks>
public sealed class SystemExecutableLaunchSiteTests
{
    private const string EncodedCommandSample = "RwBlAHQALQBTAGUAcgB2AGkAYwBlAA==";
    private const string SampleFilePath = @"C:\Users\example\Downloads\report.txt";
    private const string SampleRecordType = "CAA";
    private const string SampleDomain = "example.com";
    private const string SampleHostname = "example.com";

    public static TheoryData<string, string, Func<ProcessStartInfo>> SystemDirectorySites() => new()
    {
        { nameof(CronJobService), "schtasks.exe", CronJobService.CreateSchtasksStartInfo },
        { nameof(DefaultArpTableReader), "arp.exe", DefaultArpTableReader.CreateWindowsArpStartInfo },
        { nameof(RouteTableService), "route.exe", RouteTableService.CreateRoutePrintStartInfo },
        { nameof(WifiScanService), "netsh.exe", WifiScanService.CreateNetshWifiScanStartInfo },
        { nameof(WindowsTpmPresenceService), "tpmtool.exe", WindowsTpmPresenceService.CreateTpmToolStartInfo },
        {
            nameof(DnsLookupService),
            "nslookup.exe",
            () => DnsLookupService.CreateLocalNslookupStartInfo(SampleHostname, SampleRecordType, null)
        },
        {
            nameof(DnsSecurityService),
            "nslookup.exe",
            () => DnsSecurityService.CreateNslookupStartInfo(SampleDomain, SampleRecordType)
        },
        {
            nameof(SecNumCloudAuditEngine),
            "nslookup.exe",
            () => SecNumCloudAuditEngine.CreateNslookupStartInfo(SampleRecordType, SampleDomain)
        },
        {
            nameof(LocalFileBrowserView),
            "rundll32.exe",
            () => LocalFileBrowserView.CreateOpenWithStartInfo(SampleFilePath)
        },
    };

    public static TheoryData<string, string, Func<ProcessStartInfo>> WindowsDirectorySites() => new()
    {
        {
            $"{nameof(LocalFileBrowserView)}.Reveal",
            "explorer.exe",
            () => LocalFileBrowserView.CreateExplorerRevealStartInfo(SampleFilePath)
        },
        {
            $"{nameof(LocalFileBrowserView)}.Browse",
            "explorer.exe",
            () => LocalFileBrowserView.CreateExplorerBrowseStartInfo(SampleFilePath)
        },
    };

    public static TheoryData<string, Func<ProcessStartInfo>> WindowsPowerShellSites() => new()
    {
        { $"{nameof(ServiceStatusService)}.List", ServiceStatusService.CreateServiceListStartInfo },
        {
            $"{nameof(ServiceStatusService)}.Action",
            () => ServiceStatusService.CreateElevatedServiceActionStartInfo(EncodedCommandSample)
        },
    };

    [Theory]
    [MemberData(nameof(SystemDirectorySites))]
    public void SystemTool_IsNamedByAbsolutePathUnderTheSystemDirectory(
        string site,
        string expectedImageName,
        Func<ProcessStartInfo> createStartInfo)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.False(string.IsNullOrEmpty(systemDirectory), "The system directory must resolve for this test to mean anything.");

        ProcessStartInfo startInfo = createStartInfo();

        AssertRootedIn(site, startInfo, systemDirectory, expectedImageName, systemDirectory);
    }

    [Theory]
    [MemberData(nameof(WindowsDirectorySites))]
    public void WindowsDirectoryTool_IsNamedByAbsolutePathUnderTheWindowsDirectory(
        string site,
        string expectedImageName,
        Func<ProcessStartInfo> createStartInfo)
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.False(string.IsNullOrEmpty(windowsDirectory), "The Windows directory must resolve for this test to mean anything.");

        ProcessStartInfo startInfo = createStartInfo();

        AssertRootedIn(site, startInfo, windowsDirectory, expectedImageName, windowsDirectory);
    }

    [Theory]
    [MemberData(nameof(WindowsPowerShellSites))]
    public void PowerShellSite_NamesWindowsPowerShellByAbsolutePath(
        string site,
        Func<ProcessStartInfo> createStartInfo)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string expectedDirectory = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0");

        ProcessStartInfo startInfo = createStartInfo();

        AssertRootedIn(site, startInfo, expectedDirectory, "powershell.exe", systemDirectory);
    }

    /// <summary>
    /// The update relauncher is the one launch where a substituted host would run with the
    /// application's own privileges and then replace the installed binaries, so it gets its
    /// own assertion rather than riding on the table above.
    /// </summary>
    [Fact]
    public void UpdateRelauncher_NamesWindowsPowerShellByAbsolutePathAndPinsItsWorkingDirectory()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string expectedDirectory = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0");
        var host = new SystemUpdateInstallerHost(Path.GetTempPath());

        string resolvedHost = host.ResolvePowerShellExecutable();
        ProcessStartInfo startInfo = SystemUpdateInstallerHost.CreateDetachedStartInfo(
            resolvedHost,
            "-NoProfile");

        Assert.True(
            Path.IsPathFullyQualified(resolvedHost),
            $"ResolvePowerShellExecutable returned '{resolvedHost}', which CreateProcess still has to search for.");
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(resolvedHost), ignoreCase: true);
        Assert.Equal("powershell.exe", Path.GetFileName(resolvedHost), ignoreCase: true);
        Assert.Equal(systemDirectory, startInfo.WorkingDirectory, ignoreCase: true);
    }

    /// <summary>
    /// Positive control: the same assertion applied to the shape these sites used to have
    /// fails, so a green above is not an artefact of an assertion that accepts anything.
    /// </summary>
    [Fact]
    public void TheAssertion_RejectsABareImageNameWithNoWorkingDirectory()
    {
        var bareStartInfo = new ProcessStartInfo { FileName = "schtasks.exe" };

        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertRootedIn(
            "control",
            bareStartInfo,
            systemDirectory,
            "schtasks.exe",
            systemDirectory));
    }

    private static void AssertRootedIn(
        string site,
        ProcessStartInfo startInfo,
        string expectedDirectory,
        string expectedImageName,
        string expectedWorkingDirectory)
    {
        Assert.True(
            Path.IsPathFullyQualified(startInfo.FileName),
            $"{site}: FileName '{startInfo.FileName}' is not a fully qualified path, so CreateProcess searches for it.");

        Assert.Equal(
            expectedDirectory,
            Path.GetDirectoryName(startInfo.FileName),
            ignoreCase: true);

        Assert.Equal(
            expectedImageName,
            Path.GetFileName(startInfo.FileName),
            ignoreCase: true);

        Assert.Equal(
            expectedWorkingDirectory,
            startInfo.WorkingDirectory,
            ignoreCase: true);
    }
}
