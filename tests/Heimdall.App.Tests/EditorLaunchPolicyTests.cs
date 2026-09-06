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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The one editor policy for both browsers. The setting reached the local browser only, and the
/// shell-target refusal it applied had no counterpart on the remote side.
/// </summary>
public sealed class EditorLaunchPolicyTests
{
    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData(@"C:\scripts\open.ps1")]
    public void ResolveExternalEditor_ShellTarget_FallsBackToTheDefaultAndSaysWhy(string configured)
    {
        string editor = EditorLaunchPolicy.ResolveExternalEditor(configured, out string? rejectionKey);

        Assert.Equal(EditorLaunchPolicy.ShellTargetRejectionKey, rejectionKey);
        Assert.Equal(EditorLaunchPolicy.ResolveEditorPath(null), editor);
    }

    [Fact]
    public void ResolveExternalEditor_RegularEditor_IsReturnedExpanded()
    {
        string editor = EditorLaunchPolicy.ResolveExternalEditor(@"%windir%\notepad.exe", out string? rejectionKey);

        Assert.Null(rejectionKey);
        Assert.DoesNotContain("%windir%", editor, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\notepad.exe", editor, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveExternalEditor_NothingConfigured_IsTheDefaultEditor(string? configured)
    {
        string editor = EditorLaunchPolicy.ResolveExternalEditor(configured, out string? rejectionKey);

        Assert.Null(rejectionKey);
        Assert.EndsWith(@"\system32\notepad.exe", editor, StringComparison.OrdinalIgnoreCase);
    }
}
