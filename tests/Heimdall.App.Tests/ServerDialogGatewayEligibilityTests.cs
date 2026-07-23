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

public sealed class ServerDialogGatewayEligibilityTests
{
    [Fact]
    public void Dialog_GatewayFieldHidden_ForIneligibleProtocol()
    {
        var viewModel = new ServerDialogViewModel
        {
            ConnectionType = "TELNET",
            DirectConnection = false,
            SelectedGatewayId = "gateway-01"
        };

        Assert.False(viewModel.SupportsGateway);
        Assert.False(viewModel.CanSelectGateway);
        Assert.False(viewModel.UsesGateway);

        XDocument document = LoadServerDialogXaml();
        XElement style = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Style"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Key"
                    && attribute.Value == "NetworkSectionStyle"));
        Assert.Contains(
            style.Elements(),
            element => IsVisibilitySetter(element, "Collapsed"));
        XElement trigger = Assert.Single(
            style.Descendants(),
            element => element.Name.LocalName == "DataTrigger");
        Assert.Equal("{Binding SupportsGateway}", trigger.Attribute("Binding")?.Value);
        Assert.Equal("True", trigger.Attribute("Value")?.Value);
        Assert.Contains(
            trigger.Elements(),
            element => IsVisibilitySetter(element, "Visible"));
    }

    [Theory]
    [InlineData("TELNET")]
    [InlineData("VNC")]
    [InlineData("FTP")]
    [InlineData("CITRIX")]
    [InlineData("LOCAL")]
    [InlineData("TOOL:PING")]
    public void ToDto_DoesNotPersistGatewayState_ForIneligibleProtocol(string connectionType)
    {
        var viewModel = new ServerDialogViewModel
        {
            ConnectionType = connectionType,
            DirectConnection = true,
            SelectedGatewayId = "gateway-01"
        };

        ServerProfileDto dto = viewModel.ToDto();

        Assert.Null(dto.SshGatewayId);
        Assert.False(dto.UseDirectConnection);
    }

    private static bool IsVisibilitySetter(XElement element, string value)
    {
        return element.Name.LocalName == "Setter"
               && element.Attribute("Property")?.Value == "Visibility"
               && element.Attribute("Value")?.Value == value;
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
