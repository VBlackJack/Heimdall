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
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins audit 2026-09-30 S-07: the gateway diagnostic and the tool gateway connector build
/// every user-visible sentence from locale templates, not from text in code.
/// </summary>
/// <remarks>
/// Each template in the fixture is distinct from anything the code could produce on its own,
/// so a string still assembled in code cannot match the expected value.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class GatewayDiagnosticTextLocalizationTests : IDisposable
{
    private readonly CredentialProtectorStateScope _protectorState = new();
    private readonly string _localesPath;

    public GatewayDiagnosticTextLocalizationTests()
    {
        _localesPath = Path.Combine(Path.GetTempPath(), $"heimdall-locales-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_localesPath);
        File.WriteAllText(
            Path.Combine(_localesPath, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["GatewayDiagnosticWorkstation"] = "WS",
                ["GatewayDiagnosticRouteHop"] = "HOP<{0}|{1}|{2}>",
                ["GatewayDiagnosticRouteEndpoint"] = "EP<{0}|{1}>",
                ["GatewayDiagnosticRouteSeparator"] = " >> ",
                ["GatewayDiagnosticHopStep"] = "STEP#{0}",
                ["GatewayDiagnosticSuccess"] = "OK",
                ["GatewayDiagnosticStepLine"] = "LINE<{0}|{1}|{2}>",
                ["ErrorGatewayRouteEmpty"] = "FIXTURE empty route",
            }));
    }

    public void Dispose()
    {
        _protectorState.Dispose();
        try
        {
            Directory.Delete(_localesPath, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task TheRoutePreview_IsBuiltFromLocaleTemplates()
    {
        GatewayDialogViewModel vm = await CreateAsync();
        vm.ConfigureDiagnostics([], (_, _, _, _, _) => Task.FromResult<IReadOnlyList<GatewayDiagnosticStep>>([]));
        vm.DiagnosticTargetHost = "dest.invalid";
        vm.DiagnosticTargetPort = "443";

        Assert.Equal("WS >> HOP<draft|draft.invalid|22> >> EP<dest.invalid|443>", vm.FullDiagnosticRoute);
    }

    [Fact]
    public async Task AStepLine_IsBuiltFromLocaleTemplates()
    {
        GatewayDialogViewModel vm = await CreateAsync();
        vm.ConfigureDiagnostics([], (_, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<GatewayDiagnosticStep>>([new(1, false, true, null, 12)]));

        await vm.TestGatewayRouteCommand.ExecuteAsync(null);

        Assert.Equal("LINE<STEP#1|OK|12>", vm.DiagnosticStatus);
    }

    [Fact]
    public async Task AnEmptyToolRoute_IsRefusedWithALocalisedMessage()
    {
        LocalizationManager localizer = await LoadAsync();

        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(() =>
            ToolGatewayConnector.ConnectCoreAsync([], new HostKeyTrustService(new HostKeyStore()), localizer, 0, default));

        Assert.StartsWith("FIXTURE empty route", refusal.Message, StringComparison.Ordinal);
    }

    private async Task<GatewayDialogViewModel> CreateAsync()
    {
        return new GatewayDialogViewModel
        {
            Name = "draft",
            Host = "draft.invalid",
            User = "audit",
            Localizer = await LoadAsync(),
        };
    }

    private async Task<LocalizationManager> LoadAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(_localesPath, "en");
        return localizer;
    }
}
