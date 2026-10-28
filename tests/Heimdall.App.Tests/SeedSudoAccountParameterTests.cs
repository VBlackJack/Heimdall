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
using System.Text.Json;
using FluentAssertions;
using TwinShell.Core.Helpers;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// A shipped command run through <c>sudo</c> acts as root, so an optional account parameter
/// described as "empty = current user" would act on root, not on the operator.
/// </summary>
public sealed class SeedSudoAccountParameterTests
{
    private const string PasswdActionFile = "passwd-change-password.json";

    [Fact]
    public void PasswdAction_LeavingTheAccountEmpty_IsRefusedInsteadOfChangingRoot()
    {
        var template = LoadAction(PasswdActionFile).LinuxCommandTemplate
            ?? throw new InvalidOperationException($"{PasswdActionFile} has no Linux command template.");
        var service = new CommandGeneratorService(new FakeTwinShellLocalizationService());

        var generate = () => service.GenerateCommand(template, new Dictionary<string, string>());

        generate.Should().Throw<InvalidOperationException>();
        service.GenerateCommand(template, new Dictionary<string, string> { ["username"] = "john" })
            .Should().Be("sudo passwd john");
    }

    [Fact]
    public void NoSudoSeedCommand_OffersAnOptionalParameterMeaningTheCurrentUser()
    {
        var files = Directory.EnumerateFiles(CommandGeneratorServiceTests.FindSeedActionsDirectory(), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        files.Count.Should().BeGreaterThanOrEqualTo(400, "the walk has to reach the seed catalogue");

        var offenders = new List<string>();
        foreach (var path in files)
        {
            var action = LoadAction(Path.GetFileName(path));
            var template = action.LinuxCommandTemplate;
            if (template?.Parameters is null
                || !template.CommandPattern.Contains("sudo", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.AddRange(template.Parameters
                .Where(parameter => !parameter.Required
                    && (parameter.Description ?? string.Empty).Contains("current user", StringComparison.OrdinalIgnoreCase))
                .Select(parameter => $"{Path.GetFileName(path)} ({parameter.Name})"));
        }

        offenders.Should().BeEmpty("under sudo the current user is root");
    }

    private static ActionModel LoadAction(string fileName)
    {
        var path = Path.Combine(CommandGeneratorServiceTests.FindSeedActionsDirectory(), fileName);
        return JsonSerializer.Deserialize<ActionModel>(File.ReadAllText(path), JsonOptionsHelper.CaseInsensitive)
            ?? throw new InvalidOperationException($"Could not deserialize seed action {fileName}.");
    }
}
