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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class ServerDialogLocalForwardsTests
{
    [Fact]
    public void FromDtoAndToDto_RoundTripLocalForwardsIncludingIpv6AndSpaceSyntax()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "App Server",
            ConnectionType = "SSH",
            RemoteServer = "app.internal",
            SshUsername = "ops",
            SshLocalForwards = ["8080:127.0.0.1:80", "[::1]:8443:db.internal:5432"]
        };

        var vm = ServerDialogViewModel.FromDto(dto);
        Assert.Contains("8080:127.0.0.1:80", vm.SshLocalForwardsText, StringComparison.Ordinal);
        Assert.Contains("[::1]:8443:db.internal:5432", vm.SshLocalForwardsText, StringComparison.Ordinal);

        vm.SshLocalForwardsText += Environment.NewLine + "  9090 metrics.internal:9100  ";
        vm.ValidateCommand.Execute(null);
        Assert.Null(vm.SshLocalForwardsError);

        var updated = vm.ToDto();
        Assert.Equal(
            ["8080:127.0.0.1:80", "[::1]:8443:db.internal:5432", "9090:metrics.internal:9100"],
            updated.SshLocalForwards);
    }

    [Fact]
    public void Validate_RejectsMalformedLocalForwardLineForSshProfiles()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Bad Forward",
            ConnectionType = "SSH",
            RemoteServer = "app.internal",
            SshUsername = "ops"
        };

        var vm = ServerDialogViewModel.FromDto(dto);
        vm.SshLocalForwardsText = "8080:127.0.0.1:80\nnot-a-valid-forward";
        vm.ValidateCommand.Execute(null);

        Assert.NotNull(vm.SshLocalForwardsError);
        Assert.Equal(nameof(ServerDialogViewModel.SshLocalForwardsText), vm.FirstInvalidField);
        Assert.NotNull(vm.ValidationError);
    }
}
