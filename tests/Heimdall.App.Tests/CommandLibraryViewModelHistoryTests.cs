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
using TwinShell.Core.Enums;
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

public sealed class CommandLibraryViewModelHistoryTests
{
    /// <summary>
    /// History must never persist user-entered secrets: the payload stores the
    /// un-substituted command pattern and drops every parameter value, because the
    /// history store (twinshell.db) is an unencrypted SQLite file.
    /// </summary>
    [Fact]
    public void BuildSecretFreeHistoryPayload_StoresPattern_AndDropsParameterValues()
    {
        var template = new CommandTemplate
        {
            Platform = Platform.Linux,
            Name = "Connect",
            CommandPattern = "mysql -u root -p{password}",
            Parameters =
            {
                new TemplateParameter { Name = "password", Label = "Password", Type = "string" },
            },
        };

        var (command, parameters) = CommandLibraryViewModel.BuildSecretFreeHistoryPayload(template);

        // Pattern is kept verbatim (placeholder, not a substituted secret).
        Assert.Equal("mysql -u root -p{password}", command);
        // No parameter values are carried into history.
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildSecretFreeHistoryPayload_NullTemplate_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => CommandLibraryViewModel.BuildSecretFreeHistoryPayload(null!));
    }
}
