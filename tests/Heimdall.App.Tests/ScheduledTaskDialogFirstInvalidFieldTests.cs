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

namespace Heimdall.App.Tests;

/// <summary>
/// A refused Save names the field to fix, so the dialog can move the focus there instead of
/// leaving a keyboard user on the Save button that has just said no.
/// </summary>
public sealed class ScheduledTaskDialogFirstInvalidFieldTests
{
    private static readonly ServerOption Server = new("srv-1", "Alpha", "Alpha (SSH)", "SSH");

    [Fact]
    public void NoServerSelected_NamesTheServerField()
    {
        var viewModel = new ScheduledTaskDialogViewModel { SelectedServer = null };

        viewModel.ValidateCommand.Execute(null);

        Assert.NotNull(viewModel.ValidationError);
        Assert.Equal(ScheduledTaskField.Server, viewModel.FirstInvalidField);
    }

    [Fact]
    public void DailyScheduleWithAnInvalidTime_NamesTheTimeField()
    {
        var viewModel = new ScheduledTaskDialogViewModel
        {
            SelectedServer = Server,
            ScheduleType = "Daily",
            TimeOfDay = "25:99"
        };

        viewModel.ValidateCommand.Execute(null);

        Assert.NotNull(viewModel.ValidationError);
        Assert.Equal(ScheduledTaskField.TimeOfDay, viewModel.FirstInvalidField);
    }

    [Fact]
    public void IntervalScheduleWithAnInvalidInterval_NamesTheIntervalField()
    {
        var viewModel = new ScheduledTaskDialogViewModel
        {
            SelectedServer = Server,
            ScheduleType = "Interval",
            IntervalMinutes = "0"
        };

        viewModel.ValidateCommand.Execute(null);

        Assert.NotNull(viewModel.ValidationError);
        Assert.Equal(ScheduledTaskField.Interval, viewModel.FirstInvalidField);
    }

    [Fact]
    public void AValidForm_NamesNoField_AndClearsTheOneBefore()
    {
        var viewModel = new ScheduledTaskDialogViewModel { SelectedServer = null };
        viewModel.ValidateCommand.Execute(null);
        Assert.NotNull(viewModel.FirstInvalidField);

        viewModel.SelectedServer = Server;
        viewModel.ValidateCommand.Execute(null);

        Assert.Null(viewModel.ValidationError);
        Assert.Null(viewModel.FirstInvalidField);
    }
}
