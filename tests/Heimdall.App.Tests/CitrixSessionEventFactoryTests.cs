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
using FluentAssertions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="CitrixSessionEventFactory"/>, the pure builder behind the Citrix event
/// seam. Producers (verified on live tip, src/Heimdall.App/Views/EmbeddedCitrixView.xaml.cs):
/// connect emitted once from the real "connected" moments (EmbedWindow success after
/// <c>_embedded = true</c>, and ShowExternalFallback) - never the optimistic InitializeSession
/// status; disconnect funnelled through one idempotent <c>EmitDisconnect</c> from the health-poll
/// embedded-gone path ("remote"), the health-poll external-dead transition ("remote"),
/// <c>OnTerminateClick</c> ("user"), and the <c>Dispose</c> backstop ("teardown"). Citrix carries no
/// protocol reason code.
/// </summary>
public sealed class CitrixSessionEventFactoryTests
{
    [Fact]
    public void BuildConnected_SetsProtocolHostTitle_AndNoReasonOrDisconnectFields()
    {
        SessionEventRecord record = CitrixSessionEventFactory.BuildConnected(
            "https://store.example/Citrix/Store", "Notepad");

        record.Protocol.Should().Be("CITRIX");
        record.Kind.Should().Be(SessionEventKind.Connected);
        record.Host.Should().Be("https://store.example/Citrix/Store");
        record.Title.Should().Be("Notepad");
        record.ReasonKey.Should().BeNull();
        record.ReasonCode.Should().BeNull();
        record.DurationMs.Should().BeNull();
        record.EndTrigger.Should().BeNull();
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("user")]
    [InlineData("teardown")]
    public void BuildDisconnected_PassesThroughEndTrigger_WithNullReasonFields(string endTrigger)
    {
        SessionEventRecord record = CitrixSessionEventFactory.BuildDisconnected(
            "https://store.example/Citrix/Store", "Notepad", durationMs: 8_000, endTrigger);

        record.Protocol.Should().Be("CITRIX");
        record.Kind.Should().Be(SessionEventKind.Disconnected);
        record.DurationMs.Should().Be(8_000);
        record.EndTrigger.Should().Be(endTrigger);
        record.ReasonKey.Should().BeNull();
        record.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void BuildDisconnected_DefaultConnectInstant_YieldsNullDuration()
    {
        long? duration = GraphicalSessionEventHelpers.ResolveDurationMs(default, DateTime.UtcNow);

        SessionEventRecord record = CitrixSessionEventFactory.BuildDisconnected(
            "https://store.example/Citrix/Store", "Notepad", duration, "teardown");

        record.DurationMs.Should().BeNull();
    }

    [Theory]
    [InlineData("admin@store.example", "store.example")]
    [InlineData("store.example", "store.example")]
    public void BuildConnected_StripsUserPrefixFromHost(string rawHost, string expected)
    {
        CitrixSessionEventFactory.BuildConnected(rawHost, "Notepad").Host.Should().Be(expected);
    }

    [Fact]
    public void BuildConnected_EmptyHost_FallsBackToTitle()
    {
        CitrixSessionEventFactory.BuildConnected(rawHost: "  ", title: "Notepad")
            .Host.Should().Be("Notepad");
    }

    [Fact]
    public void BuildConnected_StoreFrontUrl_RemovesQueryAndFragmentButPreservesEndpoint()
    {
        const string rawUrl = "https://store.example:8443/path/?access_token=secret#fragment";

        SessionEventRecord record = CitrixSessionEventFactory.BuildConnected(rawUrl, "Notepad");

        record.Host.Should().Be("https://store.example:8443/path/");
        record.Host.Should().NotContain("access_token");
        record.Host.Should().NotContain("secret");
        record.Host.Should().NotContain("fragment");
    }

    [Fact]
    public void BuildDisconnected_StoreFrontUrl_RemovesQueryAndFragmentButPreservesEndpoint()
    {
        const string rawUrl = "https://store.example:8443/path/?access_token=secret#fragment";

        SessionEventRecord record = CitrixSessionEventFactory.BuildDisconnected(
            rawUrl,
            "Notepad",
            durationMs: 8_000,
            endTrigger: "remote");

        record.Host.Should().Be("https://store.example:8443/path/");
        record.Host.Should().NotContain("access_token");
        record.Host.Should().NotContain("secret");
        record.Host.Should().NotContain("fragment");
    }

    /// <summary>
    /// Source-level guard, not a behavioural one: <c>EmbedWindow</c> needs an STA thread and a live
    /// Citrix window, so the ordering cannot be exercised from a unit test. It is asserted on the
    /// text of the view instead.
    /// </summary>
    /// <remarks>
    /// The contract is that Connected is emitted only once the embedding is verified. Emitting it
    /// before the verdict is not merely early: <c>EmitConnect</c> is idempotent, so a later
    /// fallback can no longer emit it and the session log permanently records an embed that failed.
    /// </remarks>
    [Fact]
    public void EmbedWindow_EmitsConnectedOnlyAfterTheEmbeddingVerdict()
    {
        List<string> body = ReadEmbedWindowBody();

        int verdictLine = body.FindIndex(line => line.Contains("CitrixEmbedVerification.Verify(", StringComparison.Ordinal));
        int failureReturnLine = body.FindIndex(line => line.Contains("ShowExternalFallback();", StringComparison.Ordinal));
        List<int> emitLines = body
            .Select((line, index) => (line, index))
            .Where(entry => entry.line.Contains("EmitConnect();", StringComparison.Ordinal))
            .Select(entry => entry.index)
            .ToList();

        verdictLine.Should().BeGreaterThan(-1, "the embedding verdict must be computed inside EmbedWindow");
        failureReturnLine.Should().BeGreaterThan(verdictLine, "the failure branch must follow the verdict");
        emitLines.Should().ContainSingle("Connected must be emitted from exactly one place in EmbedWindow");
        emitLines[0].Should().BeGreaterThan(
            failureReturnLine,
            "Connected must be emitted only on the success path, after the failure branch has returned");
    }

    /// <summary>
    /// Lines of the <c>EmbedWindow</c> method body, matched line by line: the repository stores
    /// CRLF, so an anchored multiline regex over the raw text would silently never match and the
    /// guard would pass without measuring anything.
    /// </summary>
    private static List<string> ReadEmbedWindowBody()
    {
        string viewPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Heimdall.App",
            "Views",
            "EmbeddedCitrixView.xaml.cs");

        string[] lines = File.ReadAllLines(viewPath);
        int start = Array.FindIndex(lines, line => line.Contains("private void EmbedWindow(", StringComparison.Ordinal));
        start.Should().BeGreaterThan(-1, "EmbedWindow must exist in the Citrix view");

        List<string> body = [];
        int depth = 0;
        bool opened = false;

        for (int index = start; index < lines.Length; index++)
        {
            body.Add(lines[index]);
            depth += lines[index].Count(character => character == '{');
            depth -= lines[index].Count(character => character == '}');

            if (!opened && depth > 0)
            {
                opened = true;
            }
            else if (opened && depth == 0)
            {
                break;
            }
        }

        return body;
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
            $"Cannot find the repository root containing Heimdall.slnx from {AppContext.BaseDirectory}.");
    }
}
