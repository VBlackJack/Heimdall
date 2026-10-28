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
using System.Reflection;
using System.Text.RegularExpressions;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The names of the tools detected on the machine ("Autorunsc", "Event Log View") reach the
/// screens without being looked up in the locale as if they were keys.
/// </summary>
/// <remarks>
/// A detected tool's descriptor carries the product name in the field that holds a locale key
/// for a built-in tool. Every list that showed it looked it up, the locale missed, the miss was
/// logged, and the name came back unchanged: nothing wrong on screen, a missing translation
/// in the log at each start.
/// </remarks>
public sealed class DetectedToolNameLookupTests
{
    private static readonly string[] DetectedNames = ["Autorunsc", "Event Log View"];

    /// <summary>
    /// The palette is the one list reachable without a window: its item builder and its
    /// scorer both read the name.
    /// </summary>
    [Fact]
    public async Task ThePaletteShowsAndScoresDetectedToolsWithoutAMissedLookup()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var palette = new CommandPaletteViewModel(null!, localizer, null!, null!, null!, null!, null!, null!, null!);
        var registry = new ToolRegistry();
        registry.RegisterExternalTools(
        [
            .. DetectedNames.Select(name => new ExternalToolInfo
            {
                Id = name.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
                Name = name,
                ExecutablePath = @"C:\Tools\" + name + ".exe",
                ProviderName = "Sysinternals"
            })
        ]);
        var detected = registry.All.Where(descriptor => descriptor.ExternalProviderName is not null).ToList();
        Assert.Equal(DetectedNames.Length, detected.Count);

        foreach (var descriptor in detected)
        {
            var item = (ServerItemViewModel)Invoke(palette, "BuildToolPaletteItem", descriptor)!;
            Assert.Equal(descriptor.LabelKey, item.DisplayName);
            Assert.True((int)Invoke(palette, "ScoreToolDescriptor", descriptor, descriptor.LabelKey)! > 0);
        }

        Assert.DoesNotContain(localizer.MissedKeys, key => DetectedNames.Contains(key, StringComparer.Ordinal));
    }

    /// <summary>
    /// The same decision is made at a dozen sites; none may feed a descriptor's label to the
    /// locale directly, which is what each of them did.
    /// </summary>
    [Fact]
    public void NoSiteLooksADescriptorLabelUpAsAKey()
    {
        var root = FindRepoRoot();
        var lookup = new Regex(@"(_localizer\[|\.Localize\(|LocalizeOrRaw\()\s*\w+\.LabelKey\b", RegexOptions.CultureInvariant);
        var resolution = new Regex(@"\.ResolveLabel\(", RegexOptions.CultureInvariant);
        List<string> offenders = [];
        var resolving = 0;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (resolution.IsMatch(text))
            {
                resolving++;
            }

            if (lookup.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.True(resolving >= 5, $"only {resolving} files resolve a descriptor label");
        Assert.True(offenders.Count == 0, "a descriptor label is looked up as a key in: " + string.Join(", ", offenders));
    }

    private static object? Invoke(CommandPaletteViewModel palette, string method, params object[] arguments)
    {
        var info = typeof(CommandPaletteViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        return info.Invoke(palette, arguments);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
