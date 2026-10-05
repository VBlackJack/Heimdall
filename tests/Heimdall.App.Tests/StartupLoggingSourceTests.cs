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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The logging switch is read at startup, not only when a settings change is saved. Before,
/// logging turned off came back on at every start until an unrelated write raised the change.
/// </summary>
/// <remarks>
/// A source reading, because the startup path belongs to a WPF application that cannot be
/// constructed here. Both statements are carried through
/// <see cref="ViewSource.IsStatementOfTheMethodBody"/>, so a call folded under a condition does
/// not pass, and the switch must follow the load it reads.
/// </remarks>
public sealed class StartupLoggingSourceTests
{
    private const string LoadMember =
        "private static async Task<AppSettings> LoadStartupSettingsAsync(IConfigManager configManager)";

    private const string LoadStatement = "AppSettings settings = await configManager.LoadSettingsAsync();";

    private const string ApplyStatement = "Core.Logging.FileLogger.SetEnabled(settings.EnableLogging);";

    [Fact]
    public void StartupSettingsLoad_AppliesTheLoggingSwitchItRead()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadAppSource()),
            LoadMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, LoadStatement), "the startup settings load is not a step of its method");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ApplyStatement), "the logging switch is not applied at startup");
        Assert.True(
            logic.IndexOf(LoadStatement, StringComparison.Ordinal) < logic.IndexOf(ApplyStatement, StringComparison.Ordinal),
            "the logging switch is applied before the settings are loaded");
    }

    private static string ReadAppSource()
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "App.xaml.cs");
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
