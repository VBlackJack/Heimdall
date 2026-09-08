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

using System.Diagnostics;

namespace Heimdall.Ssh;

/// <summary>A diagnostic result contains only typed outcomes, never credentials or exception text.</summary>
public sealed record GatewayDiagnosticStep(int Hop, bool IsTarget, bool Success, SshFailureCode? Failure, long ElapsedMilliseconds)
{
    /// <summary>Progress-only notification; final results never retain this flag.</summary>
    public bool IsRunning { get; init; }
}

/// <summary>A disposable, private diagnostic route independent of live application tunnels.</summary>
public interface IGatewayDiagnosticSession : IDisposable
{
    /// <summary>Authenticates the next hop through the current last hop.</summary>
    Task ConnectHopAsync(SshConnectionParams hop, string fingerprint, CancellationToken ct);
    /// <summary>Confirms a TCP channel to the destination from the last hop, without application traffic.</summary>
    Task ProbeTargetAsync(string host, int port, CancellationToken ct);
}

/// <summary>Runs each hop in order, stops at the first failure, and releases the entire route.</summary>
public static class GatewayRouteDiagnostic
{
    /// <summary>Runs with a per-step deadline and an optional final TCP destination.</summary>
    public static async Task<IReadOnlyList<GatewayDiagnosticStep>> RunAsync(
        IReadOnlyList<SshConnectionParams> chain, Func<SshConnectionParams, string?> fingerprint,
        TimeSpan stepTimeout, string? targetHost, int targetPort,
        IProgress<GatewayDiagnosticStep>? progress, CancellationToken ct,
        Func<IGatewayDiagnosticSession>? createSession = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0) throw new ArgumentException("A route must contain a gateway.", nameof(chain));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stepTimeout, TimeSpan.Zero);
        List<GatewayDiagnosticStep> results = [];
        using IGatewayDiagnosticSession session = (createSession ?? (() => new SshGatewayDiagnosticSession()))();
        for (int index = 0; index < chain.Count; index++)
        {
            SshConnectionParams hop = chain[index];
            GatewayDiagnosticStep result = await RunStepAsync(index + 1, false, async token =>
            {
                string? pin = fingerprint(hop);
                if (string.IsNullOrWhiteSpace(pin)) return SshFailureCode.HostKeyUnavailable;
                await session.ConnectHopAsync(hop, pin, token).ConfigureAwait(false);
                return null;
            }, hop).ConfigureAwait(false);
            if (!result.Success) return results;
        }
        if (!string.IsNullOrWhiteSpace(targetHost))
        {
            await RunStepAsync(chain.Count + 1, true, async token =>
            {
                await session.ProbeTargetAsync(targetHost, targetPort, token).ConfigureAwait(false);
                return null;
            }, null).ConfigureAwait(false);
        }
        return results;

        async Task<GatewayDiagnosticStep> RunStepAsync(int hopIndex, bool isTarget,
            Func<CancellationToken, Task<SshFailureCode?>> operation, SshConnectionParams? parameters)
        {
            Stopwatch watch = Stopwatch.StartNew();
            progress?.Report(new GatewayDiagnosticStep(hopIndex, isTarget, false, null, 0) { IsRunning = true });
            SshFailureCode? failure;
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(stepTimeout);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                failure = await operation(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                failure = ct.IsCancellationRequested ? SshFailureCode.Cancelled : SshFailureCode.NetworkTimedOut;
            }
            catch (Exception ex)
            {
                failure = ct.IsCancellationRequested ? SshFailureCode.Cancelled
                    : deadline.IsCancellationRequested ? SshFailureCode.NetworkTimedOut
                    : FailureClassifier.Classify(ex, parameters).Code;
            }
            GatewayDiagnosticStep step = new(hopIndex, isTarget, failure is null, failure, watch.ElapsedMilliseconds);
            results.Add(step);
            progress?.Report(step);
            return step;
        }
    }
}
