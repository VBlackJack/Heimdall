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

using System.Windows.Controls;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.Views.Tools;

namespace Heimdall.App.UiTests;

/// <summary>
/// The tool entries of a discovered host's menu follow the availability predicate: an entry whose
/// tool may not be used is not built, and no separator is left without a neighbour.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class HostActionsToolAvailabilityTests
{
    private const string Host = "10.0.0.5";

    private static readonly int[] TlsAndHttp = [443, 22];

    private static readonly string[] TheFiveTools = ["PING", "PORTSCAN", "DNS", "WHOIS", "CERT"];

    private static List<object> Build(Func<string, bool>? isToolAvailable, List<string>? opened = null)
    {
        return ToolContextMenuHelper.BuildHostActions(
            Host,
            "host-01",
            TlsAndHttp,
            WpfTestHost.Localizer,
            (toolId, _, _) => opened?.Add(toolId),
            openSessionCallback: null,
            addServerCallback: null,
            isToolAvailable: isToolAvailable);
    }

    private static bool Has(IEnumerable<object> items, string localeKey)
    {
        string label = WpfTestHost.Translate(localeKey);
        return items.OfType<MenuItem>().Any(item => string.Equals(item.Header as string, label, StringComparison.Ordinal));
    }

    private static void AssertNoDanglingSeparator(IReadOnlyList<object> items)
    {
        Assert.False(items.Count > 0 && items[0] is Separator, "the menu starts with a separator");
        Assert.False(items.Count > 0 && items[^1] is Separator, "the menu ends with a separator");
        for (int index = 1; index < items.Count; index++)
        {
            Assert.False(items[index] is Separator && items[index - 1] is Separator, "two separators side by side");
        }
    }

    [Fact]
    public async Task WithoutAPredicate_EveryToolEntryIsOffered()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            List<object> items = Build(isToolAvailable: null);

            Assert.True(Has(items, "ToolCtxOpenPing"));
            Assert.True(Has(items, "ToolCtxOpenPortScan"));
            Assert.True(Has(items, "ToolCtxOpenDns"));
            Assert.True(Has(items, "ToolCtxOpenWhois"));
            Assert.True(Has(items, "ToolCtxOpenCertInspector"));
            AssertNoDanglingSeparator(items);
        }).Task;
    }

    [Fact]
    public async Task APredicateRefusingOneTool_OmitsThatEntryOnly()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            List<object> items = Build(id => id != "PORTSCAN");

            Assert.False(Has(items, "ToolCtxOpenPortScan"));
            Assert.True(Has(items, "ToolCtxOpenPing"));
            Assert.True(Has(items, "ToolCtxOpenDns"));
            Assert.True(Has(items, "ToolCtxOpenWhois"));
            Assert.True(Has(items, "ToolCtxOpenCertInspector"));
            AssertNoDanglingSeparator(items);
        }).Task;
    }

    [Fact]
    public async Task APredicateRefusingEveryTool_LeavesNoToolEntryAndNoDanglingSeparator()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            List<object> items = Build(id => !TheFiveTools.Contains(id));

            Assert.False(Has(items, "ToolCtxOpenPing"));
            Assert.False(Has(items, "ToolCtxOpenPortScan"));
            Assert.False(Has(items, "ToolCtxOpenDns"));
            Assert.False(Has(items, "ToolCtxOpenWhois"));
            Assert.False(Has(items, "ToolCtxOpenCertInspector"));

            // The copy entries and the browser entry (port 443) remain.
            Assert.True(Has(items, "ToolCtxCopyIp"));
            Assert.True(items.OfType<MenuItem>().Count() >= 3);
            AssertNoDanglingSeparator(items);
        }).Task;
    }

    [Fact]
    public async Task AnEntryThatIsOffered_StillOpensItsTool()
    {
        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            List<string> opened = [];
            List<object> items = Build(id => id == "DNS", opened);

            MenuItem dns = Assert.Single(
                items.OfType<MenuItem>(),
                item => string.Equals(item.Header as string, WpfTestHost.Translate("ToolCtxOpenDns"), StringComparison.Ordinal));
            dns.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));

            Assert.Equal(["DNS"], opened);
        }).Task;
    }
}
