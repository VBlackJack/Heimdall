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
using System.Text.RegularExpressions;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Tests;

/// <summary>
/// The session row's hover text used to repeat the name the row was already printing. It now has to
/// say something the row does not show, and it has to stay out of the status dot's territory.
/// </summary>
public sealed class ServerItemRowTooltipTests
{
    [Fact]
    public void RowTooltip_NamesTheHostTheRowNeverShows()
    {
        ServerItemViewModel server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "alpha",
            DisplayName = "Alpha",
            RemoteServer = "alpha.example.test",
            ConnectionType = "SSH",
            SshPort = 2222,
            SshUsername = "operator"
        });

        string tooltip = Assert.IsType<string>(server.RowTooltipText);

        Assert.Contains("alpha.example.test:2222", tooltip, StringComparison.Ordinal);
        Assert.Contains("operator", tooltip, StringComparison.Ordinal);
        Assert.Contains("SSH", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void RowTooltip_LeavesOutTheUserLineWhenNoAccountIsConfigured()
    {
        ServerItemViewModel server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "alpha",
            DisplayName = "Alpha",
            RemoteServer = "alpha.example.test",
            ConnectionType = "SSH",
            SshPort = 22
        });

        string tooltip = Assert.IsType<string>(server.RowTooltipText);

        Assert.Contains("alpha.example.test:22", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("User", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool row has no host, no account and no protocol worth naming. Nothing to add means no
    /// tooltip, rather than a tooltip that repeats the row back at the reader.
    /// </summary>
    [Fact]
    public void RowTooltip_IsAbsentWhenThereIsNothingTheRowDoesNotAlreadyShow()
    {
        ServerItemViewModel tool = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "base64",
            DisplayName = "Base64",
            RemoteServer = "",
            ConnectionType = "tool:base64"
        });

        Assert.Null(tool.RowTooltipText);
    }

    /// <summary>
    /// The decision taken on P3-12: the health verdict stays on the status dot. The dot is the
    /// control the verdict belongs to and its tooltip is the only place the verdict is spelled out;
    /// folding it into the row - a far larger hover target - would make the dot's own tooltip
    /// unreachable in practice.
    /// </summary>
    [Fact]
    public void RowTooltip_LeavesTheHealthVerdictToTheStatusDot()
    {
        ServerItemViewModel server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "alpha",
            DisplayName = "Alpha",
            RemoteServer = "alpha.example.test",
            ConnectionType = "SSH",
            SshPort = 22
        });
        server.HealthState = new HealthState(
            HealthStatus.Down,
            DateTime.UtcNow,
            null,
            "timeout");

        string verdict = server.StatusTooltipText;
        string tooltip = Assert.IsType<string>(server.RowTooltipText);

        Assert.False(string.IsNullOrWhiteSpace(verdict));
        Assert.DoesNotContain(verdict, tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void RowTooltip_TracksTheHostItDescribes()
    {
        ServerItemViewModel server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "alpha",
            DisplayName = "Alpha",
            RemoteServer = "alpha.example.test",
            ConnectionType = "RDP",
            RemotePort = 3389
        });
        List<string> raised = [];
        server.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        server.RemoteServer = "moved.example.test";

        Assert.Contains(nameof(ServerItemViewModel.RowTooltipText), raised);
        Assert.Contains(
            "moved.example.test:3389",
            Assert.IsType<string>(server.RowTooltipText),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The markup half: the row has to be bound to the composed text, and the dot has to keep its
    /// own. Composition in the view model is worth nothing if the template still reads DisplayName.
    /// </summary>
    [Fact]
    public void SessionRowMarkup_BindsTheComposedTooltipAndKeepsTheDotsOwn()
    {
        string markup = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        Match row = Regex.Match(
            markup,
            "<Border x:Name=\"ServerSelectionChrome\".*?ToolTipService.InitialShowDelay",
            RegexOptions.Singleline);

        Assert.True(row.Success, "The session row chrome was not found in MainWindow.xaml.");
        Assert.Contains("ToolTip=\"{Binding RowTooltipText}\"", row.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=\"{Binding DisplayName}\"", row.Value, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding StatusTooltipText}\"", markup, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Heimdall.slnx was not found above the test output.");
    }
}
