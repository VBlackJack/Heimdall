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
/// Freezes the single word the session tree's containers are called, in both shipped locales and
/// in the public documentation.
/// </summary>
/// <remarks>
/// <para>The product called one thing by two names. "New Folder" on the Add menu opened a dialog
/// headed "New group"; the tree context menu offered "New group" while the status line that
/// followed reported a folder created; the confirmation for deleting one spoke of subgroups and
/// promised to move the entries to "No Group", a node the tree drew as "(No Group)" and the
/// server dialog labelled "Folder". A newcomer had to work out that the two words named one thing.
/// </para>
/// <para>Only the values move. Key names such as <c>TreeCtxNewGroup</c> are identifiers: renaming
/// them would touch every reference, churn the XAML, and teach nobody anything, and the persisted
/// shape behind them - <c>ServerProfileDto.Group</c>, <c>AppSettings.EmptyGroups</c>, and the JSON
/// on disk in every profile - is deliberately untouched. This is a wording migration.</para>
/// <para>The word "group" keeps a legitimate home in this product, which is why
/// <see cref="NothingElseCallsAContainerAGroup"/> asserts a partition rather than mere absence. A
/// sweep that replaced every occurrence would render a regular-expression capture group as
/// "Folder 1" and a Unix permission class as a folder. Those keys are named here so the sweep
/// cannot reach them, and so the exemption cannot be quietly widened either.</para>
/// </remarks>
public sealed class SessionTreeVocabularyTests
{
    // Vacuity guard, same reasoning as DeadLocaleKeyGuardTests: a locale read that silently
    // returned an empty map would let every assertion below pass having measured nothing.
    private const int MinExpectedKeys = 5000;

    // A hand-listed exclusion set nobody can rename away is a permanent pardon, so the doc scan
    // asserts every exclusion still exists, and that it read a plausible number of files.
    private const int MinExpectedDocuments = 20;

    /// <summary>
    /// Every key whose value names the session tree's container. Their key names still say
    /// "Group"; their values must not.
    /// </summary>
    private static readonly string[] ContainerKeys =
    {
        "A11yServerGroup",
        "ConfirmConnectAllMessage",
        "DetailLabelGroup",
        "FilterAllGroups",
        "GatewayOverviewGroupDefaultReferenceType",
        "MoveToFieldGroup",
        "NewGroupDialogTitle",
        "NewGroupFieldName",
        "RenameGroupDialogTitle",
        "RenameGroupErrorInvalidSegment",
        "RenameGroupErrorPersistence",
        "RenameGroupErrorSiblingCollision",
        "RenameGroupFieldNew",
        "ServerFieldGroup",
        "StatusGroupCreated",
        "StatusGroupRenamed",
        "TooltipHelpGroup",
        "TooltipHelpTags",
        "TreeCtxDeleteGroup",
        "TreeCtxDeleteGroupConfirm",
        "TreeCtxMoveToGroup",
        "TreeCtxNewGroup",
        "TreeCtxRenameGroup",
        "TreeNodeNoGroup",
        "TreeNoGroupDropZoneHint",
        "TreeTooltipGroupCount",
    };

    /// <summary>
    /// The keys where "group" is a different concept entirely and must survive the migration
    /// intact, in both locales.
    /// </summary>
    private static readonly string[] ForeignGroupKeys =
    {
        "SftpPropertiesGroup",  // the Unix owning group of a remote file
        "ToolChmodGroup",       // the Unix permission class
        "ToolHelpCHMOD",        // "owner, group, and others"
        "ToolHelpREGEX",        // "capture group details"
        "ToolRegexGroupEntry",  // "Group {0}: ..." in the regex tester's match list
    };

    /// <summary>
    /// The Windows workgroup. Listed apart from <see cref="ForeignGroupKeys"/> because only the
    /// French rendering contains the word: English writes "Workgroup" as one token, which the
    /// pattern below does not match. These two may say it rather than must.
    /// </summary>
    private static readonly string[] WorkgroupKeys =
    {
        "ToolHelpNETMAP",
        "ToolNetMapTipDomain",
    };

    /// <summary>
    /// Refused characters, kept identical to the documentation guard's list. Written as escapes so
    /// this file stays pure ASCII and survives whatever code page reads it.
    /// </summary>
    private static readonly Dictionary<char, string> RefusedCharacters = new()
    {
        ['\u2014'] = "em dash, use the ASCII hyphen -",
        ['\u2013'] = "en dash, use the ASCII hyphen -",
        ['\u2010'] = "unicode hyphen, use the ASCII hyphen -",
        ['\u2011'] = "non-breaking hyphen, use the ASCII hyphen -",
        ['\u2212'] = "minus sign, use the ASCII hyphen -",
        ['\u201C'] = "left curly quote, use the ASCII double quote",
        ['\u201D'] = "right curly quote, use the ASCII double quote",
        ['\u201E'] = "low double quote, use the ASCII double quote",
        ['\u2018'] = "left curly apostrophe, use the ASCII apostrophe",
        ['\u2019'] = "right curly apostrophe, use the ASCII apostrophe",
        ['\u00AB'] = "left guillemet, not on the AZERTY layout, use the ASCII double quote",
        ['\u00BB'] = "right guillemet, not on the AZERTY layout, use the ASCII double quote",
        ['\u0153'] = "oe ligature, not on the AZERTY layout, write oe",
        ['\u0152'] = "OE ligature, not on the AZERTY layout, write OE",
        ['\u00E6'] = "ae ligature, not on the AZERTY layout, write ae",
        ['\u00C6'] = "AE ligature, not on the AZERTY layout, write AE",
        ['\u2026'] = "single-character ellipsis, write three dots",
        ['\u00A0'] = "no-break space, use a plain space",
        ['\u202F'] = "narrow no-break space, use a plain space",
        ['\u2009'] = "thin space, use a plain space",
        ['\u200B'] = "zero-width space, delete it",
        ['\uFEFF'] = "byte order mark, delete it",
    };

