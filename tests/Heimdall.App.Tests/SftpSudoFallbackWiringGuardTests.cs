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
using System.Reflection;
using System.Text.RegularExpressions;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The live mode of the SFTP root-fallback setting reaches every file pane: the dependency container
/// fills the session manager, the manager hands it to each view, and the view passes it to the view
/// model, which reads it at every act. A link missing from this chain leaves the pane Automatic, which
/// is today's behaviour and the one the setting exists to change.
/// </summary>
/// <remarks>
/// The constructor and the parameter lists are read by reflection. The two hand-overs that only exist
/// as statements (the object initializer in the manager, the argument in the view) are counted inside
/// helper members over source with comments and literals blanked; they establish that the statement
/// is written, never that it runs.
/// </remarks>
public sealed class SftpSudoFallbackWiringGuardTests
{
    private const string ProviderParameter = "sftpSudoFallbackProvider";
    private const string ViewProperty = "SftpSudoFallbackProvider";

    [Fact]
    public void TheSessionManager_TakesTheProviderAsItsLastOptionalParameter()
    {
        ConstructorInfo constructor = Assert.Single(typeof(EmbeddedSessionManager).GetConstructors());
        ParameterInfo last = constructor.GetParameters()[^1];

        Assert.Equal(ProviderParameter, last.Name);
        Assert.Equal(typeof(Func<SftpSudoFallbackMode>), last.ParameterType);
        Assert.True(last.HasDefaultValue);
        Assert.Null(last.DefaultValue);
    }

