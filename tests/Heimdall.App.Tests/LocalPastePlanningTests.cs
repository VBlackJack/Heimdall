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
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// The paste's planning walk, extracted so it runs off the UI thread and so a root that is a
/// link is refused rather than copied through.
/// </summary>
public sealed class LocalPastePlanningTests
{
    [Fact]
    public void PlanPasteRoots_RegularTree_IsPlanned()
    {
        using TempTree tree = new();
        string source = tree.CreateDirectory("proj");
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");

        LocalPastePlan plan = LocalFileBrowserViewModel.PlanPasteRoots([source], tree.CreateDirectory("dst"), File.GetAttributes);

        (string sourcePath, IReadOnlyList<Heimdall.App.Services.LocalPasteOp> operations) = Assert.Single(plan.Roots);
        Assert.Equal(source, sourcePath);
        Assert.Equal(2, operations.Count);
        Assert.Empty(plan.RefusedLinks);
        Assert.Empty(plan.RefusedSelfTargets);
        Assert.Empty(plan.Errors);
    }

    /// <remarks>
    /// A junction at the root was traversed to wherever it pointed, and a file link at the root
    /// was copied by value. A root link is refused by name, so the user pastes the target instead.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlanPasteRoots_RootReparsePoint_IsRefusedByName(bool directory)
    {
        using TempTree tree = new();
        string source = directory
            ? tree.CreateDirectory("linked")
            : tree.CreateFile("linked.txt");

        LocalPastePlan plan = LocalFileBrowserViewModel.PlanPasteRoots(
            [source],
            tree.CreateDirectory("dst"),
            path => File.GetAttributes(path) | FileAttributes.ReparsePoint);

        Assert.Empty(plan.Roots);
        Assert.Equal([Path.GetFileName(source)], plan.RefusedLinks);
    }

    [Fact]
    public void PlanPasteRoots_SelfTarget_IsRefusedByName()
    {
        using TempTree tree = new();
        string source = tree.CreateDirectory("proj");

        LocalPastePlan plan = LocalFileBrowserViewModel.PlanPasteRoots([source], Path.Combine(source, "inner"), File.GetAttributes);

        Assert.Empty(plan.Roots);
        Assert.Equal(["proj"], plan.RefusedSelfTargets);
    }

    private sealed class TempTree : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"Heimdall-paste-{Guid.NewGuid():N}");

        public TempTree() => Directory.CreateDirectory(_root);

        public string CreateDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(string name)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, "x");
            return path;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
