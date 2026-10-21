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

using FluentAssertions;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Infrastructure.Tests;

public sealed class ActionLocalizerTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("en-GB")]
    [InlineData("es")]
    [InlineData("")]
    public void ThePivotOrAnUntranslatedCulture_ReturnsTheSameInstance(string culture)
    {
        ActionModel action = CreateAction();

        ActionLocalizer.Localize(action, culture).Should().BeSameAs(action);
    }

    [Fact]
    public void ATranslatedCulture_ReplacesThePivotFieldByField()
    {
        ActionModel action = CreateAction();

        ActionModel french = ActionLocalizer.Localize(action, "fr-FR");

        french.Should().NotBeSameAs(action);
        french.Id.Should().Be(action.Id);
        french.PublicId.Should().Be(action.PublicId);
        french.Title.Should().Be("Redémarrer un service");
        french.Description.Should().Be("Restarts a service.", "the translation carries no description");
        french.Notes.Should().Be("Droits administrateur requis.");
        french.WindowsCommandTemplate!.CommandPattern.Should().Be("Restart-Service -Name {serviceName} -Force");
    }

    [Fact]
    public void AMissingExampleKey_KeepsThePivotDescription()
    {
        ActionModel french = ActionLocalizer.Localize(CreateAction(), "fr");

        french.WindowsExamples.Single(e => e.Command == "Restart-Service -Name Spooler -Force")
            .Description.Should().Be("Redémarre le spouleur");
        french.WindowsExamples.Single(e => e.Command == "Restart-Service -Name W32Time -Force")
            .Description.Should().Be("Restarts the time service");
    }

    [Fact]
    public void AMissingParameterOrLabel_KeepsThePivotLabel()
    {
        ActionModel french = ActionLocalizer.Localize(CreateAction(), "fr");

        List<TemplateParameter> parameters = french.WindowsCommandTemplate!.Parameters;
        TemplateParameter serviceName = parameters.Single(p => p.Name == "serviceName");
        serviceName.Label.Should().Be("Service name", "the translation only carries a description for it");
        serviceName.Description.Should().Be("Nom du service à redémarrer");
        parameters.Single(p => p.Name == "computer").Label.Should().Be("Computer");
    }

    [Fact]
    public void TheCopy_DoesNotAliasTheSourceTemplatesOrExamples()
    {
        ActionModel action = CreateAction();

        ActionModel french = ActionLocalizer.Localize(action, "fr");

        french.WindowsCommandTemplate.Should().NotBeSameAs(action.WindowsCommandTemplate);
        action.WindowsCommandTemplate!.Parameters.Single(p => p.Name == "serviceName")
            .Description.Should().Be("Name of the service to restart");
        action.WindowsExamples[0].Description.Should().Be("Restarts the print spooler");
        action.Title.Should().Be("Restart a service");
    }

    private static ActionModel CreateAction()
    {
        return new ActionModel
        {
            Id = "svc",
            Title = "Restart a service",
            Description = "Restarts a service.",
            Notes = "Requires administrator rights.",
            Category = "services-automation",
            Platform = Platform.Windows,
            WindowsCommandTemplate = new CommandTemplate
            {
                Id = "svc-win",
                Platform = Platform.Windows,
                Name = "Restart-Service",
                CommandPattern = "Restart-Service -Name {serviceName} -Force",
                Parameters =
                [
                    new TemplateParameter { Name = "serviceName", Label = "Service name", Description = "Name of the service to restart" },
                    new TemplateParameter { Name = "computer", Label = "Computer" }
                ]
            },
            WindowsExamples =
            [
                new CommandExample { Command = "Restart-Service -Name Spooler -Force", Description = "Restarts the print spooler", Platform = Platform.Windows },
                new CommandExample { Command = "Restart-Service -Name W32Time -Force", Description = "Restarts the time service", Platform = Platform.Windows }
            ],
            Translations =
            [
                new ActionTranslation
                {
                    CultureCode = "fr",
                    Title = "Redémarrer un service",
                    Notes = "Droits administrateur requis.",
                    WindowsParameters = new()
                    {
                        ["serviceName"] = new ParameterTranslation(null, "Nom du service à redémarrer")
                    },
                    ExampleDescriptions = new()
                    {
                        ["Restart-Service -Name Spooler -Force"] = "Redémarre le spouleur"
                    }
                }
            ]
        };
    }
}
