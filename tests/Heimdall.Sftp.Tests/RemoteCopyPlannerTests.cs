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

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Guards the remote-copy containment rule that outlived the client-side copy planner.
/// </summary>
/// <remarks>
/// These oracles used to run against the planner's tree walk. That walk is gone: neither transport
/// can publish a file without risking an existing destination, so FTP refuses and SFTP requires its
/// server-side exclusive reservation. The containment rule was enforced only inside the walk, which a
/// successful server-side copy already bypassed, so it is now applied by the caller before any command
/// runs and is exercised here directly.
/// </remarks>
public sealed class RemoteCopyPathGuardTests
{
    [Theory]
    // Destination is the source.
    [InlineData("/srv/data", "/srv/data")]
    // Trailing separators must not change the verdict on either side.
    [InlineData("/srv/data/", "/srv/data")]
    [InlineData("/srv/data", "/srv/data/")]
    // Destination sits inside the source, at any depth.
    [InlineData("/srv/data", "/srv/data/sub")]
    [InlineData("/srv/data", "/srv/data/sub/deeper")]
    [InlineData("/srv/data/", "/srv/data/sub")]
    // The remote root contains every destination.
    [InlineData("/", "/srv/data")]
    // Collapsed before comparing: a non-canonical spelling of the source is still the source.
    [InlineData("/srv/./data", "/srv/data/sub")]
    [InlineData("/srv//data", "/srv/data/sub/x")]
    [InlineData("/srv/data", "/srv/data/sub/../other")]
    public void IsSameOrDescendantPath_SameOrInside_IsRefused(string source, string destination)
    {
        Assert.True(RemoteCopyPathGuard.IsSameOrDescendantPath(source, destination));
    }

    [Theory]
    // A sibling that merely shares a textual prefix is NOT inside the source. Without the trailing
    // separator in the comparison this pair would be wrongly refused, so it is the discriminating
    // case for the guard's implementation.
    [InlineData("/srv/data", "/srv/database")]
    [InlineData("/srv/data", "/srv/data-backup")]
    [InlineData("/srv/data/", "/srv/database")]
    // Unrelated trees, and a destination that is an ancestor rather than a descendant.
    [InlineData("/srv/data", "/srv/other")]
    [InlineData("/srv/data/sub", "/srv/data")]
    [InlineData("/srv/data/sub", "/srv/data/other")]
    // A destination that merely spells a way out of the source is outside it.
    [InlineData("/srv/data", "/srv/data/../elsewhere")]
    [InlineData("/srv/data", "/srv/data/./../data2")]
    public void IsSameOrDescendantPath_OutsideTheSource_IsAllowed(string source, string destination)
    {
        Assert.False(RemoteCopyPathGuard.IsSameOrDescendantPath(source, destination));
    }
}
