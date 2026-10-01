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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class GatewayOverviewDialogViewModelTests
{
    [Fact]
    public async Task ReassignCommand_InvokesMutationAndReloadsOverview()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        SshGatewayDto targetGateway = CreateGateway("gw-target", "Bastion");
        GatewayOverview initialOverview = GatewayOverviewBuilder.Build(
            [targetGateway],
            [CreateServer("alpha", "Alpha", "gw-missing")]);
        GatewayOverview refreshedOverview = GatewayOverviewBuilder.Build(
            [targetGateway],
            [CreateServer("alpha", "Alpha", "gw-target")]);
        GatewayOverviewMutationRequest? capturedRequest = null;
        var viewModel = new GatewayOverviewDialogViewModel(
            initialOverview,
            localizer,
            [new GatewayOption("gw-target", "Bastion (bastion.example.test:22)")],
            (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(1);
            },
            _ => Task.FromResult(refreshedOverview));

        GatewayOverviewMissingReferenceItemViewModel missing = Assert.Single(viewModel.MissingReferences);
        Assert.True(missing.ReassignCommand.CanExecute(null));

        await missing.ReassignCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(["alpha"], capturedRequest!.ServerIds);
        Assert.Equal("gw-target", capturedRequest.TargetGatewayId);
        Assert.Empty(viewModel.MissingReferences);
        GatewayOverviewGatewayItemViewModel gateway = Assert.Single(viewModel.Gateways);
        GatewayOverviewSessionItemViewModel session = Assert.Single(gateway.Sessions);
        Assert.Equal("alpha", session.Id);
        Assert.Equal("Reassigned 1 session.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ClearCommand_SendsNullTargetAndReloadsOverview()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        GatewayOverview initialOverview = GatewayOverviewBuilder.Build(
            [],
            [CreateServer("alpha", "Alpha", "gw-missing")]);
        GatewayOverview refreshedOverview = GatewayOverviewBuilder.Build(
            [],
            [CreateServer("alpha", "Alpha", gatewayId: null)]);
        GatewayOverviewMutationRequest? capturedRequest = null;
        var viewModel = new GatewayOverviewDialogViewModel(
            initialOverview,
            localizer,
            [],
            (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(1);
            },
            _ => Task.FromResult(refreshedOverview));

        GatewayOverviewMissingReferenceItemViewModel missing = Assert.Single(viewModel.MissingReferences);
        Assert.True(missing.ClearCommand.CanExecute(null));

        await missing.ClearCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Equal(["alpha"], capturedRequest!.ServerIds);
        Assert.Null(capturedRequest.TargetGatewayId);
        Assert.Empty(viewModel.MissingReferences);
        Assert.Equal("Cleared gateway reference on 1 session.", viewModel.StatusMessage);
    }

    /// <summary>
    /// The summary line words each count by its number, in the language's rule: one gateway, one
    /// routed session and no unresolved reference, which French words in the singular too.
    /// </summary>
    [Theory]
    [InlineData("en", "1 gateway", "1 routed session", "0 unresolved references")]
    [InlineData("fr", "1 passerelle", "1 session rout\u00e9e", "0 r\u00e9f\u00e9rence introuvable")]
    [InlineData("es", "1 pasarela", "1 sesi\u00f3n enrutada", "0 referencias sin resolver")]
    public async Task Summary_WordsEachCountByItsNumber(
        string locale,
        string expectedGateways,
        string expectedRoutedSessions,
        string expectedMissingReferences)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);
        GatewayOverview overview = GatewayOverviewBuilder.Build(
            [CreateGateway("gw-target", "Bastion")],
            [CreateServer("alpha", "Alpha", "gw-target")]);

        var viewModel = new GatewayOverviewDialogViewModel(
            overview,
            localizer,
            [],
            (_, _) => Task.FromResult(0),
            _ => Task.FromResult(overview));

        Assert.Equal(expectedGateways, viewModel.GatewaySummary);
        Assert.Equal(expectedRoutedSessions, viewModel.RoutedSessionSummary);
        Assert.Equal(expectedMissingReferences, viewModel.MissingReferenceSummary);
        Assert.Equal(
            localizer.FormatCount(1, "GatewayOverviewSessionCountOne", "GatewayOverviewSessionCount", 1),
            Assert.Single(viewModel.Gateways).SessionCountText);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale = "en")
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }

    private static SshGatewayDto CreateGateway(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Host = "bastion.example.test",
            Port = 22,
            User = "ops"
        };

    private static ServerProfileDto CreateServer(string id, string displayName, string? gatewayId) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            RemoteServer = $"{id}.example.test",
            ConnectionType = "SSH",
            SshGatewayId = gatewayId
        };
}
