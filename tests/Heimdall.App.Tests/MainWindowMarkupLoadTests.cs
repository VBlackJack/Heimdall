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

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows;

namespace Heimdall.App.Tests;

/// <summary>
/// MainWindow.xaml, read whole by the framework's own loader, parses.
/// </summary>
/// <remarks>
/// <para>
/// A nested <c>MultiBinding</c> in the Scheduled row name threw a <c>XamlParseException</c> the
/// moment the window was created, so the product never showed a window, and every blocking test
/// stayed green: none of them loaded the markup as a whole. Parsing is the step that fails on a
/// resource that does not resolve, a binding collection holding something it refuses, or a markup
/// extension that cannot be built, wherever in the 7,000 lines it sits.
/// </para>
/// <para>
/// The compiled markup is loaded into a window that has been given the base <c>Window</c>
/// construction and nothing else. The window's own constructor wants the whole service graph,
/// none of which has anything to do with whether the markup parses, and the loader that takes
/// only a URI cannot be used: it builds the root element itself, the window type has no
/// parameterless constructor, and it fails there on a perfectly healthy file before it has read
/// a single element of the markup. Loading into an instance reads all of it, applies the
/// resources of the application, and attaches the event handlers by name.
/// </para>
/// </remarks>
[Collection(CredentialDialogPasswordDirtyCollection.Name)]
public sealed class MainWindowMarkupLoadTests
{
    private const string MainWindowComponentUriFormat = "/{0};component/mainwindow.xaml";

    /// <summary>
    /// A control declared near the end of the first tab strip, so finding it proves the parser
    /// built the tree rather than returned having read nothing.
    /// </summary>
    private const string DeclaredControlName = "Mw_LockOverlayHelloUnlockBtn";

    [Fact]
    public void TheWholeOfMainWindowXamlParses()
    {
        RunOnStaThread(() =>
        {
            Assert.Null(Application.Current);
            App application = new();
            application.InitializeComponent();
            try
            {
                // The assembly is named Heimdall while the project is Heimdall.App, so the name is
                // read from the window's own assembly rather than spelled.
                Uri markup = new(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        MainWindowComponentUriFormat,
                        typeof(MainWindow).Assembly.GetName().Name),
                    UriKind.Relative);

                MainWindow window = WindowWithoutItsServices();

                Exception? failure = Record.Exception(() => Application.LoadComponent(window, markup));

                Assert.True(failure is null, "MainWindow.xaml does not parse: " + failure);
                Assert.NotNull(window.FindName(DeclaredControlName));
            }
            finally
            {
                ResetApplicationSingleton(application);
            }
        });
    }

    /// <summary>
    /// A <see cref="MainWindow"/> that has run the construction of <see cref="Window"/> and not
    /// its own, which is what loading markup into it needs.
    /// </summary>
    private static MainWindow WindowWithoutItsServices()
    {
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        ConstructorInfo baseConstruction = typeof(Window).GetConstructor(Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Window).FullName, ".ctor");
        baseConstruction.Invoke(window, null);
        return window;
    }

    private static void ResetApplicationSingleton(Application application)
    {
        Assert.Same(application, Application.Current);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo? appInstance = typeof(Application).GetField("_appInstance", flags);
        FieldInfo? appCreated = typeof(Application).GetField("_appCreatedInThisAppDomain", flags);
        FieldInfo? isShuttingDown = typeof(Application).GetField("_isShuttingDown", flags);
        Assert.NotNull(appInstance);
        Assert.NotNull(appCreated);
        Assert.NotNull(isShuttingDown);
        appInstance.SetValue(null, null);
        appCreated.SetValue(null, false);
        isShuttingDown.SetValue(null, false);
        Assert.Null(Application.Current);
    }

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }
}
