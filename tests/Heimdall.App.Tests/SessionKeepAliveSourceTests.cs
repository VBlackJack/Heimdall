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
/// Keep-alive bookkeeping that only a reading of the views can pin: both views need WPF and a
/// live remote session to be built.
/// </summary>
public sealed class SessionKeepAliveSourceTests
{
    [Fact]
    public void RdpAntiIdle_SkipsATickDuringAReconnectBounceInsteadOfStoppingForGood()
    {
        string logic = Logic("EmbeddedRdpView.xaml.cs", "private void OnAntiIdleTick(object? sender, EventArgs e)");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, "if (!_rdpHost.IsConnected)"),
            "A tick no longer tells a bounce apart from a dead view.");
        Assert.DoesNotContain("|| !_rdpHost.IsConnected)", logic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public void Dispose()")]
    [InlineData("private void OnDisconnectClick(object sender, RoutedEventArgs e)")]
    public void Vnc_ReleasesItsSleepPreventionWhenTheSessionEnds(string member)
    {
        string logic = Logic("EmbeddedVncView.xaml.cs", member);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, "ReleaseSleepPrevention();"),
            $"{member} keeps the machine awake after the VNC session ended.");
    }

    private static string Logic(string fileName, string member)
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", fileName);
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(full)), member);
    }
}
