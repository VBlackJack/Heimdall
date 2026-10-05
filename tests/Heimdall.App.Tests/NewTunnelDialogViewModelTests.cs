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
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class NewTunnelDialogViewModelTests
{
    [Fact]
    public void NoGateways_DisablesConfirmation()
    {
        var vm = new NewTunnelDialogViewModel([], new LocalizationManager());

        Assert.False(vm.HasGateways);
        Assert.True(vm.HasNoGateways);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public void ValidInputs_EnableConfirmation()
    {
        var vm = new NewTunnelDialogViewModel([CreateGateway()], new LocalizationManager())
        {
            RemoteHost = "10.0.0.42",
            RemotePort = 5432,
            LocalPort = 9090
        };

        Assert.True(vm.ConfirmCommand.CanExecute(null));
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public void ActiveLocalPort_DisablesConfirmationAndShowsValidation()
    {
        var vm = new NewTunnelDialogViewModel(
            [CreateGateway()],
            new LocalizationManager(),
            new HashSet<int> { 9090 })
        {
            RemoteHost = "10.0.0.42",
            RemotePort = 5432,
            LocalPort = 9090
        };

        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.True(vm.HasValidationMessage);
    }

    // Bound to the numbers, a port box holding "abc" never reached the view model: it kept the
    // last good port, showed no message, and the tunnel opened to a port the box did not show.
    // Proved against the mutant that ignores text that does not parse: Confirm stays enabled.
    [Theory]
    [InlineData("abc")]
    [InlineData("22a")]
    [InlineData("")]
    [InlineData("-1")]
    public void APortBoxThatDoesNotHoldANumber_BlocksConfirmation(string text)
    {
        var vm = new NewTunnelDialogViewModel([CreateGateway()], new LocalizationManager())
        {
            RemoteHost = "10.0.0.42"
        };
        Assert.True(vm.ConfirmCommand.CanExecute(null));

        vm.RemotePortText = text;

        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.True(vm.HasValidationMessage);

        vm.RemotePortText = "5432";
        vm.LocalPortText = text;

        Assert.Equal(5432, vm.RemotePort);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public void SettingAPortNumber_ShowsItInItsBox()
    {
        var vm = new NewTunnelDialogViewModel([CreateGateway()], new LocalizationManager())
        {
            RemotePort = 5432,
            LocalPort = 15432
        };

        Assert.Equal("5432", vm.RemotePortText);
        Assert.Equal("15432", vm.LocalPortText);
    }

    [Theory]
    [InlineData("db.internal:5432")]
    [InlineData("db internal")]
    [InlineData("*.example.test")]
    public void ARemoteHostThatIsNotAHostNameOrAnAddress_BlocksConfirmation(string host)
    {
        var vm = new NewTunnelDialogViewModel([CreateGateway()], new LocalizationManager())
        {
            RemoteHost = host
        };

        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.Equal("NewTunnelValidationRemoteHostInvalid", vm.ValidationMessage);
    }

    [Theory]
    [InlineData("db.example.test")]
    [InlineData("server01")]
    [InlineData("10.0.0.42")]
    [InlineData("fe80::1")]
    public void AHostNameOrAnAddress_IsAccepted(string host)
    {
        var vm = new NewTunnelDialogViewModel([CreateGateway()], new LocalizationManager())
        {
            RemoteHost = host
        };

        Assert.True(vm.ConfirmCommand.CanExecute(null), vm.ValidationMessage);
    }

    private static SshGatewayDto CreateGateway()
    {
        return new SshGatewayDto
        {
            Id = "gateway-1",
            Name = "Gateway 1",
            Host = "gateway.example.test",
            Port = 22,
            User = "admin"
        };
    }
}
