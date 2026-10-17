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
using System.Xml;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Every <c>AutomationProperties.LiveSetting</c> under <c>src/Heimdall.App</c> is raised by
/// something: <c>LiveRegionBehavior</c> in the markup, or a call in code that the baseline names.
/// </summary>
/// <remarks>
/// <para>In WPF a LiveSetting publishes a property and nothing else. A screen reader hears a
/// region change only when <c>LiveRegionChanged</c> is raised on the element's peer. The census
/// of 2026-10-17 found 97 elements declaring the setting with nothing raising it: 10 were raised
/// from code, 25 carried status or errors and now carry the behavior, and 62 were bulk output
/// (grids, lists, text boxes, logs) whose setting was removed, because announcing a result grid
/// or a per-keystroke preview would flood a screen reader rather than inform it.</para>
/// <para>The exemptions live in <c>live-regions-raised-from-code.baseline.txt</c>, in the shape
/// of <c>bare-source-assertions.baseline.txt</c>: a list that may only shrink, with a companion
/// test that fails when an entry describes nothing. Each entry cites the statement that raises
/// the event, and that statement is read through
/// <see cref="ViewSource.IsStatementOfTheMethodBody"/>, never as a bare fragment.</para>
/// </remarks>
public sealed class LiveRegionCensusGuardTests
{
    private const string BaselineFile = "live-regions-raised-from-code.baseline.txt";
    private const string LiveSetting = "AutomationProperties.LiveSetting";
    private const string Announce = "LiveRegionBehavior.AnnounceOnTargetUpdated";
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Eight live regions sat under <c>Views\Tools</c> on the day this was written, after the
    /// census removed the bulk ones. The floor leaves room for a tool or two to go; a scan that
    /// stops descending finds none and fails here.
    /// </summary>
    private const int ToolViewLiveRegionFloor = 6;

