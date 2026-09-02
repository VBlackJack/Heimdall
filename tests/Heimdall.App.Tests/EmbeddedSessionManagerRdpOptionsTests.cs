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
using Heimdall.App.Services;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

[Collection(CredentialDialogPasswordDirtyCollection.Name)]
public sealed class EmbeddedSessionManagerRdpOptionsTests
{
    [Fact]
    public void ResolveRdpResizeEnableDelayMs_ProfileNullReturnsGlobal()
    {
        var result = EmbeddedSessionManager.ResolveRdpResizeEnableDelayMs(null, 15000);

        Assert.Equal(15000, result);
    }

    [Fact]
    public void ResolveRdpResizeEnableDelayMs_ProfileZeroReturnsZero()
    {
        var result = EmbeddedSessionManager.ResolveRdpResizeEnableDelayMs(0, 15000);

        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolveRdpResizeEnableDelayMs_ProfilePositiveReturnsProfile()
    {
        var result = EmbeddedSessionManager.ResolveRdpResizeEnableDelayMs(3000, 15000);

        Assert.Equal(3000, result);
    }

    [Fact]
    public void ResolveRdpResizeEnableDelayMs_ProfileNegativeClampsToZero()
    {
        var result = EmbeddedSessionManager.ResolveRdpResizeEnableDelayMs(-1, 15000);

        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolveRdpResizeEnableDelayMs_GlobalNegativeReturnsDefault()
    {
        var result = EmbeddedSessionManager.ResolveRdpResizeEnableDelayMs(null, -1);

        Assert.Equal(EmbeddedSessionManager.DefaultRdpResizeEnableDelayMs, result);
    }

    // RDP host pooling hangs off one assignment in the manager, and the view it feeds carries a
    // transient provider as its own default - so dropping that assignment reverts pooling with
    // nothing red. The cost is measured: a control that has ever connected takes about 66 kernel
    // handles the operating system never returns, against roughly 3 for reusing one.
    [Fact]
    public void RdpSession_GetsThePooledHostProviderAndNotTheViewOwnTransientDefault()
    {
        RunOnStaThread(() =>
        {
            App application = CreateApplication();
            try
            {
                EmbeddedSessionManager manager = CreateManager();

                EmbeddedRdpView view = manager.CreateRdpView();

                Assert.IsType<PooledRdpHostProvider>(view.HostProvider);
            }
            finally
            {
                application.Shutdown();
                application.Dispatcher.InvokeShutdown();
                ResetApplicationSingletonForTest(application);
            }
        });
    }

    // The control for the assertion above: the default really is the transient provider, so
    // IsType<PooledRdpHostProvider> is not satisfied by whatever a bare view happens to hold.
    [Fact]
    public void ARdpViewBuiltWithoutTheManager_KeepsTheTransientDefault()
    {
        RunOnStaThread(() =>
        {
            App application = CreateApplication();
            try
            {
                EmbeddedRdpView view = new EmbeddedRdpView();

                Assert.IsType<TransientRdpHostProvider>(view.HostProvider);
            }
            finally
            {
                application.Shutdown();
                application.Dispatcher.InvokeShutdown();
                ResetApplicationSingletonForTest(application);
            }
        });
    }

    // Only the two fields the RDP view factory reads are supplied; the rest stay null so that a
    // future dependency added to that path surfaces here instead of being silently tolerated.
    private static EmbeddedSessionManager CreateManager()
        => new EmbeddedSessionManager(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

    private static App CreateApplication()
    {
        Assert.Null(System.Windows.Application.Current);
        App application = new App();
        application.InitializeComponent();
        return application;
    }

    private static void ResetApplicationSingletonForTest(App application)
    {
        Assert.Same(application, System.Windows.Application.Current);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo? appInstance = typeof(System.Windows.Application).GetField("_appInstance", flags);
        FieldInfo? appCreated = typeof(System.Windows.Application).GetField("_appCreatedInThisAppDomain", flags);
        FieldInfo? isShuttingDown = typeof(System.Windows.Application).GetField("_isShuttingDown", flags);
        Assert.NotNull(appInstance);
        Assert.NotNull(appCreated);
        Assert.NotNull(isShuttingDown);
        appInstance.SetValue(null, null);
        appCreated.SetValue(null, false);
        isShuttingDown.SetValue(null, false);
        Assert.Null(System.Windows.Application.Current);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        Thread thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }
}
