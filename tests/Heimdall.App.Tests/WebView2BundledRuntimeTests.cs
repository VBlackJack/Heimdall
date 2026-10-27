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
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests;

/// <summary>
/// Which runtime an embedded view runs on. Only the SelfContained edition uses the Fixed
/// Version Runtime in <c>runtimes\webview2</c>; a Standard build that finds one there, left
/// by the SelfContained install it replaced, runs on the system Evergreen Runtime instead.
/// </summary>
public sealed class WebView2BundledRuntimeTests
{
    private const string BaseDirectory = @"C:\Heimdall";
    private const string RuntimeFolder = @"C:\Heimdall\runtimes\webview2";

    [Fact]
    public void SelfContained_WithTheRuntime_UsesTheBundledFolder()
    {
        string? folder = WebView2Helper.BundledRuntimeFolderFor(BuildVariant.SelfContained, BaseDirectory, _ => true);

        Assert.Equal(RuntimeFolder, folder);
    }

    [Fact]
    public void Standard_WithALeftoverRuntime_UsesTheSystemRuntime()
    {
        string? folder = WebView2Helper.BundledRuntimeFolderFor(BuildVariant.Standard, BaseDirectory, _ => true);

        Assert.Null(folder);
    }

    [Fact]
    public void SelfContained_WithoutTheRuntime_UsesTheSystemRuntime()
    {
        string? folder = WebView2Helper.BundledRuntimeFolderFor(BuildVariant.SelfContained, BaseDirectory, _ => false);

        Assert.Null(folder);
    }

    [Fact]
    public void TheProbe_LooksForTheRuntimeExecutable()
    {
        string? probed = null;
        WebView2Helper.BundledRuntimeFolderFor(BuildVariant.SelfContained, BaseDirectory, path =>
        {
            probed = path;
            return true;
        });

        Assert.Equal(@"C:\Heimdall\runtimes\webview2\msedgewebview2.exe", probed);
    }

    /// <summary>
    /// The edition reaches the executable only if Build.ps1 passes it to each publish and
    /// the project turns the property into the metadata VariantDetector reads.
    /// </summary>
    [Fact]
    public void TheEditionStamp_IsPassedByTheBuildAndMappedByTheProject()
    {
        string root = RepositoryRoot();
        string build = File.ReadAllText(Path.Combine(root, "Build.ps1"));
        string project = File.ReadAllText(Path.Combine(root, "src", "Heimdall.App", "Heimdall.App.csproj"));

        Assert.Contains("\"-p:HeimdallEdition=$VariantName\"", build, StringComparison.Ordinal);
        Assert.Contains(
            $"<AssemblyMetadata Include=\"{VariantDetector.EditionMetadataKey}\" Value=\"$(HeimdallEdition)\" />",
            project,
            StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException($"Cannot find repository root from {AppContext.BaseDirectory}.");
    }
}
