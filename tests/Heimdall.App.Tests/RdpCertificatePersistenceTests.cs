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
using Heimdall.App.Services;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class RdpCertificatePersistenceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FailedRevocationRetainsApprovalAndCanBeRetried()
    {
        RdpCertificateTrustStore store = new();
        RdpTrustKey key = RdpTrustKey.ForProfile("profile");
        store.Trust(key, "SHA256:AA");
        bool fail = true;
        using RdpCertificatePersistence persistence = new(store, (_, _) =>
            fail ? Task.FromException(new IOException("Save failed")) : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => persistence.RevokeAsync(key, "SHA256:AA"));
        Assert.Single(store.GetApproved(key));
        fail = false;
        await persistence.RevokeAsync(key, "SHA256:AA").WaitAsync(Budget);
        await persistence.PendingWrites.WaitAsync(Budget);
        Assert.Empty(store.GetApproved(key));
    }

    [Fact]
    public async Task RevocationWaitsForDiskAndCannotBeOverwrittenByAnEarlierQueuedSnapshot()
    {
        RdpCertificateTrustStore store = new();
        RdpTrustKey key = RdpTrustKey.ForProfile("profile");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyCollection<RdpCertificateEntry> disk = [];
        int saves = 0;
        using RdpCertificatePersistence persistence = new(store, async (_, entries) =>
        {
            if (Interlocked.Increment(ref saves) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(Budget);
            }
            disk = entries;
        });
        store.Trust(key, "SHA256:AA");
        await entered.Task.WaitAsync(Budget);
        store.Trust(key, "SHA256:BB");
        Task revoke = persistence.RevokeAsync(key, "SHA256:AA");
        Assert.False(revoke.IsCompleted);
        Assert.Equal(2, store.GetApproved(key).Count);
        release.SetResult();
        await revoke.WaitAsync(Budget);
        await persistence.PendingWrites.WaitAsync(Budget);
        Assert.Equal("SHA256:BB", Assert.Single(disk).Thumbprint);
        Assert.Equal("SHA256:BB", Assert.Single(store.GetApproved(key)).Thumbprint);
    }

    [Fact]
    public async Task ProductionWriterRevocationSurvivesReload()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(RdpCertificatePersistenceTests), Guid.NewGuid().ToString("N"));
        try
        {
            ConfigManager config = new(root);
            await config.InitializeAsync();
            RdpTrustKey key = RdpTrustKey.ForTypedDestination("audit.invalid");
            RdpCertificateTrustStore store = new();
            store.Trust(key, "SHA256:AA");
            await RdpCertificatePersistence.PersistAsync(config, key, store.GetApproved(key));
            Assert.Single((await config.LoadSettingsAsync()).TrustedRdpCertificatesForTypedDestinations);
            using RdpCertificatePersistence persistence = new(store, config);
            await persistence.RevokeAsync(key, "SHA256:AA");
            await persistence.PendingWrites.WaitAsync(Budget);
            ConfigManager reload = new(root);
            Assert.Empty((await reload.LoadSettingsAsync()).TrustedRdpCertificatesForTypedDestinations);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
