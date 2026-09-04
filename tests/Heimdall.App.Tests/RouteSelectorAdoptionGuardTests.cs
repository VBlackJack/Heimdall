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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Every tool view that offers a "Route via" combo goes through the shared selector: built as a
/// step of <c>Initialize</c>, released as a step of <c>Dispose</c>, and with none of the old
/// per-view machinery left behind.
/// </summary>
/// <remarks>
/// <para>Sixteen views each held a private copy of the gateway list and a private handler that
/// dialled a snapshot DTO. The defect was the shape, not any one file, and a seventeenth view
/// written from an old one would bring the shape back. This finds the combo in the markup and
/// asks the code-behind about it, so a new view inherits the rule the day it declares the
/// combo.</para>
/// <para>The presence checks go through <see cref="ViewSource.IsStatementOfTheMethodBody"/>, as
/// the source-reading guard requires: a construction folded behind an always-false condition
/// keeps the text and loses the behaviour, and a bare substring search would not notice.</para>
/// </remarks>
public sealed class RouteSelectorAdoptionGuardTests
{
    private const string ToolViewsRelativePath = "src/Heimdall.App/Views/Tools";
    private const string ComboDeclaration = "x:Name=\"CmbRouteVia\"";

    [Fact]
    public void EveryViewWithARouteViaCombo_BuildsAndReleasesTheSharedSelector()
    {
        List<string> offenders = [];

        foreach (string markupPath in EnumerateRouteViaMarkups())
        {
            string codePath = markupPath + ".cs";
            string name = Path.GetFileName(codePath);
            string code = File.ReadAllText(codePath);
            string logic = ViewSource.WithoutCommentsAndLiterals(code);

            string initialize = ViewSource.HandlerBody(logic, "public void Initialize(");
            if (!ViewSource.IsStatementOfTheMethodBody(initialize, "_routeSelector = new GatewayRouteSelector("))
            {
                offenders.Add($"{name}: Initialize does not build the shared selector as a step of its body");
            }

            string dispose = ViewSource.HandlerBody(logic, "public void Dispose()");
            if (!ViewSource.IsStatementOfTheMethodBody(dispose, "_routeSelector?.Dispose()"))
            {
                offenders.Add($"{name}: Dispose does not release the selector as a step of its body");
            }

            // The old shape, forbidden by text: a private gateway list, a per-view populate
            // method, a per-view selection handler in the markup.
            Assert.DoesNotContain("_gateways", logic, StringComparison.Ordinal);
            Assert.DoesNotContain("PopulateRouteSelector", logic, StringComparison.Ordinal);
            Assert.DoesNotContain("OnRouteViaChanged", File.ReadAllText(markupPath), StringComparison.Ordinal);
        }

        Assert.True(
            offenders.Count == 0,
            "These views declare a Route via combo without going through GatewayRouteSelector:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  " + o)));
    }

    [Fact]
    public void TheScanReachesTheSixteenViews()
    {
        // Guarding the guard: a moved directory or a renamed combo would otherwise turn the
        // assertion above into a permanent, meaningless pass.
        string[] markups = [.. EnumerateRouteViaMarkups()];

        Assert.True(markups.Length >= 16, $"Expected at least 16 views with a Route via combo, found {markups.Length}.");
        Assert.Contains(markups, m => Path.GetFileName(m) == "PortScannerView.xaml");
        Assert.Contains(markups, m => Path.GetFileName(m) == "NetworkCartographyView.xaml");
    }

    [Fact]
    public void ThePredicateRejectsAConstructionFoldedBehindACondition()
    {
        // And the other way: the predicate must fail on the shape it forbids, or the guard above
        // measures nothing.
        const string Folded = """
            public void Initialize(ToolContext? context, LocalizationManager? localizer)
            {
                if (context is null && context is not null)
                {
                    _routeSelector = new GatewayRouteSelector(CmbRouteVia, context, L, OnGatewaySelected, ReportRouteStatus);
                }
            }
            """;

        Assert.False(ViewSource.IsStatementOfTheMethodBody(
            ViewSource.WithoutCommentsAndLiterals(Folded),
            "_routeSelector = new GatewayRouteSelector("));
    }

    private static IEnumerable<string> EnumerateRouteViaMarkups()
    {
        string dir = Path.Combine(
            ViewSource.RepoRoot(),
            ToolViewsRelativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(Directory.Exists(dir), $"Tool views directory not found: {dir}");

        return Directory.EnumerateFiles(dir, "*.xaml", SearchOption.TopDirectoryOnly)
            .Where(path => File.ReadAllText(path).Contains(ComboDeclaration, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);
    }
}
