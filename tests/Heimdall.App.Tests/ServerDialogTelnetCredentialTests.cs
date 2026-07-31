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

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogTelnetCredentialTests
{
    private static readonly XNamespace s_xamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Dialog_DoesNotExposeTelnetCredentialSectionOrControls()
    {
        XDocument document = LoadServerDialogXaml();
        XElement generalTab = Assert.Single(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(s_xamlNamespace + "Name"),
                "DlgSrv_TabGeneral",
                StringComparison.Ordinal));

        Assert.DoesNotContain(
            generalTab.Descendants(),
            element =>
                element.Name.LocalName == "DataTrigger"
                && string.Equals(
                    (string?)element.Attribute("Value"),
                    "Telnet",
                    StringComparison.Ordinal));

        foreach (string name in new[]
        {
            "DlgSrv_BasicTelnetCredentialsTitle",
            "DlgSrv_BasicTelnetCredentialsDesc",
            "DlgSrv_BasicTelnetUsernameLabel",
            "DlgSrv_TelnetUsernameBox",
            "DlgSrv_BasicTelnetPasswordLabel",
            "TelnetPasswordBox"
        })
        {
            Assert.DoesNotContain(
                document.Descendants(),
                element => string.Equals(
                    (string?)element.Attribute(s_xamlNamespace + "Name"),
                    name,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void FromDtoToDto_PreservesHistoricalTelnetCredentialsWithoutInput()
    {
        var source = new ServerProfileDto
        {
            Id = "telnet-history",
            DisplayName = "Historical Telnet",
            ConnectionType = "TELNET",
            RemoteServer = "telnet.example.com",
            TelnetPort = 2323,
            TelnetUsername = "historical-user",
            TelnetPasswordEncrypted = "historical-encrypted-password"
        };

        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(source);
        ServerProfileDto roundTripped = viewModel.ToDto();

        Assert.Equal(source.TelnetPort, roundTripped.TelnetPort);
        Assert.Equal(source.TelnetUsername, roundTripped.TelnetUsername);
        Assert.Equal(source.TelnetPasswordEncrypted, roundTripped.TelnetPasswordEncrypted);
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
