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

using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Session;

namespace Heimdall.App.Tests;

public sealed class ReconnectChainFailureReportTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(4, 5)]
    public void ReportForAttempt_BeforeTheLastAttempt_StaysSilent(int attempt, int maxAttempts)
        => Assert.Equal(ConnectFailureReport.Silent, SessionCoordinator.ReportForAttempt(attempt, maxAttempts));

    [Theory]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    public void ReportForAttempt_TheLastAttempt_LeavesAFailedTabButNoDialog(int attempt, int maxAttempts)
        => Assert.Equal(ConnectFailureReport.FailedTabOnly, SessionCoordinator.ReportForAttempt(attempt, maxAttempts));
}
