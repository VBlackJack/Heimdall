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

using System.Reflection;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Shell;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins how the main view model is composed: what it may depend on, and what it may no longer reach.
/// </summary>
/// <remarks>
/// These are read from the types themselves rather than from source text, so a rename or a reformat
/// cannot make them pass or fail for the wrong reason.
/// </remarks>
public sealed class MainViewModelCompositionTests
{
    private static ConstructorInfo MainViewModelConstructor =>
        typeof(MainViewModel).GetConstructors().Single();

    // The service provider was reached at the point of use, which declares a collaborator nowhere and
    // hides it from every test double.
    [Fact]
    public void MainViewModel_TakesNoServiceProvider()
    {
        Assert.DoesNotContain(
            MainViewModelConstructor.GetParameters(),
            parameter => typeof(IServiceProvider).IsAssignableFrom(parameter.ParameterType));

        Assert.DoesNotContain(
            typeof(MainViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(IServiceProvider).IsAssignableFrom(field.FieldType));
    }

    // The palette is built through the factory, which is what breaks the ownership cycle.
    [Fact]
    public void MainViewModel_TakesThePaletteFactory()
    {
        Assert.Contains(
            MainViewModelConstructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(ICommandPaletteViewModelFactory));
    }

    // A measured count, not a claimed one. It fails if a dependency creeps back in.
    [Fact]
    public void MainViewModel_ConstructorTakesTheMeasuredNumberOfDependencies()
    {
        Assert.Equal(23, MainViewModelConstructor.GetParameters().Length);
    }

    // The palette resolves a scoped service per operation. Handing it the root provider is what makes
    // that resolution wrong; a scope factory is the dependency that says so in the type system.
    [Fact]
    public void CommandPaletteViewModel_TakesAScopeFactoryRatherThanTheRootProvider()
    {
        ParameterInfo[] parameters = typeof(CommandPaletteViewModel)
            .GetConstructors()
            .Single()
            .GetParameters();

        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(IServiceScopeFactory));
        Assert.DoesNotContain(
            parameters,
            parameter => typeof(IServiceProvider).IsAssignableFrom(parameter.ParameterType));
    }

    // The tunnelling collaborators existed on the shell's constructor only to be handed to the tunnels
    // view model. The factory owns them now, so the shell must no longer name them at all.
    [Fact]
    public void MainViewModel_TakesTheTunnelsFactoryAndNotItsCollaborators()
    {
        ParameterInfo[] parameters = MainViewModelConstructor.GetParameters();

        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(ITunnelsViewModelFactory));

        foreach (Type moved in TunnellingCollaborators)
        {
            Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == moved);
            Assert.DoesNotContain(
                typeof(MainViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
                field => field.FieldType == moved);
        }
    }

    // The snapshot service sat on the shell for one reason: the shell ran the restore sequence
    // itself. The coordinator owns that sequence now, so the shell must no longer be able to read
    // or delete the snapshot file at all.
    [Fact]
    public void MainViewModel_TakesTheRestoreCoordinatorAndNotTheSnapshotService()
    {
        ParameterInfo[] parameters = MainViewModelConstructor.GetParameters();

        Assert.Contains(
            parameters,
            parameter => parameter.ParameterType == typeof(ISessionRestoreCoordinator));

        Assert.DoesNotContain(
            parameters,
            parameter => parameter.ParameterType == typeof(ISessionSnapshotService));
        Assert.DoesNotContain(
            typeof(MainViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(ISessionSnapshotService));
    }

    private static Type[] TunnellingCollaborators =>
    [
        typeof(Heimdall.Ssh.TunnelManager),
        typeof(Heimdall.Core.StateMachine.ConnectionStateMachine),
        typeof(Heimdall.Core.Ssh.IHostKeyVerifier),
    ];

}
