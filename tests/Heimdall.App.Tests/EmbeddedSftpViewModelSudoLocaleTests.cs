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

using Heimdall.App.ViewModels;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

/// <summary>
/// The SFTP browser starts every sudo as <c>env LC_ALL=C sudo</c>, and a missing sudo reported
/// through <c>env</c> is still read as a missing sudo.
/// </summary>
public sealed class EmbeddedSftpViewModelSudoLocaleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildSudoInvocation_StartsWithTheSudoProgram(bool authenticateViaStdin)
    {
        string command = EmbeddedSftpViewModel.BuildSudoInvocation("true", authenticateViaStdin);

        Assert.StartsWith(PrivilegedFileCommands.SudoProgram + " ", command, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSudoSucceeded_MissingSudoThroughEnv_IsSudoMissing()
    {
        var failure = Assert.Throws<SudoAuthenticationException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(
                127,
                "env: 'sudo': No such file or directory",
                "command"));

        Assert.Equal(SudoFailureKind.SudoMissing, failure.Kind);
    }
}
