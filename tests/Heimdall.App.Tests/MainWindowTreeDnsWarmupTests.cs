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
using System.Net.Sockets;

namespace Heimdall.App.Tests;

/// <summary>
/// The DNS pre-warm the sessions tree fires when it shows a selection.
/// </summary>
/// <remarks>
/// The warm-up hangs off ShowTreeSelection, which also runs on a folder click and on the
/// right-click pre-selection. Neither moves the selection, so the same host was resolved again
/// with the answer already in the resolver cache. The other half is the scan: every selection
/// change fired a lookup nobody could cancel, so twenty rows crossed with the arrow keys started
/// twenty resolutions and ran them all to the end.
/// </remarks>
public sealed class MainWindowTreeDnsWarmupTests
{
    private const string TreeInteractionsRelativePath = @"src\Heimdall.App\MainWindow.TreeInteractions.cs";

    [Fact]
    public void ANewHost_IsWarmed()
    {
        DnsWarmupGate gate = new();

        Assert.True(gate.ShouldWarm("alpha.example.test"));
    }

    [Fact]
    public void TheHostAlreadyWarmed_IsNotWarmedAgain()
    {
        DnsWarmupGate gate = new();

        Assert.True(gate.ShouldWarm("alpha.example.test"));
        Assert.False(gate.ShouldWarm("alpha.example.test"));
        Assert.False(gate.ShouldWarm("alpha.example.test"));
    }

    /// <summary>
    /// Host names are case-insensitive, so two spellings of one host are one warm-up.
    /// </summary>
    [Fact]
    public void ACaseVariantOfTheWarmedHost_IsNotWarmedAgain()
    {
        DnsWarmupGate gate = new();

        Assert.True(gate.ShouldWarm("alpha.example.test"));
        Assert.False(gate.ShouldWarm("ALPHA.Example.Test"));
    }

