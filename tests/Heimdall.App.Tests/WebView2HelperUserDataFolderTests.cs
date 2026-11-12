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
using Xunit;

namespace Heimdall.App.Tests;

/// <summary>
/// Two instances (another data root, another runtime) must never share one browser profile folder.
/// </summary>
public sealed class WebView2HelperUserDataFolderTests
{
    private const string TempRoot = @"C:\Temp";
    private const string DataRootA = @"D:\Heimdall\data-a";
    private const string DataRootB = @"D:\Heimdall\data-b";
    private const string InstallA = @"D:\Heimdall\app-a";
    private const string InstallB = @"D:\Heimdall\app-b";
    private const string BundledRuntime = @"D:\Heimdall\runtimes\webview2";
    private const string Sub = "SSH";

    [Fact]
    public void UserDataFolder_DiffersByDataRoot()
    {
        Assert.NotEqual(
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallA, null, Sub),
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootB, InstallA, null, Sub));
    }

    [Fact]
    public void UserDataFolder_DiffersByInstallFolder()
    {
        Assert.NotEqual(
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallA, null, Sub),
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallB, null, Sub));
    }

    [Fact]
    public void UserDataFolder_DiffersBetweenBundledAndEvergreen()
    {
        Assert.NotEqual(
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallA, BundledRuntime, Sub),
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallA, null, Sub));
    }

    [Fact]
    public void UserDataFolder_StableForSameInputs()
    {
        Assert.Equal(
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallA, BundledRuntime, Sub),
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallA, BundledRuntime, Sub));
    }

    [Fact]
    public void UserDataFolder_IgnoresCaseAndTrailingSeparatorOfTheDataRoot()
    {
        Assert.Equal(
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallA, null, Sub),
            WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA.ToUpperInvariant() + @"\", InstallA.ToUpperInvariant() + @"\", null, Sub));
    }

    [Fact]
    public void UserDataFolder_KeepsTheSubfolderName()
    {
        string folder = WebView2Helper.EmbeddedUserDataFolderFor(TempRoot, DataRootA, InstallA, null, "DrawIO");

        Assert.Equal("DrawIO", Path.GetFileName(folder));
        Assert.Equal(
            WebView2Helper.InstanceKeyLength,
            Path.GetFileName(Path.GetDirectoryName(folder))!.Length);
        Assert.StartsWith(Path.Combine(TempRoot, "Heimdall", "WebView2"), folder, StringComparison.Ordinal);
    }
}