    /// <summary>
    /// Documents left out of the vocabulary sweep, and the reason each one is out. None of them is
    /// a page someone reads to decide whether to use the product.
    /// </summary>
    private static readonly Dictionary<string, string> ExcludedDocuments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ARCHITECTURE.md"] = "names the code identifiers Group, GroupName and GroupDefaultsDto",
            ["CHANGELOG.md"] = "shipped history, which a wording migration does not rewrite",
            ["CI_FLAKY_TESTS.md"] = "quotes the GitHub Actions ##[group] log markers",
            ["SMOKE-TESTS.md"] = "names scripts/smoke/move-to-group-smoke.ps1, which this lot cannot rename",
        };

    // Matches the container noun in either language: group, groups, groupe, groupes. It leaves the
    // verb forms alone on purpose - "grouped by category" and "regroupes en Root / Dark" describe
    // an action, not the thing in the tree - and it does not fire on "workgroup" or "regroupement",
    // where no word boundary precedes the stem.
    private static readonly Regex s_containerNoun = new(
        @"\bgroupe?s?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Theory]
    [InlineData("en", "folder")]
    [InlineData("fr", "dossier")]
    public void EveryContainerStringNamesAFolder(string locale, string word)
    {
        Dictionary<string, string> values = ReadLocale(locale);

        List<string> offenders = new();
        foreach (string key in ContainerKeys)
        {
            // A key that vanished, or was mistyped here, would otherwise silently shrink the guard.
            Assert.True(
                values.ContainsKey(key),
                $"{locale}.json has no key {key}. Either the key was renamed - which this "
                + "migration deliberately does not do - or this list is stale.");

            if (!values[key].Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"{key} = \"{Excerpt(values[key])}\"");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} session-tree string(s) in {locale}.json do not call the container a "
            + $"\"{word}\". The tree, its context menu, the dialogs and the status line have to "
            + "agree on one word:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The migration and the concepts it must not touch are one partition, asserted as one oracle.
    /// Split apart, the second half could never fail: it holds today and would hold on the day
    /// someone swept every "group" in the file into "folder", because it is that sweep the first
    /// half detects.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void NothingElseCallsAContainerAGroup(string locale)
    {
        Dictionary<string, string> values = ReadLocale(locale);

        HashSet<string> saidGroup = values
            .Where(pair => s_containerNoun.IsMatch(pair.Value))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> permitted = ForeignGroupKeys
            .Concat(WorkgroupKeys)
            .ToHashSet(StringComparer.Ordinal);

        List<string> strays = saidGroup
            .Where(key => !permitted.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            $"{strays.Count} value(s) in {locale}.json still call something a group. If one of them "
            + "is the Unix permission class, the Unix owning group, a regex capture group or a "
            + "Windows workgroup, name it in ForeignGroupKeys or WorkgroupKeys with its reason; "
            + "otherwise it is the session tree's container, and that is a folder:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                strays.Take(40).Select(key => $"{key} = \"{Excerpt(values[key])}\"")));

        List<string> swept = ForeignGroupKeys
            .Where(key => !saidGroup.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            swept.Count == 0,
            $"{swept.Count} key(s) in {locale}.json lost the word \"group\" where it is the only "
            + "correct word. Calling a regex capture group or the Unix permission class a folder "
            + "makes the string wrong rather than consistent:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                swept.Select(key => $"{key} = \"{Excerpt(values[key])}\"")));
    }

    /// <summary>
    /// The French strings this migration rewrites carry no typographic substitute.
    /// </summary>
    /// <remarks>
    /// The guillemets were the concrete case: the rename and creation status lines quoted the
    /// folder name with them, and an AZERTY keyboard produces neither. The rest of fr.json still
    /// carries the same substitutes in around a hundred values. That is a larger debt this lot did
    /// not take, and widening the scope of this test is how it gets paid.
    /// </remarks>
    [Fact]
    public void TheFrenchContainerStringsUseNoTypographicSubstitutes()
    {
        Dictionary<string, string> values = ReadLocale("fr");

        List<string> violations = new();
        foreach (string key in ContainerKeys)
        {
            foreach (char character in values[key])
            {
                if (RefusedCharacters.TryGetValue(character, out string? remedy))
                {
                    violations.Add($"{key} contains U+{(int)character:X4} ({remedy})");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations.Take(40)));
    }

    /// <summary>
    /// The pages a reader consults before installing say the same word as the product does, in
    /// both languages, or the migration only half happened.
    /// </summary>
    [Fact]
    public void PublicDocumentationCallsThemFolders()
    {
        string root = RepoRoot();
        List<string> documents = PublicDocuments(root);

        // Guarding the guard, three ways: a glob that matched nothing, a sweep that never left the
        // English directory, and an exclusion whose file was renamed out from under it.
        Assert.True(
            documents.Count >= MinExpectedDocuments,
            $"only {documents.Count} public documents were scanned, so this guard read almost "
            + "nothing and would pass whatever the docs said");
        Assert.Contains(
            documents,
            path => path.Replace('\\', '/').Contains("/docs/fr/", StringComparison.Ordinal));

        foreach (KeyValuePair<string, string> excluded in ExcludedDocuments)
        {
            Assert.True(
                File.Exists(Path.Combine(root, "docs", excluded.Key))
                    || File.Exists(Path.Combine(root, "docs", "fr", excluded.Key)),
                $"docs/{excluded.Key} is excluded from this sweep ({excluded.Value}) but no longer "
                + "exists. Delete the entry rather than leave a pardon behind it.");
        }

        List<string> violations = new();
        foreach (string path in documents)
        {
            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                if (s_containerNoun.IsMatch(lines[index]))
                {
                    violations.Add($"{Relative(root, path)}:{index + 1}: {Excerpt(lines[index])}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} documentation line(s) still call the session tree's containers "
            + "groups. English and French move together or neither does:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations.Take(40)));
    }

    /// <summary>
    /// The UI smoke script drives the product by the words on screen, so it has to be told when
    /// they change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// scripts/smoke/move-to-group-smoke.ps1 looks menu items up by exact name. This vocabulary
    /// migration rewrote four of the strings it looks for, and every dotnet test lane stayed green
    /// because no lane runs that script: two green suites either side of a junction neither
    /// crosses, which is the shape that let an inert close guard ship here in August.
    /// </para>
    /// <para>
    /// The list is explicit rather than scanned out of the script, and that is deliberate. Most
    /// quoted strings in there are fixtures - folder names the script creates itself - so a scan
    /// would either demand they be locale values or need a heuristic to tell chrome from data.
    /// A short list that must be maintained is honest about what it covers; a clever scan that
    /// silently skips the string that matters is not.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSmokeScriptLooksForStringsTheProductStillRenders()
    {
        string root = RepoRoot();
        string scriptPath = Path.Combine(root, "scripts", "smoke", "move-to-group-smoke.ps1");
        Assert.True(
            File.Exists(scriptPath),
            $"the smoke script this guard is about is gone: {scriptPath}. Delete the guard with it "
                + "rather than leaving it asserting about nothing.");

        string script = File.ReadAllText(scriptPath);
        Dictionary<string, string> english = ReadLocale("en");

        // key -> the literal the script searches for. The script runs against the English build.
        (string Key, string Literal)[] lookups =
        [
            ("TreeCtxMoveToGroup", "Move to folder"),
            ("TreeNodeNoGroup", "(No Folder)"),
        ];

        List<string> problems = [];
        foreach ((string key, string literal) in lookups)
        {
            Assert.True(english.ContainsKey(key), $"{key} is missing from en.json");

            if (!string.Equals(english[key], literal, StringComparison.Ordinal))
            {
                problems.Add(
                    $"en.json[{key}] now reads \"{english[key]}\", but "
                        + $"scripts/smoke/move-to-group-smoke.ps1 still looks for \"{literal}\". "
                        + "Update the script, or the smoke run fails on a rename nobody sees.");
            }

            if (!script.Contains("'" + literal + "'", StringComparison.Ordinal))
            {
                problems.Add(
                    $"the smoke script no longer looks for \"{literal}\", so this guard is "
                        + $"watching a lookup that is gone. Re-point it at what the script reads "
                        + $"for {key}.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static List<string> PublicDocuments(string root)
    {
        string[] directories = { Path.Combine(root, "docs"), Path.Combine(root, "docs", "fr") };

        return directories
            .SelectMany(directory => Directory.EnumerateFiles(
                directory, "*.md", SearchOption.TopDirectoryOnly))
            .Where(path => !ExcludedDocuments.ContainsKey(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, string> ReadLocale(string locale)
    {
        Dictionary<string, string> values = JsonSerializer
            .Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(RepoRoot(), "locales", $"{locale}.json")))!;

        Assert.True(
            values.Count >= MinExpectedKeys,
            $"only {values.Count} keys read from {locale}.json, so the locale read failed");

        return values;
    }

    private static string Excerpt(string value)
    {
        string flattened = value.Replace("\r", string.Empty).Replace('\n', ' ');
        return flattened.Length <= 120 ? flattened : flattened[..120] + "...";
    }

    private static string Relative(string root, string path) =>
        path[(root.Length + 1)..].Replace('\\', '/');

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
