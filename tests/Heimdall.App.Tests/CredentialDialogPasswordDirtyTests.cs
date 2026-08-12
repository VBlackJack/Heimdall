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
using System.Windows.Controls;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;

namespace Heimdall.App.Tests;

[Collection(CredentialDialogPasswordDirtyCollection.Name)]
public sealed class CredentialDialogPasswordDirtyTests
{
    private const string TestPassword = "CredentialDirtyProbe";

    private static readonly string[] ServerPasswordBoxNames =
    [
        "RdpPasswordBox",
        "WinRmPasswordBox",
        "SshPasswordBox",
        "SshKeyPassphraseBox",
        "VncPasswordBox",
        "FtpPasswordBox"
    ];

    private static readonly string[] GatewayPasswordBoxNames =
    [
        "PasswordBox",
        "KeyPassphraseBox"
    ];

    [Fact]
    public void CredentialPasswordBoxes_ArmDirtyGuard_AndProgrammaticClearDoesNotArmIt()
    {
        RunOnStaThread(() =>
        {
            App application = CreateApplication();
            try
            {
                ServerDialog serverDialog = new()
                {
                    DataContext = new ServerDialogViewModel()
                };
                GatewayDialog gatewayDialog = new()
                {
                    DataContext = new GatewayDialogViewModel()
                };

                AssertDialogPasswordBoxes(serverDialog, ServerPasswordBoxNames);
                AssertDialogPasswordBoxes(gatewayDialog, GatewayPasswordBoxNames);
                AssertAcceptedGatewaySaveClearsCredentialsWithoutVeto();
            }
            finally
            {
                application.Shutdown();
            }
        });
    }

    private static App CreateApplication()
    {
        Assert.Null(Application.Current);
        App application = new();
        application.InitializeComponent();
        return application;
    }

    private static void AssertDialogPasswordBoxes(Window dialog, IEnumerable<string> passwordBoxNames)
    {
        SetIsDirty(dialog, false);
        Assert.False(ReadIsDirty(dialog));

        foreach (string passwordBoxName in passwordBoxNames)
        {
            PasswordBox passwordBox = Assert.IsType<PasswordBox>(dialog.FindName(passwordBoxName));
            int passwordChangedCount = 0;
            passwordBox.PasswordChanged += (_, _) => passwordChangedCount++;

            passwordBox.Password = TestPassword;

            Assert.Equal(1, passwordChangedCount);
            Assert.True(ReadIsDirty(dialog));

            SetIsDirty(dialog, false);

            InvokeClearCredentialInputs(dialog);

            Assert.Empty(passwordBox.Password);
            Assert.False(ReadIsDirty(dialog));
        }
    }

    private static bool ReadIsDirty(Window dialog)
    {
        return dialog.DataContext switch
        {
            ServerDialogViewModel serverViewModel => serverViewModel.IsDirty,
            GatewayDialogViewModel gatewayViewModel => gatewayViewModel.IsDirty,
            _ => throw new InvalidOperationException("Unsupported credential dialog view model.")
        };
    }

    private static void AssertAcceptedGatewaySaveClearsCredentialsWithoutVeto()
    {
        GatewayDialogViewModel viewModel = new()
        {
            Name = "Gateway",
            Host = "gateway.example.test",
            User = "user"
        };
        GatewayDialog dialog = new()
        {
            DataContext = viewModel
        };

        dialog.Loaded += (_, _) =>
        {
            PasswordBox passwordBox = Assert.IsType<PasswordBox>(dialog.FindName("PasswordBox"));
            passwordBox.Password = TestPassword;
            Assert.True(viewModel.IsDirty);

            Button saveButton = Assert.IsType<Button>(dialog.FindName("SaveBtn"));
            saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };

        bool? result = dialog.ShowDialog();

        Assert.True(result);
        Assert.Empty(Assert.IsType<PasswordBox>(dialog.FindName("PasswordBox")).Password);
        Assert.Empty(Assert.IsType<PasswordBox>(dialog.FindName("KeyPassphraseBox")).Password);
    }

    private static void SetIsDirty(Window dialog, bool isDirty)
    {
        switch (dialog.DataContext)
        {
            case ServerDialogViewModel serverViewModel:
                serverViewModel.IsDirty = isDirty;
                break;

            case GatewayDialogViewModel gatewayViewModel:
                gatewayViewModel.IsDirty = isDirty;
                break;

            default:
                throw new InvalidOperationException("Unsupported credential dialog view model.");
        }
    }

    private static void InvokeClearCredentialInputs(Window dialog)
    {
        MethodInfo? method = dialog.GetType().GetMethod(
            "ClearCredentialInputs",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(dialog, null);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CredentialDialogPasswordDirtyCollection
{
    public const string Name = "CredentialDialogPasswordDirty";
}
