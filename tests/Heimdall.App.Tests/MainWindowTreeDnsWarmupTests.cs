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

namespace Heimdall.App.Tests;

/// <summary>
/// The DNS pre-warm the sessions tree fires when it shows a selection.
/// </summary>
/// <remarks>
/// The warm-up hangs off ShowTreeSelection, which also runs on a folder click and on the
/// right-click pre-selection. Neither moves the selection, so the same host was resolved again
/// with the answer already in the resolver cache.
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
    /// The gate can be correct while the tree never asks it, which is the defect unchanged.
    /// </summary>
    [Fact]
    public void WarmDns_SourceContract_AsksTheGateBeforeResolving()
    {
        string body = ExtractMethodBody(
            ReadTreeInteractionsSource(),
            "private void WarmDns(ServerItemViewModel server)");

        int gateIndex = body.IndexOf("_dnsWarmupGate.ShouldWarm(", StringComparison.Ordinal);
        int resolveIndex = body.IndexOf("WarmDnsAsync(", StringComparison.Ordinal);

        Assert.True(
            gateIndex >= 0,
            "WarmDns must ask the gate: without it every selection change resolves the host "
            + "again, including the folder clicks and right-clicks that do not move the "
            + "selection at all.");
        Assert.True(resolveIndex > gateIndex, "The gate must be consulted before the lookup.");
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
