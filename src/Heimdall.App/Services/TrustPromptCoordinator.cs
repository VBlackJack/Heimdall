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

namespace Heimdall.App.Services;

internal enum TrustPromptKind
{
    SshHostKey,
    FtpsCertificate
}

internal readonly record struct TrustPromptKey(
    TrustPromptKind Kind,
    string Host,
    int Port,
    string PresentedFingerprint)
{
    public static TrustPromptKey Create(
        TrustPromptKind kind,
        string host,
        int port,
        string presentedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentedFingerprint);
        return new TrustPromptKey(kind, host, port, presentedFingerprint);
    }
}

/// <summary>
/// Serializes trust prompts and coalesces concurrent requests for the same
/// trust type, logical target, and presented fingerprint.
/// </summary>
internal sealed class TrustPromptCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<TrustPromptKey, PendingPrompt> _pending = [];
    private readonly SemaphoreSlim _promptSlot = new(initialCount: 1, maxCount: 1);

    public Task<TDecision> RequestAsync<TDecision>(
        TrustPromptKey key,
        Func<CancellationToken, Task<TDecision>> displayAsync,
        TDecision cancellationDecision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(displayAsync);

        PendingPrompt<TDecision> pending;
        bool startProcessing;
        lock (_sync)
        {
            if (_pending.TryGetValue(key, out PendingPrompt? existing))
            {
                pending = existing as PendingPrompt<TDecision>
                    ?? throw new InvalidOperationException(
                        "A trust prompt key cannot be shared across decision types.");
                startProcessing = false;
            }
            else
            {
                pending = new PendingPrompt<TDecision>(displayAsync);
                _pending.Add(key, pending);
                startProcessing = true;
            }

            pending.AddWaiter();
        }

        if (startProcessing)
        {
            _ = ProcessAsync(key, pending);
        }

        return pending.WaitAsync(cancellationDecision, ct);
    }

    private async Task ProcessAsync<TDecision>(
        TrustPromptKey key,
        PendingPrompt<TDecision> pending)
    {
        bool slotAcquired = false;
        try
        {
            await _promptSlot.WaitAsync(pending.LifetimeToken).ConfigureAwait(false);
            slotAcquired = true;

            TDecision decision = await pending
                .DisplayAsync(pending.LifetimeToken)
                .ConfigureAwait(false);
            pending.TrySetResult(decision);
        }
        catch (OperationCanceledException) when (pending.LifetimeToken.IsCancellationRequested)
        {
            pending.TrySetCanceled();
        }
        catch (Exception ex)
        {
            pending.TrySetException(ex);
        }
        finally
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(key, out PendingPrompt? current)
                    && ReferenceEquals(current, pending))
                {
                    _pending.Remove(key);
                }
            }

            if (slotAcquired)
            {
                _promptSlot.Release();
            }

            pending.Dispose();
        }
    }

    private abstract class PendingPrompt : IDisposable
    {
        public abstract void Dispose();
    }

    private sealed class PendingPrompt<TDecision>(
        Func<CancellationToken, Task<TDecision>> displayAsync) : PendingPrompt
    {
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly TaskCompletionSource<TDecision> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiterCount;

        public CancellationToken LifetimeToken => _lifetimeCts.Token;

        public Func<CancellationToken, Task<TDecision>> DisplayAsync { get; } = displayAsync;

        public void AddWaiter()
        {
            _ = Interlocked.Increment(ref _waiterCount);
        }

        public async Task<TDecision> WaitAsync(
            TDecision cancellationDecision,
            CancellationToken ct)
        {
            try
            {
                return await _completion.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return cancellationDecision;
            }
            finally
            {
                if (Interlocked.Decrement(ref _waiterCount) == 0
                    && !_completion.Task.IsCompleted)
                {
                    _lifetimeCts.Cancel();
                }
            }
        }

        public void TrySetResult(TDecision decision)
        {
            // Coalesced TrustOnce decisions intentionally apply to every concurrent
            // request for the same target and fingerprint: they share one session trust.
            _ = _completion.TrySetResult(decision);
        }

        public void TrySetCanceled()
        {
            _ = _completion.TrySetCanceled(_lifetimeCts.Token);
        }

        public void TrySetException(Exception exception)
        {
            _ = _completion.TrySetException(exception);
        }

        public override void Dispose()
        {
            _lifetimeCts.Dispose();
        }
    }
}
