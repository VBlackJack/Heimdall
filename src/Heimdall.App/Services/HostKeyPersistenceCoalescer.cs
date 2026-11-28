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

using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Writes trusted host key changes to the settings in batches. Every trust store
/// mutation raises one event, and a known_hosts sync raises one per line: persisted
/// one by one, each cost a full settings load, serialize, atomic write and a
/// settings-changed broadcast, thousands of times at start-up. Changes are now
/// gathered for a short quiet window and merged in one write, last change per key
/// winning.
/// </summary>
public sealed class HostKeyPersistenceCoalescer : IDisposable, IAsyncDisposable
{
    /// <summary>Quiet window after the last change before the batch is written.</summary>
    public static readonly TimeSpan DefaultQuietWindow = TimeSpan.FromMilliseconds(250);

    private readonly IConfigManager _configManager;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _quietWindow;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);
    private PendingPolicySnapshot? _pendingPolicies;
    private ITimer? _timer;
    private Task _flush = Task.CompletedTask;
    private bool _disposed;

    public HostKeyPersistenceCoalescer(
        IConfigManager configManager,
        TimeProvider? timeProvider = null,
        TimeSpan? quietWindow = null)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _quietWindow = quietWindow ?? DefaultQuietWindow;
    }

    /// <summary>The write started by the last flush, for callers that need to await it.</summary>
    public Task LastFlush
    {
        get
        {
            lock (_gate)
            {
                return _flush;
            }
        }
    }

    /// <summary>Records a trusted entry to write under <paramref name="key"/>.</summary>
    public void Upsert(string key, HostKeyEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(entry);
        Enqueue(key, new PendingChange(entry, entry.Fingerprint, Remove: false));
    }

    /// <summary>Records a legacy fingerprint-only trust, added only when the key is absent.</summary>
    public void UpsertFingerprint(string key, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        Enqueue(key, new PendingChange(Entry: null, fingerprint, Remove: false));
    }

    /// <summary>Records the removal of <paramref name="key"/>.</summary>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Enqueue(key, new PendingChange(Entry: null, Fingerprint: null, Remove: true));
    }

    /// <summary>Records an updated snapshot of <c>@cert-authority</c> and <c>@revoked</c> rules to persist.</summary>
    public void UpdatePolicies(
        IReadOnlyList<(string HostPatterns, string Fingerprint)> certAuthorities,
        IReadOnlyList<(string HostPatterns, string Fingerprint)> revokedKeys)
    {
        ArgumentNullException.ThrowIfNull(certAuthorities);
        ArgumentNullException.ThrowIfNull(revokedKeys);

        var caList = certAuthorities
            .Select(static r => new KnownHostsPolicyRuleDto(r.HostPatterns, r.Fingerprint))
            .ToList();
        var revokedList = revokedKeys
            .Select(static r => new KnownHostsPolicyRuleDto(r.HostPatterns, r.Fingerprint))
            .ToList();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingPolicies = new PendingPolicySnapshot(caList, revokedList);
            ArmTimerLocked();
        }
    }

    /// <summary>Writes whatever is pending now instead of waiting for the quiet window.</summary>
    public Task FlushAsync()
    {
        var (batch, policies) = TakeBatch();
        return batch is null && policies is null ? LastFlush : WriteAsync(batch, policies);
    }

    /// <summary>
    /// Stops the quiet-window timer and starts the final write. The write is not
    /// awaited here: prefer <see cref="DisposeAsync"/>, which the container uses at
    /// exit, so a change made in the last quiet window is on disk before the process
    /// ends.
    /// </summary>
    public void Dispose()
    {
        _ = StopAndFlush();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAndFlush().ConfigureAwait(false);
    }

    private Task StopAndFlush()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return LastFlush;
            }

            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        return FlushAsync();
    }

    private void Enqueue(string key, PendingChange change)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending[key] = change;
            ArmTimerLocked();
        }
    }

    private void ArmTimerLocked()
    {
        if (_timer is null)
        {
            _timer = _timeProvider.CreateTimer(_ => OnQuietWindowElapsed(), null, _quietWindow, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer.Change(_quietWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnQuietWindowElapsed()
    {
        var (batch, policies) = TakeBatch();
        if (batch is not null || policies is not null)
        {
            _ = WriteAsync(batch, policies);
        }
    }

    private (Dictionary<string, PendingChange>? Batch, PendingPolicySnapshot? Policies) TakeBatch()
    {
        lock (_gate)
        {
            Dictionary<string, PendingChange>? batch = null;
            if (_pending.Count > 0)
            {
                batch = new Dictionary<string, PendingChange>(_pending, StringComparer.Ordinal);
                _pending.Clear();
            }

            PendingPolicySnapshot? policies = _pendingPolicies;
            _pendingPolicies = null;

            return (batch, policies);
        }
    }

    private Task WriteAsync(Dictionary<string, PendingChange>? batch, PendingPolicySnapshot? policies)
    {
        Task previous;
        Task write;
        lock (_gate)
        {
            previous = _flush;
            write = WriteAfterAsync(previous, batch, policies);
            _flush = write;
        }

        return write;
    }

    private async Task WriteAfterAsync(
        Task previous,
        Dictionary<string, PendingChange>? batch,
        PendingPolicySnapshot? policies)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The previous write already logged its own failure.
        }

        int changeCount = (batch?.Count ?? 0) + (policies is null ? 0 : 1);
        try
        {
            await _configManager.MergeSettingAsync(settings =>
            {
                if (batch is not null)
                {
                    foreach (KeyValuePair<string, PendingChange> item in batch)
                    {
                        Apply(settings, item.Key, item.Value);
                    }
                }

                if (policies is not null)
                {
                    settings.TrustedCertAuthorities = policies.CertAuthorities;
                    settings.RevokedHostKeys = policies.RevokedKeys;
                }
            }).ConfigureAwait(false);
            FileLogger.Info($"Persisted {changeCount} trusted host key change(s) in one write.");
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Failed to persist {changeCount} trusted host key change(s): {ex.Message}");
        }
    }

    private static void Apply(AppSettings settings, string key, PendingChange change)
    {
        if (change.Remove)
        {
            settings.TrustedHostKeys.Remove(key);
            settings.TrustedHostKeysV2.Remove(key);
            return;
        }

        if (change.Entry is not null)
        {
            settings.TrustedHostKeysV2[key] = change.Entry;
            settings.TrustedHostKeys[key] = change.Entry.Fingerprint;
            return;
        }

        // Legacy fingerprint-only trust never overwrites an existing entry.
        if (change.Fingerprint is not null && !settings.TrustedHostKeys.ContainsKey(key))
        {
            settings.TrustedHostKeys[key] = change.Fingerprint;
        }
    }

    private sealed record PendingChange(HostKeyEntry? Entry, string? Fingerprint, bool Remove);

    private sealed record PendingPolicySnapshot(
        List<KnownHostsPolicyRuleDto> CertAuthorities,
        List<KnownHostsPolicyRuleDto> RevokedKeys);
}
