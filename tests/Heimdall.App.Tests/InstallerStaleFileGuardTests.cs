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
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// The Inno Setup script removes each vendored asset tree before it copies the new one.
/// </summary>
/// <remarks>
/// <para>The installer only adds and overwrites. A file that a newer version of draw.io,
/// noVNC or xterm no longer carries would stay on disk beside the new ones, and the app
/// would keep loading it. <c>[InstallDelete]</c> removes the tree first; this test keeps the
/// list in step with the <c>VENDORED.json</c> manifests, so a vendored tree added later
/// cannot be forgotten.</para>
/// <para>There is no end-to-end test: an installer upgrade cannot run in the test lane.
/// The script is compiled with ISCC by the packaging workflow instead.</para>
/// </remarks>
public sealed class InstallerStaleFileGuardTests
{
    private const string AssetsDirectory = "src/Heimdall.App/Assets";
    private const string ManifestFileName = "VENDORED.json";
    private const string ScriptPath = "installer/innosetup.iss";
    private const string WholeDirectoryMarker = ".";

    /// <summary>The editor's source tree: vendored, but excluded from the app content.</summary>
    private const string UnshippedTree = "milkdown-editor";

    /// <summary>A scan that found fewer trees than this did not reach them.</summary>
    private const int MinimumShippedTrees = 5;

    private static readonly Regex InstallDeleteLine = new(
        @"^\s*Type:\s*filesandordirs;\s*Name:\s*""(?<path>[^""]+)""\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryShippedVendoredTree_IsRemovedBeforeTheNewOneIsCopied()
    {
        string root = RepositoryRoot();
        List<string> trees = ShippedCompleteTrees(Path.Combine(root, AssetsDirectory));

        // Without this a scan that read no manifest would pass by having nothing to check.
        Assert.True(
            trees.Count >= MinimumShippedTrees,
            $"Only {trees.Count} shipped vendored trees were found; the scan did not reach them.");

        HashSet<string> deleted = DeletedPaths(File.ReadAllText(Path.Combine(root, ScriptPath)));

        List<string> missing = [.. trees
            .Where(tree => !deleted.Contains(@"{app}\Assets\" + tree))];
        Assert.True(
            missing.Count == 0,
            "innosetup.iss has no [InstallDelete] line for these vendored trees, so an upgrade "
            + "keeps the files a newer version dropped: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheScript_NeverDeletesAssetsOrTheApplicationFolderWithAWildcard()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), ScriptPath));

        foreach (string path in DeletedPaths(script))
        {
            Assert.False(
                path.Contains('*', StringComparison.Ordinal)
                || path.TrimEnd('\\').Equals(@"{app}\Assets", StringComparison.OrdinalIgnoreCase)
                || path.TrimEnd('\\').Equals("{app}", StringComparison.OrdinalIgnoreCase),
                $"[InstallDelete] removes '{path}': that is broader than the vendored trees and "
                + "would take Heimdall's own files with it.");
        }
    }

    [Fact]
    public void TheUpgradePrompt_ReadsTheMachineWideInstallRecordToo()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), ScriptPath));

        Assert.Contains("RegQueryStringValue(HKCU, UninstallKey", script, StringComparison.Ordinal);
        Assert.Contains("RegQueryStringValue(HKLM, UninstallKey", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DeletedPaths_ReadsOnlyTheFilesAndDirsLines()
    {
        HashSet<string> paths = DeletedPaths(
            "; Type: filesandordirs; Name: \"{app}\\x\"\n"
            + "Type: filesandordirs; Name: \"{app}\\Assets\\vnc\"\n"
            + "Type: files; Name: \"{app}\\old.dll\"\n");

        Assert.Equal([@"{app}\Assets\vnc"], paths);
    }

    private static HashSet<string> DeletedPaths(string script)
        => [.. InstallDeleteLine.Matches(script).Select(match => match.Groups["path"].Value)];

    /// <summary>
    /// The first-level directories under Assets that a manifest declares complete (<c>"."</c>) and
    /// that the app ships. A manifest that only claims a subdirectory (milkdown-editor's
    /// <c>dist</c>) is a build input, not an installed tree.
    /// </summary>
    private static List<string> ShippedCompleteTrees(string assets)
    {
        List<string> trees = [];
        foreach (string manifest in Directory.GetFiles(assets, ManifestFileName, SearchOption.AllDirectories))
        {
            string tree = Path.GetFileName(Path.GetDirectoryName(manifest)!);
            if (tree.Equals(UnshippedTree, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.GetProperty("completeDirectories").EnumerateArray()
                .Any(entry => entry.GetString() == WholeDirectoryMarker))
            {
                trees.Add(tree);
            }
        }

        return trees;
    }

    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Cannot find repository root containing Heimdall.slnx from test binary directory: "
            + AppContext.BaseDirectory);
    }
}
