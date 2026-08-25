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
using System.Text.RegularExpressions;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes who may write the whole settings object.
/// </summary>
/// <remarks>
/// <c>SaveSettingsAsync</c> writes the object it is handed, so every caller erases whatever
/// another surface persisted since it took its snapshot. That is harmless when the load and
/// the write are adjacent, and destroys work when anything sits between them - BL-0095 found
/// three sites where something did, one of them separated by a modal dialog, so the window
/// was bounded by how long a user thought about it.
///
/// The class of defect is structural: nothing in the suite notices a new caller, and the
/// damage is silent by nature - the user sees a setting they made "not stick", never an
/// error. Hence a frozen list rather than a review habit. A legitimate new caller is added
/// here in the same commit that introduces it, with the reason, so the decision is recorded
/// where the next reader will look.
/// </remarks>
public sealed class SettingsWholeObjectWriteGuardTests
{
    /// <summary>
    /// Sites allowed to hand a whole <see cref="AppSettings"/> to
    /// <c>SaveSettingsAsync</c>, each with the reason it is safe.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedCallers = new(StringComparer.Ordinal)
    {
        ["App.xaml.cs"] =
            "startup HMAC key seeding: the settings object is the one just loaded by the caller "
            + "and nothing is awaited between the mutation and the write",
        ["MainWindow.xaml.cs"] =
            "new root folder: the name prompt happens BEFORE the load, so load and write are adjacent",
        ["ContextMenuFactory.cs"] =
            "new folder from the tree, twice: the name prompt happens BEFORE the load, so load "
            + "and write are adjacent",
        ["ScheduledTasksViewModel.cs"] =
            "scheduled task list: load, assign, write, three consecutive statements"
    };

    [Fact]
    public void NoNewWholeSettingsWriter_WithoutADecision()
    {
        List<string> offenders = [];

        foreach (string file in EnumerateAppSources())
        {
            string name = Path.GetFileName(file);
            if (AllowedCallers.ContainsKey(name))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (CallsSaveSettings(text))
            {
                offenders.Add(Path.GetFileName(Path.GetDirectoryName(file)) + "/" + name);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These write the whole settings object without being on the allow-list: "
            + string.Join(", ", offenders)
            + ". Prefer MergeSettingAsync, which reloads under the write lock and writes only "
            + "what the callback changes. If a whole-object write is genuinely right here, add "
            + "the file to AllowedCallers with the reason, in this commit.");
    }

    // Non-vacuity: the guard must actually be looking at files that contain the call, or an
    // allow-list of four would sit above a scan that never matched anything.
    [Fact]
    public void Guard_ActuallyFindsTheAllowedCallers()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string file in EnumerateAppSources())
        {
            if (CallsSaveSettings(File.ReadAllText(file)))
            {
                seen.Add(Path.GetFileName(file));
            }
        }

        foreach (string allowed in AllowedCallers.Keys)
        {
            Assert.True(seen.Contains(allowed), $"allow-list entry no longer matches anything: {allowed}");
        }
    }

    // The importers and the gateway edit dialog are the sites BL-0095 converted. Naming them
    // here means a revert reddens with the reason rather than with a bare count.
    [Theory]
    [InlineData("Services/Import/ProfileImportService.cs")]
    [InlineData("Services/Import/OpenSshConfigImporter.cs")]
    [InlineData("Views/Dialogs/ServerDialog.xaml.cs")]
    public void ConvertedSites_StillUseTheLockedWrite(string relativePath)
    {
        string path = Path.Combine(AppSourceRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"source not found: {path}");

        string text = File.ReadAllText(path);
        Assert.False(
            CallsSaveSettings(text),
            $"{relativePath} went back to writing the whole settings object; BL-0095 converted "
            + "it because a load-then-write window there erases concurrent writes.");
        Assert.Contains("MergeSettingAsync", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayEditCommit_ResolvesThePositionInTheListItIsGiven()
    {
        AppSettings fresh = new();
        fresh.SshGateways.Add(new SshGatewayDto { Id = "gw-added-meanwhile", Name = "Meanwhile" });
        fresh.SshGateways.Add(new SshGatewayDto { Id = "gw-edited", Name = "Before" });

        bool applied = GatewayEditCommit.Apply(
            fresh,
            "GW-EDITED",
            new SshGatewayDto { Id = "ignored", Name = "After" });

        Assert.True(applied);
        Assert.Equal(2, fresh.SshGateways.Count);
        Assert.Equal("Meanwhile", fresh.SshGateways[0].Name);
        Assert.Equal("After", fresh.SshGateways[1].Name);
        Assert.Equal("gw-edited", fresh.SshGateways[1].Id);
    }

    [Fact]
    public void GatewayEditCommit_DropsTheEditWhenTheGatewayWasDeletedMeanwhile()
    {
        AppSettings fresh = new();
        fresh.SshGateways.Add(new SshGatewayDto { Id = "gw-other", Name = "Other" });

        bool applied = GatewayEditCommit.Apply(
            fresh,
            "gw-deleted",
            new SshGatewayDto { Id = "gw-deleted", Name = "Resurrected" });

        Assert.False(applied);
        Assert.Equal("gw-other", Assert.Single(fresh.SshGateways).Id);
    }

    private static bool CallsSaveSettings(string text)
    {
        // A leading receiver is what separates a CALL from a declaration. Matching the bare
        // name flagged TwinShellBootstrapper, which merely implements the unrelated
        // ISettingsService.SaveSettingsAsync(UserSettings) - a false positive that would have
        // pushed an innocent file onto the allow-list and weakened the guard for good.
        return Regex.IsMatch(text, @"\.SaveSettingsAsync\s*\(", RegexOptions.None);
    }

    private static string AppSourceRoot()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string root = Path.Combine(repoRoot, "src", "Heimdall.App");
        Assert.True(Directory.Exists(root), $"application sources not found: {root}");
        return root;
    }

    private static IEnumerable<string> EnumerateAppSources()
    {
        return Directory
            .EnumerateFiles(AppSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
