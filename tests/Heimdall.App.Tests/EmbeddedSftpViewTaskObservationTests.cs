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

using Heimdall.App.Views;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSftpViewTaskObservationTests
{
    [Fact]
    public async Task ObserveFaultedTask_LogsFaultWithoutThrowing()
    {
        List<string> warnings = [];
        var fault = new InvalidOperationException("dialog setup failed");

        Task observer = EmbeddedSftpView.ObserveFaultedTask(
            Task.FromException(fault),
            "test prologue",
            warnings.Add);

        await observer;

        string warning = Assert.Single(warnings);
        Assert.Contains("test prologue", warning, StringComparison.Ordinal);
        Assert.Contains(fault.Message, warning, StringComparison.Ordinal);
    }
}
