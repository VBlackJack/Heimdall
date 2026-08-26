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
using Heimdall.App.ViewModels.Dialogs;

namespace Heimdall.App.Tests;

/// <summary>
/// What the server dialog's one test control is allowed to be, and to promise.
/// </summary>
/// <remarks>
/// A first-time user pressed "Test connection" believing it validated his credentials. It opened
/// a TCP socket. There were two such buttons under two different labels, both sitting inside a
/// credentials card - the SSH one directly under the password box and under a sentence about how
/// those credentials would be used - and the SSH result chip said "Server reachable" with no
/// mention of what had not been checked.
///
/// These tests freeze the two decisions taken in response, because both are the kind that erode
/// quietly: one control for one act, and a promise that names its own limit.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogReachabilityScopeTests
{
    private static readonly string[] AddressedProtocols =
        ["SSH", "SFTP", "RDP", "WINRM", "TELNET", "VNC", "FTP"];

    // Four of these had no test control at all before. The point of moving it to the address is
    // that one probe serves every protocol that HAS an address, so the answer stops depending on
    // which credentials card a protocol happens to render.
    [Theory]
    [InlineData("SSH")]
    [InlineData("SFTP")]
    [InlineData("RDP")]
    [InlineData("WINRM")]
    [InlineData("TELNET")]
    [InlineData("VNC")]
    [InlineData("FTP")]
    public void EveryAddressedProtocol_OffersTheReachabilityTest(string connectionType)
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "Host",
            RemoteServer = "host.example.com",
            ConnectionType = connectionType
        };

        Assert.True(
            vm.SupportsReachabilityTest,
            $"{connectionType} has an address, so it must offer the reachability test.");
        Assert.True(
            vm.TestReachabilityCommand.CanExecute(null),
            $"{connectionType} with an address and a default port must be testable.");
    }

    // Offering a dead control is the same defect one protocol over: the user presses it, nothing
    // useful happens, and they are left asking what it was for. Local Shell has no address at all;
    // Citrix reaches its StoreFront by URL, which is why its Server/Port row is collapsed.
    [Theory]
    [InlineData("Local")]
    [InlineData("Citrix")]
    public void ProtocolsWithoutAnAddress_DoNotOfferIt(string connectionType)
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "Host",
            RemoteServer = "host.example.com",
            ConnectionType = connectionType
        };

        Assert.False(vm.SupportsReachabilityTest);
        Assert.False(vm.TestReachabilityCommand.CanExecute(null));
    }

    [Fact]
    public void WithoutAnAddress_TheTestIsNotExecutable()
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "Host",
            ConnectionType = "SSH",
            RemoteServer = ""
        };

        Assert.True(vm.SupportsReachabilityTest);
        Assert.False(vm.TestReachabilityCommand.CanExecute(null));
    }

    // The load-bearing half. Every string the control can put on screen must state that
    // credentials were not checked - otherwise the chip is exactly the "Server reachable" that
    // was read as approval. Asserting the strings exist would pass while they said anything at
    // all; asserting what they DENY is what pins the meaning.
    [Theory]
    [InlineData("en", "Credentials were not checked")]
    [InlineData("fr", "identifiants n'ont pas")]
    public void EverySuccessMessage_SaysCredentialsWereNotChecked(string locale, string denial)
    {
        using JsonDocument document = LoadLocale(locale);

        foreach (string key in new[]
                 {
                     "ServerDialogReachabilityChipSuccess",
                     "ServerDialogReachabilityChipSuccessSsh"
                 })
        {
            Assert.True(
                document.RootElement.TryGetProperty(key, out JsonElement value),
                $"'{key}' is missing from {locale}.json");
            Assert.Contains(
                denial,
                value.GetString() ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    // The hint is read before the click; the chip only after. A user who has already formed the
    // wrong expectation has formed it by then, which is why the disclaimer RDP already carried on
    // its result chip did not stop the misreading.
    [Theory]
    [InlineData("en", "Does not check")]
    [InlineData("fr", "Ne v")]
    public void TheHint_DeniesTheCredentialCheckBeforeTheClick(string locale, string denial)
    {
        using JsonDocument document = LoadLocale(locale);

        Assert.True(
            document.RootElement.TryGetProperty("ServerDialogReachabilityHint", out JsonElement hint),
            $"'ServerDialogReachabilityHint' is missing from {locale}.json");
        Assert.Contains(denial, hint.GetString() ?? "", StringComparison.Ordinal);
    }

    // The two buttons that caused this are gone, and nothing may quietly reintroduce one under a
    // label that promises more than it does.
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void TheOldTestButtonKeys_AreGone(string locale)
    {
        using JsonDocument document = LoadLocale(locale);

        foreach (string retired in new[]
                 {
                     "ServerDialogTestConnectionButton",
                     "ServerDialogRdpTestButton",
                     "ServerDialogTestChipSuccess"
                 })
        {
            Assert.False(
                document.RootElement.TryGetProperty(retired, out _),
                $"'{retired}' is back in {locale}.json; the dialog has one test control, not two.");
        }
    }

    private static JsonDocument LoadLocale(string locale)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory!.FullName, "locales", $"{locale}.json")));
    }

    [Fact]
    public void TheAddressedProtocolList_IsNotEmpty()
    {
        // Guards the theories above against silently becoming vacuous if the list is emptied.
        Assert.NotEmpty(AddressedProtocols);
    }
}
