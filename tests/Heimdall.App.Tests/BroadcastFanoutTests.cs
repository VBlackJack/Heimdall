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

using System.Text;
using Heimdall.App.Services;
using Heimdall.Terminal;

namespace Heimdall.App.Tests;

/// <summary>
/// Producer: BroadcastFanout.Dispatch / Evaluate
/// (src/Heimdall.App/Services/BroadcastFanout.cs). Verifies the broadcast
/// fan-out is guarded by SmartPasteGuard before any subscriber write, mirroring
/// the terminal paste path (EmbeddedSshView clipboard-read handler, ~line 1110).
/// </summary>
public class BroadcastFanoutTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Evaluate_DecodesUtf8AndDelegatesToSmartPasteGuard()
    {
        Assert.Equal(SmartPasteGuard.PasteRisk.Safe, BroadcastFanout.Evaluate(Bytes("ls -la")));
        Assert.Equal(SmartPasteGuard.PasteRisk.MultiLine, BroadcastFanout.Evaluate(Bytes("a\nb")));
        Assert.Equal(SmartPasteGuard.PasteRisk.Dangerous, BroadcastFanout.Evaluate(Bytes("rm -rf /")));
    }

    [Fact]
    public void Dispatch_SafePayload_WritesAllSubscribers_WithoutConfirming()
    {
        var writes = new List<string>();
        bool confirmConsulted = false;

        var writers = new List<Action<byte[]>>
        {
            d => writes.Add("a:" + Encoding.UTF8.GetString(d)),
            d => writes.Add("b:" + Encoding.UTF8.GetString(d)),
        };

        int written = BroadcastFanout.Dispatch(
            Bytes("whoami"),
            isProduction: false,
            writers,
            _ => { confirmConsulted = true; return true; });

        Assert.Equal(2, written);
        Assert.Equal(new[] { "a:whoami", "b:whoami" }, writes);
        Assert.False(confirmConsulted); // Safe payloads must never trigger the guard prompt.
    }

    [Fact]
    public void Dispatch_DangerousPayload_DeclinedConfirmation_WritesNothing()
    {
        var writes = new List<string>();
        SmartPasteGuard.PasteRisk seenRisk = SmartPasteGuard.PasteRisk.Safe;

        var writers = new List<Action<byte[]>>
        {
            d => writes.Add(Encoding.UTF8.GetString(d)),
        };

        int written = BroadcastFanout.Dispatch(
            Bytes("rm -rf /var"),
            isProduction: false,
            writers,
            risk => { seenRisk = risk; return false; });

        // Guarded write, not raw write: the dangerous payload is gated behind the
        // confirmation, and a declined prompt suppresses the entire fan-out.
        Assert.Equal(0, written);
        Assert.Empty(writes);
        Assert.Equal(SmartPasteGuard.PasteRisk.Dangerous, seenRisk);
    }

    [Fact]
    public void Dispatch_MultiLinePayload_ApprovedConfirmation_WritesAllSubscribers()
    {
        var writes = new List<string>();

        var writers = new List<Action<byte[]>>
        {
            d => writes.Add("a"),
            d => writes.Add("b"),
        };

        int written = BroadcastFanout.Dispatch(
            Bytes("echo one\necho two"),
            isProduction: false,
            writers,
            risk => risk == SmartPasteGuard.PasteRisk.MultiLine); // approve only multi-line

        Assert.Equal(2, written);
        Assert.Equal(new[] { "a", "b" }, writes);
    }

    [Fact]
    public void Dispatch_ProductionMultiLine_IsTreatedAsDangerous()
    {
        SmartPasteGuard.PasteRisk seenRisk = SmartPasteGuard.PasteRisk.Safe;

        int written = BroadcastFanout.Dispatch(
            Bytes("line1\nline2"),
            isProduction: true,
            new List<Action<byte[]>> { _ => { } },
            risk => { seenRisk = risk; return false; });

        Assert.Equal(0, written);
        Assert.Equal(SmartPasteGuard.PasteRisk.Dangerous, seenRisk);
    }

    [Fact]
    public void Dispatch_SkipsDisposedSubscriber_AndKeepsWritingOthers()
    {
        var writes = new List<string>();

        var writers = new List<Action<byte[]>>
        {
            _ => throw new ObjectDisposedException("closed-session"),
            _ => writes.Add("alive"),
        };

        int written = BroadcastFanout.Dispatch(
            Bytes("date"),
            isProduction: false,
            writers,
            _ => true);

        Assert.Equal(1, written);
        Assert.Equal(new[] { "alive" }, writes);
    }
}
