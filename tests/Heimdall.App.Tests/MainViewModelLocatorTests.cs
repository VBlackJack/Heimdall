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
using System.Runtime.ExceptionServices;
using System.Windows;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The locator answers null for a cleared application singleton and for an application with no
/// main window, instead of throwing. Both are states the shutdown path runs in.
/// </summary>
/// <remarks>
/// The second case creates a real <see cref="Application"/>, which is a process-wide singleton, so
/// the class sits in the collection that serialises every test touching it and resets the
/// singleton the way those tests do.
/// </remarks>
[Collection(CredentialDialogPasswordDirtyCollection.Name)]
public sealed class MainViewModelLocatorTests
{
    [Fact]
    public void Find_NoApplication_ReturnsNull()
    {
        Assert.Null(MainViewModelLocator.Find(null));
    }

    [Fact]
    public void Find_ApplicationWithoutMainWindow_ReturnsNull()
    {
        RunOnStaThread(() =>
        {
            Assert.Null(Application.Current);
            Application application = new();
            try
            {
                Assert.Null(application.MainWindow);
                Assert.Null(MainViewModelLocator.Find(application));
            }
            finally
            {
                ResetApplicationSingleton(application);
            }
        });
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
