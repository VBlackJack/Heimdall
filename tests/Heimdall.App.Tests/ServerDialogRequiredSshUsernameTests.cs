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

namespace Heimdall.App.Tests;

/// <summary>
/// The SSH login name is required to connect, so it has to say so - and only where it is true.
/// </summary>
/// <remarks>
/// It was required by the connect path, unmarked in the dialog, and unvalidated. The
/// already-translated key ValidationInlineSshUserRequired existed in both locales with zero
/// references in src/, which is the reliable marker of a half-shipped surface: the product knew
/// what to say about this field and never said it.
///
/// The half that needed measuring was where it is NOT required. External SSH hands off to PuTTY
/// before the guard is ever reached and PuTTY asks for the login name itself, so an asterisk there
/// would put a false statement on screen - the same defect class this change exists to remove. The
/// predicate mirrors ConnectionHelpers.RequiresUsernameToConnect and the order of the guards that
/// consume it.
/// </remarks>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogRequiredSshUsernameTests
{
    private static ServerDialogViewModel Profile(string connectionType, string sshMode = "Embedded") =>
        new()
        {
            DisplayName = "Host",
            RemoteServer = "host.example.com",
            ConnectionType = connectionType,
            SshMode = sshMode
        };

    [Theory]
    [InlineData("SSH", "Embedded")]
    [InlineData("SFTP", "Embedded")]
    [InlineData("SFTP", "External")]  // SFTP has no external launcher; the mode cannot excuse it
    public void WhereItIsRequired_TheLabelSaysSo(string connectionType, string sshMode)
    {
        ServerDialogViewModel vm = Profile(connectionType, sshMode);

        Assert.True(vm.RequiresSshUsername);
        Assert.EndsWith(" *", vm.SshUsernameLabel, StringComparison.Ordinal);
    }

    // The measured half. PuTTY prompts for the login name, so the field is genuinely optional and
    // an asterisk would be a lie.
    [Fact]
    public void ExternalSsh_IsNotMarkedRequired()
    {
        ServerDialogViewModel vm = Profile("SSH", sshMode: "External");

        Assert.False(vm.RequiresSshUsername);
        Assert.DoesNotContain("*", vm.SshUsernameLabel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RDP")]
    [InlineData("WINRM")]
    [InlineData("TELNET")]
    [InlineData("VNC")]
    public void ProtocolsThatDoNotUseIt_AreUnaffected(string connectionType)
    {
        ServerDialogViewModel vm = Profile(connectionType);

        Assert.False(vm.RequiresSshUsername);
        Assert.DoesNotContain("*", vm.SshUsernameLabel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatingWithoutIt_ProducesTheInlineError(string? username)
    {
        ServerDialogViewModel vm = Profile("SSH");
        vm.SshUsername = username!;

        vm.ValidateCommand.Execute(null);

        Assert.False(string.IsNullOrEmpty(vm.SshUsernameError));
        Assert.Equal(vm.SshUsernameError, vm.ValidationError);
    }

    [Fact]
    public void ValidatingWithIt_ProducesNoError()
    {
        ServerDialogViewModel vm = Profile("SSH");
        vm.SshUsername = "operator";

        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.SshUsernameError);
    }

    [Fact]
    public void ExternalSsh_ValidatesWithoutAUsername()
    {
        ServerDialogViewModel vm = Profile("SSH", sshMode: "External");
        vm.SshUsername = "";

        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.SshUsernameError);
    }

    // The error has to clear as the user fixes it, the way the server address already does.
    // Otherwise it sits under a field that is no longer wrong until the next Save.
    [Fact]
    public void TypingAUsername_ClearsTheError()
    {
        ServerDialogViewModel vm = Profile("SSH");
        vm.SshUsername = "";
        vm.ValidateCommand.Execute(null);
        Assert.False(string.IsNullOrEmpty(vm.SshUsernameError));

        vm.SshUsername = "operator";

        Assert.Null(vm.SshUsernameError);
    }

    // Switching to External must not leave a stale error under a field that is now optional.
    [Fact]
    public void SwitchingToExternal_DropsTheRequirement()
    {
        ServerDialogViewModel vm = Profile("SSH");
        vm.SshUsername = "";
        vm.ValidateCommand.Execute(null);
        Assert.True(vm.RequiresSshUsername);

        vm.SshMode = "External";

        Assert.False(vm.RequiresSshUsername);
        Assert.DoesNotContain("*", vm.SshUsernameLabel, StringComparison.Ordinal);
    }
}
