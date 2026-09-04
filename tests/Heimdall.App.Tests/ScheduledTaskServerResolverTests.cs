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

using Heimdall.App.ViewModels.Scheduled;

namespace Heimdall.App.Tests;

/// <summary>
/// Which profile a scheduled task runs against when its identifier no longer resolves.
/// </summary>
/// <remarks>
/// The defect this pins is not a crash and not a refusal: it is a scheduled task opening a session
/// on the WRONG machine, unattended, because the fallback matched a display name and display names
/// are not unique. Any dangling reference reached it - a deleted profile, one replaced by an
/// import, one re-identified by a migration.
/// </remarks>
public sealed class ScheduledTaskServerResolverTests
{
    private sealed record Server(string Id, string DisplayName);

    private static Server? Resolve(string? taskId, string? taskName, params Server[] servers) =>
        ScheduledTaskServerResolver.Resolve(
            taskId,
            taskName,
            servers,
            s => s.Id,
            s => s.DisplayName);

    [Fact]
    public void ATaskIsAnsweredByTheIdentifierItRecorded()
    {
        Server? resolved = Resolve(
            "id-a",
            "Production",
            new Server("id-b", "Production"),
            new Server("id-a", "Production"));

        Assert.Equal("id-a", resolved?.Id);
    }

    // The defect, stated as the test that would have caught it. Two profiles called "Production",
    // one of them the machine the task means; its identifier no longer resolves because the
    // profile was deleted or re-identified. The fallback took the first same-named profile and
    // connected to it - the wrong machine, unattended.
    [Fact]
    public void ATaskWhoseIdentifierIsGoneAndWhoseNameIsAmbiguousConnectsToNothing()
    {
        Server? resolved = Resolve(
            "id-gone",
            "Production",
            new Server("id-other", "Production"),
            new Server("id-another", "Production"));

        Assert.Null(resolved);
    }

    // And the correction to the correction. Refusing the fallback outright breaks the ordinary
    // case - a profile deleted and re-created, or re-identified by a migration - where exactly one
    // profile carries the name and there is no ambiguity to be wrong about. Worse than breaking
    // it: the scheduler stamps LastRun before calling this, so the grid would show the task ran
    // while nothing connected.
    [Fact]
    public void ATaskWhoseIdentifierIsGoneStillFindsAnUnambiguousName()
    {
        Server? resolved = Resolve(
            "id-gone",
            "PROD-DC01",
            new Server("id-a", "Lab"),
            new Server("id-b", "PROD-DC01"));

        Assert.Equal("id-b", resolved?.Id);
    }

    // The control that keeps the assertion above from being "the resolver always returns null":
    // the same inventory, the same names, and an identifier that DOES resolve.
    [Fact]
    public void TheSameInventoryStillAnswersATaskWhoseIdentifierResolves()
    {
        Server? resolved = Resolve(
            "id-another",
            "Production",
            new Server("id-other", "Production"),
            new Server("id-another", "Production"));

        Assert.Equal("id-another", resolved?.Id);
    }

    // Kept for a task file old enough to carry no identifier at all: there the name is the only
    // thing there is, and refusing it would break every such task rather than protect anything.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ATaskWithNoIdentifierStillFallsBackToTheDisplayName(string? taskId)
    {
        Server? resolved = Resolve(
            taskId,
            "production",
            new Server("id-a", "Lab"),
            new Server("id-b", "Production"));

        Assert.Equal("id-b", resolved?.Id);
    }

    // The same rule for a task that names no identifier: ambiguity refuses there too.
    [Fact]
    public void ATaskWithNoIdentifierAndAnAmbiguousNameConnectsToNothing()
        => Assert.Null(Resolve(
            null,
            "Production",
            new Server("id-a", "Production"),
            new Server("id-b", "Production")));

    // Case-sensitive on the identifier, like every other identifier comparison on this path. The
    // single same-named profile is then found by the unambiguous-name rule, which is the intended
    // outcome - what must not happen is the identifier matching loosely.
    [Fact]
    public void AnIdentifierIsMatchedExactlyAndDoesNotResolveByCase()
    {
        Server? resolved = Resolve(
            "ID-A",
            "Production",
            new Server("id-a", "Production"),
            new Server("id-b", "Production"));

        Assert.Null(resolved);
    }
}
