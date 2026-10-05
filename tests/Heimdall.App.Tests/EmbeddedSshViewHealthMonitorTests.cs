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
/// The health monitor polls the shell session's SSH client. The session
/// disposes that client when it disconnects, so a monitor left running past
/// the disconnect polls a disposed client until the tab is closed.
/// </summary>
/// <remarks>
/// The view cannot be constructed under test without the WPF host and a
/// WebView2 runtime, so the wiring is read from the source: the disconnect
/// handler is the one place every disconnect passes through, and the guard
/// asserts the stop is a statement of its own there, not folded into a branch.
/// </remarks>
public sealed class EmbeddedSshViewHealthMonitorTests
{
    private const string ViewSourcePath = "Views/EmbeddedSshView.xaml.cs";
    private const string DisconnectHandlerSignature = "private void OnDisconnected(SshSessionDisconnectInfo disconnectInfo)";
    private const string StopStatement = "StopHealthMonitor();";

    [Fact]
    public void OnDisconnected_StopsTheHealthMonitor()
    {
        string source = ReadAppSource(ViewSourcePath);
        string body = ExtractMethodBody(source, DisconnectHandlerSignature);

        IReadOnlyList<string> stopStatements = body
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => string.Equals(line, StopStatement, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            stopStatements.Count == 1,
            $"Expected exactly one '{StopStatement}' statement in OnDisconnected, found {stopStatements.Count}. "
            + "A disconnect that leaves the monitor running polls a disposed SSH client until the tab closes.");
    }

    [Theory]
    [InlineData("ReleaseSleepPrevention();")]
    [InlineData("StopKeepAliveTimer();")]
    public void OnDisconnected_ReleasesWhatOnlyALiveSessionNeeds(string statement)
    {
        string body = ExtractMethodBody(ReadAppSource(ViewSourcePath), DisconnectHandlerSignature);

        int count = body
            .Split('\n')
            .Select(static line => line.Trim())
            .Count(line => string.Equals(line, statement, StringComparison.Ordinal));

        Assert.True(
            count == 1,
            $"Expected exactly one '{statement}' statement in OnDisconnected, found {count}. "
            + "A tab left open on a dead session kept the machine awake and kept sending keep-alives.");
    }

    private const string PolicyGate = "if (!TerminalSurfaceFailurePolicy.IsFatal(e.ProcessFailedKind))";

    private const string FallbackCall = "ShowWebViewUnavailable(";

    [Fact]
    public void OnWebViewProcessFailed_ConsultsThePolicyBeforeBlankingTheTerminal()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadAppSource(ViewSourcePath)),
            "private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)");

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, PolicyGate), "The process failure handler does not consult the policy.");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, FallbackCall), "The handler no longer falls back for a fatal failure.");
        Assert.True(
            logic.IndexOf(PolicyGate, StringComparison.Ordinal) < logic.IndexOf(FallbackCall, StringComparison.Ordinal),
            "The terminal is blanked before the policy is consulted.");
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Signature not found: {signature}");

        int openBrace = source.IndexOf('{', signatureIndex);
        Assert.True(openBrace >= 0, $"No body found for: {signature}");

        int depth = 0;
        for (int index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[openBrace..(index + 1)];
                }
            }
        }

        Assert.Fail($"Unbalanced braces while reading the body of: {signature}");
        return string.Empty;
    }

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Heimdall.App",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from {AppContext.BaseDirectory}.");
    }
}
