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

using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>Serializes application trust writes and acknowledges durable revocations.</summary>
public sealed class RdpCertificatePersistence : IDisposable
{
    private readonly RdpCertificateTrustStore _store;
    private readonly Func<RdpTrustKey, IReadOnlyCollection<RdpCertificateEntry>, Task> _persist;
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Connects the application trust store to its settings writer.</summary>
    public RdpCertificatePersistence(RdpCertificateTrustStore store, IConfigManager configManager)
        : this(store, (key, entries) => PersistAsync(configManager, key, entries))
    {
    }

    internal RdpCertificatePersistence(
        RdpCertificateTrustStore store,
        Func<RdpTrustKey, IReadOnlyCollection<RdpCertificateEntry>, Task> persist)
    {
        _store = store;
        _persist = persist;
        _store.TrustChanged += OnTrustChanged;
    }

    /// <summary>Persists removal before withdrawing the row and reporting success.</summary>
    /// <remarks>A failed save leaves the approval visible so the user can retry.</remarks>
    public Task RevokeAsync(RdpTrustKey key, string thumbprint)
        => Enqueue(async () =>
        {
            string normalized = RdpCertificateTrust.Normalize(thumbprint);
            RdpCertificateEntry[] remaining = _store.GetApproved(key)
                .Where(entry => !string.Equals(entry.Thumbprint, normalized, StringComparison.Ordinal))
                .ToArray();
            await _persist(key, remaining).ConfigureAwait(false);
            _store.Remove(key, normalized);
        });

    /// <summary>Stops receiving changes when the application services are disposed.</summary>
    public void Dispose() => _store.TrustChanged -= OnTrustChanged;

    internal Task PendingWrites
    {
        get
        {
            lock (_gate) return _tail;
        }
    }

    internal static Task PersistAsync(
        IConfigManager configManager,
        RdpTrustKey key,
        IReadOnlyCollection<RdpCertificateEntry> entries)
        => configManager.MergeSettingAsync(settings =>
        {
            Dictionary<string, List<RdpCertificateEntry>> owners = key.Scope switch
            {
                RdpTrustScope.TypedDestination => settings.TrustedRdpCertificatesForTypedDestinations,
                _ => settings.TrustedRdpCertificates,
            };
            if (entries.Count == 0)
            {
                owners.Remove(key.Identity);
            }
            else
            {
                owners[key.Identity] = [.. entries];
            }
        });

    private void OnTrustChanged(RdpTrustKey key, IReadOnlyCollection<RdpCertificateEntry> entries)
        => _ = ObserveAsync(Enqueue(() => _persist(key, _store.GetApproved(key))));

    private Task Enqueue(Func<Task> operation)
    {
        lock (_gate)
        {
            Task previous = _tail;
            _tail = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Each caller observes its own failure; a failed save must allow retry.
                }

                await operation().ConfigureAwait(false);
            });
            return _tail;
        }
    }

    private static async Task ObserveAsync(Task pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Failed to persist RDP certificate trust: {ex.Message}");
        }
    }
}