    [Fact]
    public void EveryLiveRegionIsRaisedByTheBehaviorOrByDeclaredCode()
    {
        HashSet<string> declared = ReadBaseline().Select(entry => entry.Site).ToHashSet(StringComparer.Ordinal);

        List<string> arrivals = Scan()
            .Where(region => !region.CarriesBehavior)
            .Select(region => region.Site)
            .Where(site => !declared.Contains(site))
            .ToList();

        Assert.True(
            arrivals.Count == 0,
            $"{arrivals.Count} element(s) declare {LiveSetting} and nothing raises LiveRegionChanged "
                + "for them, so a screen reader hears nothing. Add "
                + "behaviors:" + Announce + "=\"True\" (and NotifyOnTargetUpdated=True on the Text "
                + "binding), call LiveRegionBehavior.Announce where code writes the text, or remove "
                + "the setting from bulk output that would flood a screen reader:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, arrivals));
    }

    /// <summary>Without this the baseline only ever grows, and a repaired site keeps its permission.</summary>
    [Fact]
    public void TheBaselineHoldsNothingThatIsNoLongerABareLiveRegion()
    {
        HashSet<string> bare = Scan()
            .Where(region => !region.CarriesBehavior)
            .Select(region => region.Site)
            .ToHashSet(StringComparer.Ordinal);

        List<string> stale = ReadBaseline()
            .Select(entry => entry.Site)
            .Where(site => !bare.Contains(site))
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} baseline entries no longer describe a live region without the behavior. "
                + "Delete these lines from " + BaselineFile + ":"
                + Environment.NewLine
                + string.Join(Environment.NewLine, stale));
    }

    public static TheoryData<string> DeclaredSites()
    {
        TheoryData<string> data = [];
        foreach (BaselineEntry entry in ReadBaseline())
        {
            data.Add(entry.Site);
        }

        return data;
    }

    /// <summary>
    /// A declared site is exempt because code raises it, so the raising statement must stand as a
    /// step of the member the baseline names, inside the condition it names when there is one.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeclaredSites))]
    public void ADeclaredSiteIsRaisedWhereTheBaselineSays(string site)
    {
        BaselineEntry entry = Assert.Single(ReadBaseline(), e => e.Site == site);
        string source = ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(AppPath(entry.CodeFile)));
        string logic = ViewSource.HandlerBody(source, entry.Member);

        if (entry.Condition.Length > 0)
        {
            Assert.True(
                ViewSource.IsStatementOfTheMethodBody(logic, entry.Condition),
                $"{site}: {entry.Member} no longer stands on {entry.Condition}");
            logic = ViewSource.HandlerBody(logic, entry.Condition);
        }

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, entry.Statement),
            $"{site}: {entry.Member} does not raise the region with {entry.Statement}");
    }

    /// <summary>
    /// The tool views sit three folders below the scan root. A scan that stops at the top level
    /// would see none of them and still pass the census.
    /// </summary>
    [Fact]
    public void TheScanReachesTheToolViews()
    {
        List<LiveRegion> tools = Scan()
            .Where(region => region.File.StartsWith(@"Views\Tools\", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            tools.Count >= ToolViewLiveRegionFloor,
            $"only {tools.Count} live regions found under Views\\Tools: the scan is not reaching them");
        Assert.All(tools, region => Assert.True(region.CarriesBehavior, region.Site + " is bare"));
    }

    /// <summary>
    /// The style setters count as live regions too: the field error style, and the Polite to
    /// Assertive triggers of the two file browsers' status bars, which ride on their element.
    /// </summary>
    [Fact]
    public void TheScanReadsLiveSettingsSetByAStyle()
    {
        List<LiveRegion> setters = Scan().Where(region => region.IsSetter).ToList();

        Assert.True(setters.Count >= 3, $"only {setters.Count} style setters found: the scan is not reading styles");
        Assert.All(setters, region => Assert.True(region.CarriesBehavior, region.Site + " is bare"));
    }

    private static List<LiveRegion> Scan()
    {
        string appRoot = AppPath(string.Empty);
        List<LiveRegion> regions = [];

        foreach (string file in SourceFileEnumeration.EnumerateFiles(appRoot, "*.xaml"))
        {
            string relative = Path.GetRelativePath(appRoot, file);
            foreach (XElement element in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants())
            {
                if (Attribute(element, LiveSetting) is not null)
                {
                    regions.Add(new LiveRegion(relative, Identity(element), HasBehavior(element), IsSetter: false));
                }
                else if (element.Name.LocalName == "Setter"
                    && (string?)element.Attribute("Property") == LiveSetting)
                {
                    regions.Add(new LiveRegion(relative, SetterIdentity(element), SetterHasBehavior(element), IsSetter: true));
                }
            }
        }

        return regions;
    }

    private static bool HasBehavior(XElement element) =>
        string.Equals(Attribute(element, Announce), "True", StringComparison.Ordinal);

    /// <summary>
    /// A setter is raised when its own style sets the behavior, or when the style is the inline
    /// <c>X.Style</c> of an element that carries it.
    /// </summary>
    private static bool SetterHasBehavior(XElement setter)
    {
        XElement? style = setter.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Style");
        if (style is null)
        {
            return false;
        }

        bool styleSetsIt = style.Elements().Any(e => e.Name.LocalName == "Setter"
            && ((string?)e.Attribute("Property"))?.EndsWith(Announce, StringComparison.Ordinal) == true
            && (string?)e.Attribute("Value") == "True");
        if (styleSetsIt)
        {
            return true;
        }

        XElement? owner = style.Parent is { } property && property.Name.LocalName.EndsWith(".Style", StringComparison.Ordinal)
            ? property.Parent
            : null;
        return owner is not null && HasBehavior(owner);
    }

    private static string Identity(XElement element) =>
        (string?)element.Attribute(XName.Get("Name", XamlNamespace))
        ?? (string?)element.Attribute("Text")
        ?? element.Name.LocalName + " at line " + ((IXmlLineInfo)element).LineNumber;

    private static string SetterIdentity(XElement setter) =>
        "Setter at line " + ((IXmlLineInfo)setter).LineNumber;

    private static List<BaselineEntry> ReadBaseline() =>
        File.ReadAllLines(Path.Combine(ViewSource.RepoRoot(), "tests", "Heimdall.App.Tests", BaselineFile))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(BaselineEntry.Parse)
            .ToList();

    private static string AppPath(string file) =>
        Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", file);

    // An attached property in another XML namespace (behaviors:...) keeps only its local part.
    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private sealed record LiveRegion(string File, string Identity, bool CarriesBehavior, bool IsSetter)
    {
        public string Site => File + "|" + Identity;
    }

    private sealed record BaselineEntry(string Site, string CodeFile, string Member, string Condition, string Statement)
    {
        private const int FieldCount = 6;

        public static BaselineEntry Parse(string line)
        {
            string[] fields = line.Split('|');
            Assert.True(fields.Length == FieldCount, $"{BaselineFile}: expected {FieldCount} fields in: {line}");
            return new BaselineEntry(fields[0] + "|" + fields[1], fields[2], fields[3], fields[4], fields[5]);
        }
    }
}
