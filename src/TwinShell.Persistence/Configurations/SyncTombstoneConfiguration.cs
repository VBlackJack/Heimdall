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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TwinShell.Persistence.Entities;

namespace TwinShell.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="SyncTombstoneEntity"/>.
/// </summary>
public sealed class SyncTombstoneConfiguration : IEntityTypeConfiguration<SyncTombstoneEntity>
{
    /// <summary>The table name, shared with the schema step that creates it.</summary>
    public const string TableName = "SyncTombstones";

    public void Configure(EntityTypeBuilder<SyncTombstoneEntity> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(e => e.PublicId);

        builder.Property(e => e.DeletedAt)
            .IsRequired();
    }
}
