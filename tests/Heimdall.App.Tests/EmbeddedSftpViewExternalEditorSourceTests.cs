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
/// The SFTP view hands the resolved external editor to the remote editor, read from the source
/// through the statement predicate: the view needs a desktop, and the setting used to be read
/// by the local browser only while a remote file always opened in notepad.
/// </summary>
public sealed class EmbeddedSftpViewExternalEditorSourceTests
{
    private const string InitializeSessionMember = "public void InitializeSession(";

    private const string ResolveStatement =
        "string editorPath = EditorLaunchPolicy.ResolveExternalEditor(ExternalEditorPath, out string? editorRejectionKey);";

    private const string ConstructStatement =
        "_editor = new RemoteFileEditor(operationsBrowser, hostKeyStore: hostKeyStore, hostKeyVerifier: _hostKeyVerifier, editorPath: editorPath);";

    private const string SetNameStatement = "System.Windows.Automation.AutomationProperties.SetName(";

    [Fact]
    public void InitializeSession_ResolvesTheConfiguredEditorAndHandsItToTheRemoteEditor()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(SftpViewSource()),
            InitializeSessionMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, ResolveStatement),
            "the configured editor is resolved through the shared policy");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, ConstructStatement),
            "the resolved editor reaches the remote editor");
    }

    [Fact]
    public void SecurityNoticeBadge_KeepsItsAutomationNameBinding()
    {
        // Absence: the code-behind used to overwrite the badge's bound automation name with a
        // one-time value, so the name stopped following the notice.
        Assert.DoesNotContain(SetNameStatement, SftpViewSource(), StringComparison.Ordinal);
    }

    private static string SftpViewSource() => File.ReadAllText(Path.Combine(
        ViewSource.RepoRoot(),
        "src",
        "Heimdall.App",
        "Views",
        "EmbeddedSftpView.xaml.cs"));
}
