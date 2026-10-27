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

namespace Heimdall.App.Tests;

/// <summary>
/// Refuses a service lookup of <c>AppSettings</c> anywhere in the application sources.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppSettings</c> is not registered in the container: the settings live behind
/// <c>ConfigManager</c>, which publishes a snapshot through <c>CurrentSettings</c>. A lookup of
/// <c>AppSettings</c> therefore compiles, runs without error and always returns null, so whatever
/// it was meant to restore silently falls back to its default. The Notes sidebar width was saved
/// on every resize and never read back for exactly that reason, and the Command Library bottom
/// panel shipped the same defect before its hand pass caught it.
/// </para>
/// <para>
/// The lookup never fails, so no behaviour test reaches it short of a full startup; the source is
/// where it is checked.
/// </para>
/// </remarks>
public sealed class AppSettingsServiceLookupGuardTests
{
    private static readonly Regex Lookup = new(
        @"Get(Required)?Service\s*<\s*(Heimdall\.Core\.Configuration\.)?AppSettings\s*>",
        RegexOptions.CultureInvariant);

    private static readonly Regex Registration = new(
        @"Add(Singleton|Scoped|Transient)\s*<\s*(Heimdall\.Core\.Configuration\.)?AppSettings\s*>",
        RegexOptions.CultureInvariant);

    [Fact]
    public void NoSourceLooksUpAppSettingsInTheContainer()
    {
        List<string> offenders = SourceFiles()
            .Where(f => Lookup.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(FindRepoRoot(), f))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The premise of the guard. If AppSettings is ever registered, the lookup becomes legal and
    /// this guard must be revisited rather than left refusing correct code.
    /// </summary>
    [Fact]
    public void AppSettingsIsNotRegisteredInTheContainer()
    {
        Assert.DoesNotContain(SourceFiles(), f => Registration.IsMatch(File.ReadAllText(f)));
    }

    /// <summary>
    /// The control: the sweep is recursive and reaches the nested view that held the defect.
    /// Without it, a sweep that stopped at the top directory would pass over everything.
    /// </summary>
    [Fact]
    public void TheSweepReachesNestedViews()
    {
        Assert.Contains(SourceFiles(), f => f.EndsWith(
            Path.Combine("Views", "Tools", "NotesToolView.xaml.cs"), StringComparison.Ordinal));
    }

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(
            Path.Combine(FindRepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static string FindRepoRoot()
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
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
