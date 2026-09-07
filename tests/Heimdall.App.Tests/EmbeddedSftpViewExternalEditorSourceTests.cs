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

    private const string ApplyNoticeStatement =
        "ApplyTransportSecurityNotice(_viewModel, _browser);";

    private const string NoticeKeyMember =
        "internal static string? TransportSecurityNoticeKey(";

    private const string NoticeKeyStatement =
        "return GetFtpSecurityNoticeLocalizationKey(ftpBrowser.IsTlsEnabled);";

    private const string ApplyNoticeMember =
        "internal static void ApplyTransportSecurityNotice(";

    private const string RaiseStatement = "viewModel.ShowSecurityNoticeKey(key);";

    /// <summary>
    /// Every session runs the transport-notice step, at the body level of InitializeSession.
    /// </summary>
    /// <remarks>
    /// SFTP-001. The FTPS data-channel limitation cannot be fixed inside Heimdall, so the
    /// disclosure IS the remedy and its wiring is load bearing. It used to sit inside an
    /// else-if arm two braces deep: the statement predicate could not reach it, and deleting
    /// it removed the badge from every FTPS session while the suite stayed green.
    /// </remarks>
    [Fact]
    public void InitializeSession_RunsTheTransportSecurityNoticeStep()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(SftpViewSource()),
            InitializeSessionMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, ApplyNoticeStatement),
            "the transport-security notice is applied for every session, not inside a branch");
    }

    /// <summary>
    /// The notice a session gets is derived from that session's own TLS setting.
    /// </summary>
    /// <remarks>
    /// An exact-argument anchor rather than a behavioural test, because the two keys differ
    /// only by the boolean: a mutant that negates it produces a perfectly valid key and a
    /// perfectly wrong badge, telling a plaintext FTP user their data channel is encrypted.
    /// </remarks>
    [Fact]
    public void TheTransportNotice_IsDerivedFromTheSessionsOwnTlsSetting()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(SftpViewSource()),
            NoticeKeyMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, NoticeKeyStatement),
            "the key follows the session's own IsTlsEnabled, not a constant");
    }

    /// <summary>
    /// The decision is not merely computed, it is handed to the view model.
    /// </summary>
    [Fact]
    public void TheTransportNotice_IsHandedToTheViewModel()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(SftpViewSource()),
            ApplyNoticeMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, RaiseStatement),
            "computing the key and never raising it would leave every FTPS pane silent");
    }

    /// <summary>
    /// The wiring guard dies when the call it guards is commented out.
    /// </summary>
    /// <remarks>
    /// The positive control. A presence assertion of this shape dies on deletion but not on
    /// folding, and this repository has recorded the commented-out call as its wiring mutant.
    /// Written so the control fails together with the oracle it protects rather than
    /// surviving it.
    /// </remarks>
    [Fact]
    public void TheWiringGuard_FailsOnACommentedOutCall()
    {
        string original = SftpViewSource();
        string mutated = original.Replace(
            "        " + ApplyNoticeStatement,
            "        // " + ApplyNoticeStatement,
            StringComparison.Ordinal);

        Assert.NotEqual(original, mutated);

        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(mutated),
            InitializeSessionMember);

        Assert.False(
            ViewSource.IsStatementOfTheMethodBody(logic, ApplyNoticeStatement),
            "the guard would not notice its own call being commented out");
    }
}
