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
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// VNC and Citrix panes act on their own pane, as RDP, SSH and SFTP already did. In a split they
/// used to close the whole tab, reconnect the primary's server and write the primary's status.
/// </summary>
public sealed class VncCitrixPaneRoutingTests
{
    private const string ManagerPath = "Services/EmbeddedSessionManager.cs";

    [Fact]
    public void WriteHostStatus_ASecondaryPane_KeepsItsStatusToItself()
    {
        SessionPaneModel primary = new() { Status = SessionStatusTokens.Connected };
        SessionPaneModel secondary = new() { Status = SessionStatusTokens.Connected };
        SessionTabViewModel tab = new() { RootContent = new SplitContainerModel { First = primary, Second = secondary } };

        EmbeddedSessionManager.WriteHostStatus(tab, secondary, SessionStatusTokens.Disconnected);

        Assert.Equal(SessionStatusTokens.Disconnected, secondary.Status);
        Assert.Equal(SessionStatusTokens.Connected, primary.Status);
    }

    [Fact]
    public void WriteHostStatus_ThePrimaryOrAnUnboundHost_WritesTheTab()
    {
        SessionTabViewModel tab = new() { Status = SessionStatusTokens.Connected };

        EmbeddedSessionManager.WriteHostStatus(tab, null, SessionStatusTokens.Disconnected);
        Assert.Equal(SessionStatusTokens.Disconnected, tab.Status);

        EmbeddedSessionManager.WriteHostStatus(tab, tab.PrimaryPane, SessionStatusTokens.Connected);
        Assert.Equal(SessionStatusTokens.Connected, tab.Status);
    }

    [Theory]
    [InlineData("view.RequestReconnect += (_) => RequestHostReconnect(tab, view.OwningPane);")]
    [InlineData("view.RequestClose += (_) => RequestHostClose(tab, view.OwningPane);")]
    public void TheVncOverlay_GoesThroughThePane(string statement)
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadSource(ManagerPath)),
            "private void WireVncReconnectRequested(EmbeddedVncView view, SessionTabViewModel tab)");

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, statement), $"Missing: {statement}");
    }

    [Fact]
    public void NoVncOrCitrixHost_StillClosesItsWholeTab()
    {
        string manager = ReadSource(ManagerPath);

        Assert.DoesNotContain("view.CloseRequested += () => CloseRequestedCallback?.Invoke(sessionTab);", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("view.RequestClose += (_) => CloseRequestedCallback?.Invoke(tab);", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void AUserDisconnect_ReportsTheSessionEnded()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadSource("Views/EmbeddedVncView.xaml.cs")),
            "private void OnDisconnectClick(object sender, RoutedEventArgs e)");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, "SessionDisconnected?.Invoke("),
            "A user disconnect leaves the tab and the connection state reading Connected.");
    }

    private static string ReadSource(string relativePath)
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