    /// <summary>
    /// The gate remembers one host, not a cache: moving away and back warms again rather than
    /// silently assuming an entry that may have expired.
    /// </summary>
    [Fact]
    public void ReturningToAnEarlierHost_IsWarmedAgain()
    {
        DnsWarmupGate gate = new();

        Assert.True(gate.ShouldWarm("alpha.example.test"));
        Assert.True(gate.ShouldWarm("beta.example.test"));
        Assert.True(gate.ShouldWarm("alpha.example.test"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AHostlessSession_IsNeverWarmed(string? host)
    {
        DnsWarmupGate gate = new();

        Assert.False(gate.ShouldWarm(host));
    }

    /// <summary>
    /// A session with no host must not become the remembered one, or the next real host would be
    /// compared against a blank and the gate would forget what it had warmed.
    /// </summary>
    [Fact]
    public void AHostlessSession_DoesNotDisplaceTheWarmedHost()
    {
        DnsWarmupGate gate = new();

        Assert.True(gate.ShouldWarm("alpha.example.test"));
        Assert.False(gate.ShouldWarm(null));
        Assert.False(gate.ShouldWarm("alpha.example.test"));
    }

    /// <summary>
    /// The gate and the cancellation can both be correct while the tree asks neither, which is the
    /// defect unchanged.
    /// </summary>
    [Fact]
    public void WarmDns_SourceContract_AsksTheGateBeforeResolving()
    {
        string body = ExtractMethodBody(
            ReadTreeInteractionsSource(),
            "private void WarmDns(ServerItemViewModel server)");

        int decisionIndex = body.IndexOf("TryBeginDnsWarmup(", StringComparison.Ordinal);
        int resolveIndex = body.IndexOf("WarmDnsAsync(", StringComparison.Ordinal);

        Assert.True(
            decisionIndex >= 0,
            "WarmDns must go through TryBeginDnsWarmup: without the gate every selection change "
            + "resolves the host again, including the folder clicks and right-clicks that do not "
            + "move the selection at all.");
        Assert.True(resolveIndex > decisionIndex, "The decision must be taken before the lookup.");
    }

    /// <summary>
    /// The cancellation reaches nothing unless the token the decision produced is the one the
    /// lookup is started with. A warm-up given <c>CancellationToken.None</c> runs to the end
    /// exactly as it did before, with the whole mechanism in place and green beside it.
    /// </summary>
    [Fact]
    public void WarmDns_SourceContract_StartsTheLookupWithTheTokenItWasGiven()
    {
        string body = ExtractMethodBody(
            ReadTreeInteractionsSource(),
            "private void WarmDns(ServerItemViewModel server)");

        int resolveIndex = body.IndexOf("WarmDnsAsync(", StringComparison.Ordinal);
        Assert.True(resolveIndex >= 0, "WarmDns must start a lookup.");

        int argumentsEnd = body.IndexOf(')', resolveIndex);
        Assert.True(argumentsEnd > resolveIndex, "The lookup call must be complete.");

        Assert.Contains(
            "cancellationToken",
            body[resolveIndex..argumentsEnd],
            StringComparison.Ordinal);
    }

    // ── Cancellation: each warm-up abandons the one before it ────────

    [Fact]
    public void TheFirstWarmup_IsGivenALiveToken()
    {
        DnsWarmupCancellation cancellation = new();

        CancellationToken token = cancellation.Begin();

        Assert.True(token.CanBeCanceled);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void ANewWarmup_CancelsTheOneBeforeIt()
    {
        DnsWarmupCancellation cancellation = new();

        CancellationToken abandoned = cancellation.Begin();
        CancellationToken current = cancellation.Begin();

        Assert.True(abandoned.IsCancellationRequested);
        Assert.False(current.IsCancellationRequested);
    }

    /// <summary>
    /// The displaced source is cancelled and then disposed, in that order and by the warm-up that
    /// displaced it. Disposing first would throw ObjectDisposedException out of Cancel, on the UI
    /// thread, on every arrow key.
    /// </summary>
    [Fact]
    public void TheDisplacedSource_IsDisposedOnceItIsCancelled()
    {
        DnsWarmupCancellation cancellation = new();
        CancellationToken abandoned = cancellation.Begin();

        _ = cancellation.Begin();

        Assert.True(abandoned.IsCancellationRequested);

        // WaitHandle is the member that still reports the source's lifetime: it throws once the
        // source is disposed. Scanning a long list must not leave one live source per row behind.
        Assert.Throws<ObjectDisposedException>(() => { _ = abandoned.WaitHandle; });
    }

    /// <summary>
    /// The item itself: scanning rows with the arrow keys must leave one lookup running, not one
    /// per row travelled.
    /// </summary>
    [Fact]
    public async Task AKeyboardScan_AbandonsEveryLookupButTheRowItRestsOn()
    {
        DnsWarmupGate gate = new();
        DnsWarmupCancellation cancellation = new();
        Dictionary<string, TaskCompletionSource> lookups = new(StringComparer.Ordinal);
        List<Task> warmups = [];

        Task Resolve(string host, CancellationToken cancellationToken)
        {
            TaskCompletionSource lookup = new();
            lookups[host] = lookup;

            // Dns.GetHostEntryAsync faults with OperationCanceledException when the token it was
            // given is cancelled; a stand-in that ignored the token would measure nothing.
            _ = cancellationToken.Register(() => lookup.TrySetCanceled(cancellationToken));
            return lookup.Task;
        }

        foreach (string host in new[] { "alpha.example.test", "beta.example.test", "gamma.example.test" })
        {
            Assert.True(MainWindow.TryBeginDnsWarmup(
                gate,
                cancellation,
                host,
                out CancellationToken cancellationToken));
            warmups.Add(MainWindow.WarmDnsAsync(host, Resolve, static _ => { }, cancellationToken));
        }

        // Asserted before anything is awaited: warm-ups nobody can cancel stay pending for ever,
        // and the wait below would hang instead of reporting what it found.
        Assert.True(lookups["alpha.example.test"].Task.IsCanceled);
        Assert.True(lookups["beta.example.test"].Task.IsCanceled);
        Assert.False(lookups["gamma.example.test"].Task.IsCompleted);

        lookups["gamma.example.test"].SetResult();
        await Task.WhenAll(warmups);
    }

    /// <summary>
    /// A cancelled lookup is the expected outcome of a scan, not a failure, so it says nothing.
    /// </summary>
    [Fact]
    public async Task ACancelledLookup_IsNotLogged()
    {
        List<string> log = [];
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await MainWindow.WarmDnsAsync(
            "alpha.example.test",
            static (_, cancellationToken) => Task.FromCanceled(cancellationToken),
            log.Add,
            source.Token);

        Assert.Empty(log);
    }

    /// <summary>
    /// Silencing cancellation must not silence the failure the log exists for: a host reachable
    /// only through a gateway does not resolve here, and that stays visible at Debug.
    /// </summary>
    [Fact]
    public async Task AHostThatDoesNotResolve_IsStillLogged()
    {
        List<string> log = [];

        await MainWindow.WarmDnsAsync(
            "gateway-only.example.test",
            static (_, _) => Task.FromException(
                new SocketException((int)SocketError.HostNotFound)),
            log.Add,
            CancellationToken.None);

        string entry = Assert.Single(log);
        Assert.Contains("gateway-only.example.test", entry, StringComparison.Ordinal);
        Assert.Contains(nameof(SocketException), entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate answers "is this host already warmed", the cancellation "is this lookup still
    /// wanted", and neither may answer for the other: a show that warms nothing must leave the
    /// lookup in flight alone. Folder clicks and the right-click pre-selection both re-show the
    /// selection without moving it.
    /// </summary>
    [Fact]
    public void AShowThatWarmsNothing_LeavesTheLookupInFlightAlone()
    {
        DnsWarmupGate gate = new();
        DnsWarmupCancellation cancellation = new();

        Assert.True(MainWindow.TryBeginDnsWarmup(
            gate,
            cancellation,
            "alpha.example.test",
            out CancellationToken warming));
        Assert.False(MainWindow.TryBeginDnsWarmup(
            gate,
            cancellation,
            "alpha.example.test",
            out CancellationToken skipped));

        Assert.False(warming.IsCancellationRequested);
        Assert.False(skipped.CanBeCanceled);
    }

    /// <summary>
    /// The other half of the same rule: a show that does warm cancels what was running.
    /// </summary>
    [Fact]
    public void MovingToAnotherSession_WarmsItAndAbandonsTheOneBefore()
    {
        DnsWarmupGate gate = new();
        DnsWarmupCancellation cancellation = new();

        Assert.True(MainWindow.TryBeginDnsWarmup(
            gate,
            cancellation,
            "alpha.example.test",
            out CancellationToken alpha));
        Assert.True(MainWindow.TryBeginDnsWarmup(
            gate,
            cancellation,
            "beta.example.test",
            out CancellationToken beta));

        Assert.True(alpha.IsCancellationRequested);
        Assert.False(beta.IsCancellationRequested);
    }

    private static string ReadTreeInteractionsSource()
    {
        string path = Path.Combine(FindRepoRoot(), TreeInteractionsRelativePath);
        Assert.True(File.Exists(path), $"Source file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method signature was not found: {signature}");

        int openingBraceIndex = source.IndexOf('{', signatureIndex + signature.Length);
        Assert.True(openingBraceIndex >= 0, $"Opening brace was not found for: {signature}");

        int depth = 0;
        for (int index = openingBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[(openingBraceIndex + 1)..index];
                    }

                    break;
            }
        }

        throw new InvalidDataException($"Closing brace was not found for: {signature}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
