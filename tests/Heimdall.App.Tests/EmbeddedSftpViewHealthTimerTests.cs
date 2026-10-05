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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Once a disconnect has been reported, the SFTP health tick must not replace it with its own,
/// generic "no longer responding" line - a host-key warning included.
/// </summary>
/// <remarks>
/// A source reading: the view needs WPF and a live browser to be built.
/// </remarks>
public sealed class EmbeddedSftpViewHealthTimerTests
{
    private const string StopStatement = "StopHealthTimer();";

    [Theory]
    [InlineData("private void OnBrowserDisconnected(string? errorMessage)")]
    [InlineData("private async void OnDisconnectClick(object sender, RoutedEventArgs e)")]
    public void AReportedDisconnect_StopsTheHealthTimer(string handler)
    {
        string logic = ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(ReadSource()), handler);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, StopStatement),
            $"{handler} does not stop the health timer.");
    }

    private static string ReadSource()
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedSftpView.xaml.cs");
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
