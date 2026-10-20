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
using Heimdall.App.Services.Import;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// An export carries the pivot text and its translations, and an import keeps both.
/// </summary>
/// <remarks>
/// The translation is applied when an action is shown, never before it is written: an
/// export taken by an operator working in French must still hold the English pivot, or the
/// next import would turn the French into the pivot and lose the English for good.
/// </remarks>
public sealed class CommandLibraryTranslationTransferTests
{
    [Fact]
    public async Task AnExport_HoldsThePivotAndTheTranslations_AndAnImportKeepsThem()
    {
        var service = new CommandLibraryTransferService();
        var source = CommandLibraryTestHelpers.CreateLinuxAction("t1", "Restart a service", "sudo systemctl restart {serviceName}");
        source.Category = "services-automation";
        source.Translations =
        [
            new ActionTranslation
            {
                CultureCode = "fr",
                Title = "Redémarrer un service",
                LinuxParameters = new() { ["serviceName"] = new ParameterTranslation("Nom du service", null) }
            }
        ];

        var path = Path.GetTempFileName();
        try
        {
            await service.ExportAsync(new FakeActionService([source]), path);

            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                var exported = document.RootElement.GetProperty("actions")[0];
                Assert.Equal("Restart a service", exported.GetProperty("title").GetString());
                Assert.Equal(
                    "Redémarrer un service",
                    exported.GetProperty("translations").GetProperty("fr").GetProperty("title").GetString());
            }

            var target = new FakeActionService([]);
            var result = await service.ImportAsync(target, path);

            Assert.Equal(1, result.Imported);
            var imported = Assert.Single(await target.GetAllActionsAsync());
            Assert.Equal("Restart a service", imported.Title);
            var french = Assert.Single(imported.Translations);
            Assert.Equal("fr", french.CultureCode);
            Assert.Equal("Nom du service", french.LinuxParameters["serviceName"].Label);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
