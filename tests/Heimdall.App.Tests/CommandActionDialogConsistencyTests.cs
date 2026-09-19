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
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins what the Add and Edit dialog says when a command pattern and its declared
/// parameters disagree.
/// </summary>
/// <remarks>
/// Two properties matter beyond the wording, and each has a way of being quietly lost.
/// The warning has to be live, because it does not block the save and a dialog that only
/// produced it on Save would close in the same gesture. And it has to stay out of
/// <c>ValidationError</c>, because that field refuses the save and half of what this
/// reports is a reading of intent rather than a fact.
/// </remarks>
public sealed class CommandActionDialogConsistencyTests
{
    [Fact]
    public async Task APlaceholderNobodyDeclaredIsReported()
    {
        var vm = await CreateAsync();

        vm.LinuxPattern = "ssh -p {port} {host}";

        Assert.True(vm.HasConsistencyWarning);
        Assert.Contains("port", vm.ConsistencyWarning!, StringComparison.Ordinal);
        Assert.Contains("host", vm.ConsistencyWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AParameterThePatternNeverMentionsIsReported()
    {
        var vm = await CreateAsync();
        vm.LinuxPattern = "tail -f {path}";
        vm.LinuxParameters.Add(new ParameterEntryVm { Name = "path" });

        vm.LinuxParameters.Add(new ParameterEntryVm { Name = "lines" });

        Assert.True(vm.HasConsistencyWarning);
        Assert.Contains("lines", vm.ConsistencyWarning!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APatternThatAgreesWithItsParametersSaysNothing()
    {
        var vm = await CreateAsync();

        vm.LinuxPattern = "tail -n {lines} {path}";
        vm.LinuxParameters.Add(new ParameterEntryVm { Name = "path" });
        vm.LinuxParameters.Add(new ParameterEntryVm { Name = "lines" });

        Assert.False(vm.HasConsistencyWarning);
        Assert.Null(vm.ConsistencyWarning);
    }

    /// <summary>
    /// The warning has to follow the work as it is typed. Declaring the missing parameter
    /// must clear it without the operator doing anything else.
    /// </summary>
    [Fact]
    public async Task DeclaringTheMissingParameterClearsTheWarning()
    {
        var vm = await CreateAsync();
        vm.LinuxPattern = "tail -f {path}";
        Assert.True(vm.HasConsistencyWarning);

        vm.LinuxParameters.Add(new ParameterEntryVm { Name = "path" });

        Assert.False(vm.HasConsistencyWarning);
    }

    /// <summary>
    /// Renaming a parameter is the other half of "live": the list did not change, one of
    /// its entries did.
    /// </summary>
    [Fact]
    public async Task RenamingAParameterUpdatesTheWarning()
    {
        var vm = await CreateAsync();
        vm.LinuxPattern = "tail -f {path}";
        var parameter = new ParameterEntryVm { Name = "path" };
        vm.LinuxParameters.Add(parameter);
        Assert.False(vm.HasConsistencyWarning);

        parameter.Name = "pathname";

        Assert.True(vm.HasConsistencyWarning);
        Assert.Contains("path", vm.ConsistencyWarning!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing a parameter has to stop its entry being watched.
    /// </summary>
    /// <remarks>
    /// Stated against the recompute count, not against the text. Measured: the recomputed
    /// warning is identical whether or not the entry is still subscribed, because the
    /// check reads the list the entry has already left - so a test written against the
    /// text passes with the unsubscription deleted, and says nothing.
    /// </remarks>
    [Fact]
    public async Task ARemovedParameterStopsAffectingTheWarning()
    {
        var vm = await CreateAsync();
        vm.LinuxPattern = "tail -f {path}";
        var parameter = new ParameterEntryVm { Name = "path" };
        vm.LinuxParameters.Add(parameter);
        vm.LinuxParameters.Remove(parameter);

        var before = vm.ConsistencyRecomputeCount;
        parameter.Name = "something else entirely";

        Assert.Equal(before, vm.ConsistencyRecomputeCount);
        Assert.True(vm.HasConsistencyWarning);
    }

    /// <summary>
    /// Positive control for the count: a parameter still in the list must move it, or the
    /// test above would pass on a view model that never recomputes at all.
    /// </summary>
    [Fact]
    public async Task ADeclaredParameterStillMovesTheRecomputeCount()
    {
        var vm = await CreateAsync();
        vm.LinuxPattern = "tail -f {path}";
        var parameter = new ParameterEntryVm { Name = "path" };
        vm.LinuxParameters.Add(parameter);

        var before = vm.ConsistencyRecomputeCount;
        parameter.Name = "pathname";

        Assert.True(vm.ConsistencyRecomputeCount > before);
    }

    /// <summary>
    /// The whole reason this is a warning: it must never stop somebody saving.
    /// </summary>
    [Fact]
    public async Task TheWarningDoesNotBlockSaving()
    {
        var vm = await CreateAsync();
        vm.Title = "Run something";
        vm.Category = "Ops";
        vm.LinuxPattern = "ssh -p {port} {host}";

        vm.ValidateCommand.Execute(null);

        Assert.True(vm.HasConsistencyWarning);
        Assert.Null(vm.ValidationError);
    }

    /// <summary>
    /// A pattern full of ordinary shell braces must stay silent, or the check cries wolf
    /// on exactly the commands worth storing and gets ignored.
    /// </summary>
    [Fact]
    public async Task OrdinaryShellBracesSayNothing()
    {
        var vm = await CreateAsync();

        vm.LinuxPattern = "awk '{print $1}' \"${LOGFILE}\" | jq '{n: .name}'";

        Assert.False(vm.HasConsistencyWarning);
    }

    /// <summary>
    /// A parameter declared for one platform says nothing about the other's pattern, so
    /// each template is reported separately and named when both exist.
    /// </summary>
    [Fact]
    public async Task EachTemplateIsReportedAndNamedWhenBothExist()
    {
        var vm = await CreateAsync();

        vm.WindowsPattern = "Get-Content {winpath}";
        vm.LinuxPattern = "cat {linpath}";

        Assert.True(vm.HasConsistencyWarning);
        Assert.Contains("winpath", vm.ConsistencyWarning!, StringComparison.Ordinal);
        Assert.Contains("linpath", vm.ConsistencyWarning!, StringComparison.Ordinal);
        Assert.Contains("Windows", vm.ConsistencyWarning!, StringComparison.Ordinal);
        Assert.Contains("Linux", vm.ConsistencyWarning!, StringComparison.Ordinal);
    }

    /// <summary>
    /// With only one pattern there is nothing to disambiguate, so the platform name would
    /// be noise.
    /// </summary>
    [Fact]
    public async Task ASingleTemplateIsNotPrefixedWithItsPlatform()
    {
        var vm = await CreateAsync();

        vm.LinuxPattern = "cat {linpath}";

        Assert.True(vm.HasConsistencyWarning);
        Assert.DoesNotContain("Linux:", vm.ConsistencyWarning!, StringComparison.Ordinal);
    }

    private static async Task<CommandActionDialogViewModel> CreateAsync()
    {
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        return new CommandActionDialogViewModel { Localizer = localizer };
    }
}