    [Fact]
    public void TheSessionManager_KeepsTheProviderItWasGiven()
    {
        Func<SftpSudoFallbackMode> provider = static () => SftpSudoFallbackMode.Never;

        EmbeddedSessionManager manager = new(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            AlwaysAvailableToolAvailability.Instance,
            sftpSudoFallbackProvider: provider);

        FieldInfo? field = typeof(EmbeddedSessionManager).GetField(
            "_sftpSudoFallbackProvider",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Same(provider, field!.GetValue(manager));
    }

    [Fact]
    public void TheViewModelInitializer_TakesTheProviderAsItsLastOptionalParameter()
    {
        MethodInfo initialize = typeof(EmbeddedSftpViewModel).GetMethod(nameof(EmbeddedSftpViewModel.Initialize))!;
        ParameterInfo last = initialize.GetParameters()[^1];

        Assert.Equal(ProviderParameter, last.Name);
        Assert.Equal(typeof(Func<SftpSudoFallbackMode>), last.ParameterType);
        Assert.True(last.HasDefaultValue);
        Assert.Null(last.DefaultValue);
    }

    [Fact]
    public void TheView_ExposesTheProviderTheManagerFills()
    {
        PropertyInfo? property = typeof(EmbeddedSftpView).GetProperty(ViewProperty);

        Assert.NotNull(property);
        Assert.Equal(typeof(Func<SftpSudoFallbackMode>), property!.PropertyType);
        Assert.NotNull(property.SetMethod);
    }

    [Fact]
    public void TheManagerHandsItsProviderToEveryFileViewItCreates()
    {
        // Each file view the manager builds is created in CreateSftpView, and that is the only place.
        Assert.Equal(1, NewViewCount(ManagerSource(), "EmbeddedSftpView"));
        Assert.Equal(1, InitializerAssignmentCount(ManagerSource(), "EmbeddedSftpView CreateSftpView", ViewProperty + " = _sftpSudoFallbackProvider"));
    }

    [Fact]
    public void TheViewPassesTheProviderToTheViewModelInitializer()
    {
        Assert.Equal(
            1,
            CallArgumentCount(ViewSourceText(), "InitializeSession", "_viewModel.Initialize(", ProviderParameter + ": " + ViewProperty));
    }

    [Fact]
    public void TheContainer_RegistersTheProviderTheManagerAsksFor()
    {
        Assert.Equal(1, RegistrationCount(AppSource(), "AddSingleton<Func<Heimdall.Core.Models.SftpSudoFallbackMode>>"));
    }

    [Fact]
    public void TheWiringReadings_SeeTheStatementsTheyCountAndOnlyThose()
    {
        const string Manager =
            "    private EmbeddedSftpView CreateSftpView()\n    {\n        var view = new EmbeddedSftpView\n        {\n            A = 1,\n            SftpSudoFallbackProvider = _sftpSudoFallbackProvider\n        };\n    }\n\n";
        const string ManagerWithout =
            "    private EmbeddedSftpView CreateSftpView()\n    {\n        var view = new EmbeddedSftpView\n        {\n            A = 1\n        };\n    }\n\n";
        const string Commented =
            "    private EmbeddedSftpView CreateSftpView()\n    {\n        // SftpSudoFallbackProvider = _sftpSudoFallbackProvider\n        var view = new EmbeddedSftpView { A = 1 };\n    }\n\n";
        const string ViewWith =
            "    public void InitializeSession()\n    {\n        _viewModel.Initialize(\n            a,\n            sftpSudoFallbackProvider: SftpSudoFallbackProvider);\n    }\n\n";
        const string ViewWithout =
            "    public void InitializeSession()\n    {\n        _viewModel.Initialize(\n            a);\n    }\n\n";
        const string ViewElsewhere =
            "    public void Other()\n    {\n        _viewModel.Initialize(a, sftpSudoFallbackProvider: SftpSudoFallbackProvider);\n    }\n\n    public void InitializeSession()\n    {\n        _viewModel.Initialize(a);\n    }\n\n";

        Assert.Equal(1, InitializerAssignmentCount(Manager, "EmbeddedSftpView CreateSftpView", ViewProperty + " = _sftpSudoFallbackProvider"));
        Assert.Equal(0, InitializerAssignmentCount(ManagerWithout, "EmbeddedSftpView CreateSftpView", ViewProperty + " = _sftpSudoFallbackProvider"));
        Assert.Equal(0, InitializerAssignmentCount(Commented, "EmbeddedSftpView CreateSftpView", ViewProperty + " = _sftpSudoFallbackProvider"));
        Assert.Equal(1, NewViewCount(Manager, "EmbeddedSftpView"));
        Assert.Equal(1, CallArgumentCount(ViewWith, "InitializeSession", "_viewModel.Initialize(", ProviderParameter + ": " + ViewProperty));
        Assert.Equal(0, CallArgumentCount(ViewWithout, "InitializeSession", "_viewModel.Initialize(", ProviderParameter + ": " + ViewProperty));
        Assert.Equal(0, CallArgumentCount(ViewElsewhere, "InitializeSession", "_viewModel.Initialize(", ProviderParameter + ": " + ViewProperty));
    }

    // Readings ---------------------------------------------------------------------------------

    private static int NewViewCount(string source, string type)
        => Regex.Matches(
            ViewSource.WithoutCommentsAndLiterals(source),
            @"\bnew\s+" + type + @"\b",
            RegexOptions.None,
            TimeSpan.FromSeconds(5)).Count;

    // How often an assignment is written inside the member's text, after blanking comments.
    private static int InitializerAssignmentCount(string source, string member, string assignment)
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(source);
        string body = ViewSource.HandlerBody(logic, " " + member + "(");
        return CountOf(body, assignment);
    }

    // How often an argument is written inside the argument list of a call, inside one member.
    private static int CallArgumentCount(string source, string member, string call, string argument)
    {
        string logic = ViewSource.WithoutCommentsAndLiterals(source);
        string body = ViewSource.HandlerBody(logic, " " + member + "(");
        int open = body.IndexOf(call, StringComparison.Ordinal);
        if (open < 0)
        {
            return 0;
        }

        int start = open + call.Length - 1;
        int depth = 0;
        for (int cursor = start; cursor < body.Length; cursor++)
        {
            if (body[cursor] == '(')
            {
                depth++;
            }
            else if (body[cursor] == ')' && --depth == 0)
            {
                return CountOf(body[start..cursor], argument);
            }
        }

        return 0;
    }

    private static int RegistrationCount(string source, string registration)
        => CountOf(ViewSource.WithoutCommentsAndLiterals(source), registration);

    private static int CountOf(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    // Sources --------------------------------------------------------------------------------------

    private static string ManagerSource()
        => File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Services", "EmbeddedSessionManager.cs"));

    private static string ViewSourceText()
        => File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedSftpView.xaml.cs"));

    private static string AppSource()
        => File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "App.xaml.cs"));
}
