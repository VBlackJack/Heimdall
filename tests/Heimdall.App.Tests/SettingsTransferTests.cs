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

using System.Text.Json.Nodes;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The portable settings file carries preferences and never a secret or this machine's state.
/// </summary>
public sealed class SettingsTransferTests
{
    private const string Profile = @"C:\Users\someone";

    [Fact]
    public void AnExportCarriesNoSecretAndNoMachineState()
    {
        AppSettings settings = new()
        {
            DefaultTheme = "Tarn",
            PinHash = "pin-hash",
            PinSalt = "pin-salt",
            VaultEnabled = true,
            CmdLibGitSyncToken = "token",
            CredentialProviderUnlockSecretEncrypted = "unlock",
            ShowToolsPanel = true,
        };
        settings.SshGateways.Add(new SshGatewayDto { Id = "gw", SshPasswordEncrypted = "secret" });

        JsonObject exported = SettingsTransfer.Export(settings, includeUserPaths: false, Profile, out _);
        JsonObject carried = exported[SettingsTransfer.SettingsProperty]!.AsObject();

        Assert.Equal("Tarn", carried[nameof(AppSettings.DefaultTheme)]!.GetValue<string>());
        foreach (string withheld in new[]
        {
            nameof(AppSettings.PinHash), nameof(AppSettings.PinSalt), nameof(AppSettings.VaultEnabled),
            nameof(AppSettings.CmdLibGitSyncToken), nameof(AppSettings.CredentialProviderUnlockSecretEncrypted),
            nameof(AppSettings.ShowToolsPanel), nameof(AppSettings.SshGateways), nameof(AppSettings.ExtensionData),
        })
        {
            Assert.False(carried.ContainsKey(withheld), withheld + " must not travel");
        }

        string text = exported.ToJsonString();
        Assert.DoesNotContain("pin-hash", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.True(SettingsTransfer.TransferableKeys.Count >= 60, "the allow-list is not reading the panel");
    }

    [Fact]
    public void APathInTheUserProfileStaysBehindUnlessAskedFor()
    {
        AppSettings settings = new() { PlinkPath = Profile + @"\tools\plink.exe", PuttyPath = @"D:\tools\putty.exe" };

        JsonObject withheld = SettingsTransfer.Export(settings, includeUserPaths: false, Profile, out int heldBack);
        JsonObject included = SettingsTransfer.Export(settings, includeUserPaths: true, Profile, out _);

        Assert.True(heldBack >= 1);
        Assert.False(withheld[SettingsTransfer.SettingsProperty]!.AsObject().ContainsKey(nameof(AppSettings.PlinkPath)));
        Assert.True(withheld[SettingsTransfer.SettingsProperty]!.AsObject().ContainsKey(nameof(AppSettings.PuttyPath)));
        Assert.Equal(Profile + @"\tools\plink.exe", included[SettingsTransfer.SettingsProperty]![nameof(AppSettings.PlinkPath)]!.GetValue<string>());
    }

    [Fact]
    public void AnImportTakesOnlyTransferableKeysAndNamesWhatChanges()
    {
        AppSettings current = new() { DefaultTheme = "Drakul", PinHash = "mine" };
        JsonObject file = new()
        {
            [SettingsTransfer.FormatProperty] = SettingsTransfer.FormatName,
            [SettingsTransfer.VersionProperty] = SettingsTransfer.FormatVersion,
            [SettingsTransfer.SettingsProperty] = new JsonObject
            {
                [nameof(AppSettings.DefaultTheme)] = "Tarn",
                [nameof(AppSettings.MaxEmbeddedSessions)] = current.MaxEmbeddedSessions,
                [nameof(AppSettings.PinHash)] = "theirs",
            },
        };

        (AppSettings merged, IReadOnlyList<string> changed) = SettingsTransfer.Import(current, file);

        Assert.Equal("Tarn", merged.DefaultTheme);
        Assert.Equal("mine", merged.PinHash);
        Assert.Equal([nameof(AppSettings.DefaultTheme)], changed);
    }

    [Fact]
    public void AnythingElseIsRefusedAsAFile()
    {
        AppSettings current = new();

        Assert.Throws<FormatException>(() => SettingsTransfer.Import(current, new JsonObject { ["servers"] = new JsonArray() }));
        Assert.Throws<FormatException>(() => SettingsTransfer.Import(current, new JsonObject
        {
            [SettingsTransfer.FormatProperty] = SettingsTransfer.FormatName,
            [SettingsTransfer.VersionProperty] = SettingsTransfer.FormatVersion + 1,
            [SettingsTransfer.SettingsProperty] = new JsonObject(),
        }));
    }
}
