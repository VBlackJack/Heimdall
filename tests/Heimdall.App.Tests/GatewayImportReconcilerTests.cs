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

using Heimdall.App.Services.Import;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class GatewayImportReconcilerTests
{
    [Fact]
    public void Reconcile_PreservesImportedGatewayId_WhenIdentityIsNewAndIdUnused()
    {
        SshGatewayDto imported = Gateway("imported-gateway", "bastion.example.com", "ops");
        ServerProfileDto server = Server("server-1", "imported-gateway");

        GatewayImportReconciliationResult result = GatewayImportReconciler.Reconcile(
            [],
            [imported],
            [server],
            NewIdFactory("unused"));

        SshGatewayDto gateway = Assert.Single(result.GatewaysToAdd);
        Assert.Equal("imported-gateway", gateway.Id);
        Assert.Equal("imported-gateway", result.GatewayIdMap["imported-gateway"]);
        Assert.Empty(result.OrphanReferences);
        Assert.Equal(0, result.MergedCount);
    }

    [Fact]
    public void Reconcile_ReusesExistingGatewayByIdentity_AndRemapsImportedId()
    {
        SshGatewayDto existing = Gateway("existing-gateway", "BASTION.example.com", "OPS");
        SshGatewayDto imported = Gateway("imported-gateway", "bastion.example.com", "ops");
        imported.KeyPath = @"C:\new\id_ed25519";
        ServerProfileDto server = Server("server-1", "imported-gateway");

        GatewayImportReconciliationResult result = GatewayImportReconciler.Reconcile(
            [existing],
            [imported],
            [server],
            NewIdFactory("unused"));

        Assert.Empty(result.GatewaysToAdd);
        Assert.Equal("existing-gateway", result.GatewayIdMap["imported-gateway"]);
        Assert.Empty(result.OrphanReferences);
        Assert.Equal(1, result.MergedCount);
    }

    [Fact]
    public void Reconcile_GeneratesNewId_WhenImportedIdCollidesWithDifferentExistingGateway()
    {
        SshGatewayDto existing = Gateway("same-id", "other.example.com", "ops");
        SshGatewayDto imported = Gateway("same-id", "bastion.example.com", "ops");

        GatewayImportReconciliationResult result = GatewayImportReconciler.Reconcile(
            [existing],
            [imported],
            [],
            NewIdFactory("generated-gateway"));

        SshGatewayDto gateway = Assert.Single(result.GatewaysToAdd);
        Assert.Equal("generated-gateway", gateway.Id);
        Assert.Equal("generated-gateway", result.GatewayIdMap["same-id"]);
    }

    [Fact]
    public void Reconcile_RemapsParentGatewayReference()
    {
        SshGatewayDto existingParent = Gateway("existing-parent", "parent.example.com", "ops");
        SshGatewayDto importedParent = Gateway("imported-parent", "parent.example.com", "ops");
        SshGatewayDto importedChild = Gateway("imported-child", "child.example.com", "ops");
        importedChild.ParentGatewayId = "imported-parent";

        GatewayImportReconciliationResult result = GatewayImportReconciler.Reconcile(
            [existingParent],
            [importedParent, importedChild],
            [],
            NewIdFactory("unused"));

        SshGatewayDto child = Assert.Single(result.GatewaysToAdd);
        Assert.Equal("imported-child", child.Id);
        Assert.Equal("existing-parent", child.ParentGatewayId);
        Assert.Empty(result.OrphanReferences);
    }

    [Fact]
    public void Reconcile_ReportsServerGatewayRefsWithoutExistingOrImportedGateway()
    {
        ServerProfileDto server = Server("server-1", "missing-gateway");
        server.DisplayName = "Missing Gateway Server";

        GatewayImportReconciliationResult result = GatewayImportReconciler.Reconcile(
            [],
            [],
            [server],
            NewIdFactory("unused"));

        GatewayImportOrphanReference orphan = Assert.Single(result.OrphanReferences);
        Assert.Equal(GatewayImportReferenceKind.Server, orphan.Kind);
        Assert.Equal("server-1", orphan.OwnerId);
        Assert.Equal("Missing Gateway Server", orphan.OwnerName);
        Assert.Equal("missing-gateway", orphan.GatewayId);
    }

    [Fact]
    public void Reconcile_StripsSecretsFromCreatedGateways()
    {
        SshGatewayDto imported = Gateway("imported-gateway", "bastion.example.com", "ops");
        imported.SshPasswordEncrypted = "secret";
        imported.SshKeyPassphraseEncrypted = "key-secret";

        GatewayImportReconciliationResult result = GatewayImportReconciler.Reconcile(
            [],
            [imported],
            [],
            NewIdFactory("unused"));

        SshGatewayDto gateway = Assert.Single(result.GatewaysToAdd);
        Assert.Null(gateway.SshPasswordEncrypted);
        Assert.Null(gateway.SshKeyPassphraseEncrypted);
    }

    private static SshGatewayDto Gateway(string id, string host, string user) => new()
    {
        Id = id,
        Name = id,
        Host = host,
        Port = 22,
        User = user
    };

    /// <summary>
    /// An imported gateway is added without the secrets it arrived with.
    /// </summary>
    /// <remarks>
    /// The reconciler exists to decide which gateways to add, not to carry credentials into the
    /// result. Clearing them must also not fabricate the declaration of the passphrase field: that
    /// flag is what the legacy credential mapping is derived from, and the mapping decides on the
    /// connect paths whether the stored password is offered as the key passphrase.
    /// </remarks>
    [Fact]
    public void AnImportedGatewayIsAddedWithoutItsSecretsAndWithoutFabricatedPresence()
    {
        SshGatewayDto imported = Gateway("gw-imported", "bastion.example.com", "operator");
        imported.KeyPath = @"C:\keys\bastion.ppk";
        imported.SshPasswordEncrypted = "cipher";

        Assert.True(imported.UsesLegacySshCredentialMapping);

        GatewayImportReconciliationResult result = GatewayImportReconciler.Reconcile(
            [],
            [imported],
            [],
            NewIdFactory("gw-imported"));

        SshGatewayDto added = Assert.Single(result.GatewaysToAdd);

        Assert.Null(added.SshPasswordEncrypted);
        Assert.Null(added.SshKeyPassphraseEncrypted);
        Assert.False(added.HasSshKeyPassphraseEncryptedField);

        // The gateway itself is still identifiable; only the secrets are gone.
        Assert.Equal(imported.Host, added.Host);
        Assert.Equal(imported.User, added.User);
        Assert.Equal(imported.KeyPath, added.KeyPath);
    }

    private static ServerProfileDto Server(string id, string gatewayId) => new()
    {
        Id = id,
        DisplayName = id,
        RemoteServer = "server.example.com",
        SshGatewayId = gatewayId
    };

    private static Func<string> NewIdFactory(params string[] ids)
    {
        var index = 0;
        return () => ids[index++];
    }
}
