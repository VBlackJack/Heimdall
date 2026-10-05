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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <remarks>
/// The WebView2 assembly is referenced by the application, not by this project, so the failure
/// kind is reached through the policy's own parameter type rather than named at compile time.
/// </remarks>
public sealed class TerminalSurfaceFailurePolicyTests
{
    private static readonly string[] FatalKinds =
    [
        "BrowserProcessExited",
        "RenderProcessExited",
        "RenderProcessUnresponsive",
    ];

    [Theory]
    [InlineData("BrowserProcessExited", true)]
    [InlineData("RenderProcessExited", true)]
    [InlineData("RenderProcessUnresponsive", true)]
    [InlineData("GpuProcessExited", false)]
    [InlineData("UtilityProcessExited", false)]
    [InlineData("SandboxHelperProcessExited", false)]
    [InlineData("UnknownProcessExited", false)]
    [InlineData("FrameRenderProcessExited", false)]
    public void OnlyTheBrowserOrThePageRenderer_TakesTheTerminalDown(string kindName, bool expected)
    {
        Assert.Equal(expected, IsFatal(Enum.Parse(KindType, kindName)));
    }

    [Fact]
    public void EveryKind_IsClassified()
    {
        foreach (object kind in Enum.GetValues(KindType))
        {
            Assert.Equal(FatalKinds.Contains(kind.ToString()), IsFatal(kind));
        }
    }

    private static MethodInfo Policy { get; } = typeof(TerminalSurfaceFailurePolicy).GetMethod(
        nameof(TerminalSurfaceFailurePolicy.IsFatal),
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static Type KindType => Policy.GetParameters()[0].ParameterType;

    private static bool IsFatal(object kind) => (bool)Policy.Invoke(null, [kind])!;
}
