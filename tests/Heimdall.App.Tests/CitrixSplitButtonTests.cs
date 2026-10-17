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
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// A Citrix session splits from its header like the SSH, VNC and web ones. The tab menu already
/// offered Split for Citrix; its header had no button for it.
/// </summary>
public sealed class CitrixSplitButtonTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheHeaderHasTheSplitButtonTheVncHeaderHas()
    {
        XElement citrix = SplitButton("EmbeddedCitrixView.xaml");
        XElement vnc = SplitButton("EmbeddedVncView.xaml");

        Assert.Equal((string?)vnc.Attribute("Content"), (string?)citrix.Attribute("Content"));
        Assert.Equal("OnSplitClick", (string?)citrix.Attribute("Click"));
    }

    [Fact]
    public void TheButtonRaisesTheSplitRequest()
    {
        string source = File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedCitrixView.xaml.cs"));
        string logic = ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), SplitClickSignature);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, RaiseSplitStatement), "the Split button raises nothing");
    }

    /// <summary>The session manager hands the request to the split the tab menu uses.</summary>
    [Fact]
    public void TheSessionManagerForwardsTheCitrixSplitRequest()
    {
        string source = File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Services", "EmbeddedSessionManager.cs"));
        string logic = ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), WireCitrixSignature);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ForwardSplitStatement), "the Citrix split request is not forwarded");
    }

    private const string SplitClickSignature = "private void OnSplitClick(";
    private const string RaiseSplitStatement = "RequestSplit?.Invoke(_sessionTab);";
    private const string WireCitrixSignature = "private void WireCitrixView(";
    private const string ForwardSplitStatement = "view.RequestSplit += (_) => SplitRequestedCallback?.Invoke(sessionTab);";

    private static XElement SplitButton(string view) =>
        Assert.Single(
            XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", view)).Descendants(),
            e => e.Name.LocalName == "Button" && (string?)e.Attribute(Xaml + "Name") == "SplitButton");
}
