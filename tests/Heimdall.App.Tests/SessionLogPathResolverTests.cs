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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Tests for <see cref="SessionLogPathResolver"/>: relative paths root under the writable base,
/// absolute paths pass through, and an empty configured value falls back to the settings default.
/// </summary>
public sealed class SessionLogPathResolverTests
{
    [Fact]
    public void Resolve_RelativeDirectory_RootsUnderWritableBase()
    {
        AppSettings settings = new AppSettings { SessionLogDirectory = @"logs\sessions" };
        string baseDir = @"C:\app";

        string resolved = SessionLogPathResolver.Resolve(settings, baseDir);

        resolved.Should().Be(Path.GetFullPath(Path.Combine(baseDir, @"logs\sessions")));
    }

    [Fact]
    public void Resolve_AbsoluteDirectory_PassesThroughUnchanged()
    {
        AppSettings settings = new AppSettings { SessionLogDirectory = @"D:\transcripts\heimdall" };
        string baseDir = @"C:\app";

        string resolved = SessionLogPathResolver.Resolve(settings, baseDir);

        resolved.Should().Be(Path.GetFullPath(@"D:\transcripts\heimdall"));
    }

    [Fact]
    public void Resolve_EmptyDirectory_FallsBackToSettingsDefaultUnderBase()
    {
        AppSettings settings = new AppSettings { SessionLogDirectory = "   " };
        string baseDir = @"C:\app";
        string expectedDefault = new AppSettings().SessionLogDirectory;

        string resolved = SessionLogPathResolver.Resolve(settings, baseDir);

        resolved.Should().Be(Path.GetFullPath(Path.Combine(baseDir, expectedDefault)));
    }

    [Fact]
    public void Resolve_NullSettings_Throws()
    {
        Action act = () => SessionLogPathResolver.Resolve(null!, @"C:\app");

        act.Should().Throw<ArgumentNullException>();
    }
}
