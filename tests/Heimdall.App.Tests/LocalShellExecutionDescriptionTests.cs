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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The local command row of an execution question keeps a value inside its quotes: a backslash
/// before a quote cannot fake the end of a value, and blank-looking padding is written out.
/// </summary>
public sealed class LocalShellExecutionDescriptionTests
{
    private static async Task<LocalizationManager> LoadLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static ServerProfileDto LocalProfile(string? arguments = null, string? workingDirectory = null) => new()
    {
        ConnectionType = "LOCAL",
        LocalShellExecutable = "tool.exe",
        LocalShellArguments = arguments,
        LocalShellWorkingDirectory = workingDirectory,
    };

    [Fact]
    public async Task BackslashBeforeAQuote_CannotFakeTheEndOfTheValue()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();

        string described = ServerListViewModel.DescribeLocalShellExecution(
            LocalProfile(arguments: @"x\""; working directory: ""C:\good"),
            localizer);

        Assert.Contains(@"arguments: ""x\\\""; working directory: \""C:\good""", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkingDirectoryEndingInABackslash_DoublesItBeforeTheClosingQuote()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();

        string described = ServerListViewModel.DescribeLocalShellExecution(
            LocalProfile(workingDirectory: @"C:\dir\"),
            localizer);

        Assert.Contains(@"working directory: ""C:\dir\\""", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArgumentsPaddedWithSpaces_ShowTheCountedPadding()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        string padded = new string(' ', 300) + "working directory: \"C:\\evil\"";

        string described = ServerListViewModel.DescribeLocalShellExecution(LocalProfile(arguments: padded), localizer);

        Assert.Contains(@"\u{0020*300}", described, StringComparison.Ordinal);
        Assert.DoesNotContain(new string(' ', 10), described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramEndingInABackslash_IsShownWithOneBackslash()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        ServerProfileDto profile = LocalProfile();
        profile.LocalShellExecutable = @"C:\dir\";

        string described = ServerListViewModel.DescribeLocalShellExecution(profile, localizer);

        Assert.StartsWith(@"C:\dir\", described, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\dir\\", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramWithABackslashBeforeAQuote_StillDoublesIt()
    {
        LocalizationManager localizer = await LoadLocalizerAsync();
        ServerProfileDto profile = LocalProfile();
        profile.LocalShellExecutable = @"a\""b";

        string described = ServerListViewModel.DescribeLocalShellExecution(profile, localizer);

        Assert.StartsWith(@"a\\\""b", described, StringComparison.Ordinal);
    }
}
