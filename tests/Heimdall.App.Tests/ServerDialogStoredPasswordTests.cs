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
using System.Xml.Linq;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// An empty password box means "keep what is stored", so emptying it is not a removal and the
/// dialog offered no other one. These pin the state line and the button that answer both halves
/// of the question: is a password saved, and how do I get rid of it.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogStoredPasswordTests
{
    private const string StoredCipher = "cipher-written-by-a-previous-save";

    [Theory]
    [InlineData("Rdp")]
    [InlineData("WinRm")]
    [InlineData("Ssh")]
    [InlineData("Vnc")]
    [InlineData("Ftp")]
    public void ClearingAStoredPassword_RemovesItFromTheSavedProfile(string credential)
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(SeedWithStoredPassword(credential));

        Assert.True(HasStoredPassword(vm, credential));

        ExecuteClearCommand(vm, credential);

        Assert.False(HasStoredPassword(vm, credential));
        Assert.Null(ReadStoredPassword(vm.ToDto(), credential));
    }

    // The counterweight: the fix must not turn every visit into a credential wipe. Leaving the
    // box alone still means "keep", which is the rule the finding explicitly asked to preserve.
    [Theory]
    [InlineData("Rdp")]
    [InlineData("WinRm")]
    [InlineData("Ssh")]
    [InlineData("Vnc")]
    [InlineData("Ftp")]
    public void ToDto_KeepsTheStoredSecret_WhenTheUserClearsNothing(string credential)
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(SeedWithStoredPassword(credential));

        Assert.Equal(StoredCipher, ReadStoredPassword(vm.ToDto(), credential));
    }

    [Fact]
    public void ClearingAStoredSecret_ArmsTheUnsavedChangesGuard()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(SeedWithStoredPassword("Rdp"));

        Assert.False(vm.IsDirty);

        vm.ClearStoredRdpPasswordCommand.Execute(null);

        Assert.True(vm.IsDirty);
    }

    // The notification, not the value, is what is observed: the getter recomputes on every read,
    // so an omitted raise leaves the card showing a promise the profile no longer keeps while a
    // value-only assertion stays green.
    [Fact]
    public void SshAuthHint_StopsPromisingPasswordAuth_OnceTheStoredSecretIsCleared()
    {
        LocalizationManager localizer = new();
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(SeedWithStoredPassword("Ssh"));
        vm.Localizer = localizer;

        Assert.Equal(localizer["ServerDialogSshAuthHintPassword"], vm.SshAuthHint);

        List<string> raised = [];
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.ClearStoredSshPasswordCommand.Execute(null);

        Assert.Contains(nameof(ServerDialogViewModel.SshAuthHint), raised);
        Assert.Equal(localizer["ServerDialogSshAuthHintAgent"], vm.SshAuthHint);
    }

    // A view-model command with no host is a guard attached to nothing, which this repository has
    // shipped before. Every card must carry both the state line and the button.
    [Theory]
    [InlineData("Rdp")]
    [InlineData("WinRm")]
    [InlineData("Ssh")]
    [InlineData("Vnc")]
    [InlineData("Ftp")]
    public void EveryCredentialCardOffersTheRemoval(string credential)
    {
        XDocument document = LoadServerDialogXaml();

        XElement passwordBox = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "PasswordBox"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == credential + "PasswordBox"));

        XElement card = passwordBox.Ancestors().First(element => element.Name.LocalName == "Border");

        Assert.Contains(
            card.Descendants(),
            element => element.Attribute("Visibility")?.Value
                == $"{{Binding HasStored{credential}Password, Converter={{StaticResource BoolToVisibilityConverter}}}}");

        Assert.Contains(
            card.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value
                    == $"{{Binding ClearStored{credential}PasswordCommand}}");
    }

    private static ServerProfileDto SeedWithStoredPassword(string credential)
    {
        ServerProfileDto dto = new()
        {
            DisplayName = "Session",
            RemoteServer = "host.example.com",
            ConnectionType = ProtocolOf(credential)
        };

        switch (credential)
        {
            case "Rdp":
                dto.RdpPasswordEncrypted = StoredCipher;
                break;
            case "WinRm":
                dto.WinRmPasswordEncrypted = StoredCipher;
                break;
            case "Ssh":
                dto.SshPasswordEncrypted = StoredCipher;
                break;
            case "Vnc":
                dto.VncPassword = StoredCipher;
                break;
            case "Ftp":
                dto.FtpPasswordEncrypted = StoredCipher;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(credential), credential, "Unknown credential card.");
        }

        return dto;
    }

    private static string ProtocolOf(string credential) => credential switch
    {
        "Rdp" => "RDP",
        "WinRm" => "WINRM",
        "Ssh" => "SSH",
        "Vnc" => "VNC",
        "Ftp" => "FTP",
        _ => throw new ArgumentOutOfRangeException(nameof(credential), credential, "Unknown credential card.")
    };

    // VNC is the odd one: its cipher rides in the DTO field named VncPassword, not VncPasswordEncrypted.
    private static string? ReadStoredPassword(ServerProfileDto dto, string credential) => credential switch
    {
        "Rdp" => dto.RdpPasswordEncrypted,
        "WinRm" => dto.WinRmPasswordEncrypted,
        "Ssh" => dto.SshPasswordEncrypted,
        "Vnc" => dto.VncPassword,
        "Ftp" => dto.FtpPasswordEncrypted,
        _ => throw new ArgumentOutOfRangeException(nameof(credential), credential, "Unknown credential card.")
    };

    private static bool HasStoredPassword(ServerDialogViewModel vm, string credential) => credential switch
    {
        "Rdp" => vm.HasStoredRdpPassword,
        "WinRm" => vm.HasStoredWinRmPassword,
        "Ssh" => vm.HasStoredSshPassword,
        "Vnc" => vm.HasStoredVncPassword,
        "Ftp" => vm.HasStoredFtpPassword,
        _ => throw new ArgumentOutOfRangeException(nameof(credential), credential, "Unknown credential card.")
    };

    private static void ExecuteClearCommand(ServerDialogViewModel vm, string credential)
    {
        switch (credential)
        {
            case "Rdp":
                vm.ClearStoredRdpPasswordCommand.Execute(null);
                break;
            case "WinRm":
                vm.ClearStoredWinRmPasswordCommand.Execute(null);
                break;
            case "Ssh":
                vm.ClearStoredSshPasswordCommand.Execute(null);
                break;
            case "Vnc":
                vm.ClearStoredVncPasswordCommand.Execute(null);
                break;
            case "Ftp":
                vm.ClearStoredFtpPasswordCommand.Execute(null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(credential), credential, "Unknown credential card.");
        }
    }

    private static XDocument LoadServerDialogXaml()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(
            repoRoot,
            "src",
            "Heimdall.App",
            "Views",
            "Dialogs",
            "ServerDialog.xaml");

        Assert.True(File.Exists(path), $"Server dialog XAML not found: {path}");
        return XDocument.Load(path);
    }
}
