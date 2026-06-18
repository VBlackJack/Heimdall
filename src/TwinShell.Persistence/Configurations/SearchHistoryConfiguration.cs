/*
 * Copyright 2025 Julien Bombled
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

public sealed class SearchHistoryConfiguration : IEntityTypeConfiguration<SearchHistoryEntity>
{
    public void Configure(EntityTypeBuilder<SearchHistoryEntity> builder)
    {
        builder.ToTable("SearchHistories");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.SearchTerm)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(e => e.NormalizedSearchTerm)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(e => e.SearchCount)
            .IsRequired();

        builder.Property(e => e.ResultCount)
            .IsRequired();

        builder.Property(e => e.LastSearchedAt)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.WasSuccessful)
            .IsRequired();

        builder.Property(e => e.UserId)
            .HasMaxLength(100);

        // Indexes for common queries
        builder.HasIndex(e => e.LastSearchedAt)
            .HasDatabaseName("IX_SearchHistories_LastSearchedAt");

        builder.HasIndex(e => e.SearchCount)
            .HasDatabaseName("IX_SearchHistories_SearchCount");

        builder.HasIndex(e => e.NormalizedSearchTerm)
            .HasDatabaseName("IX_SearchHistories_NormalizedSearchTerm");

        builder.HasIndex(e => e.UserId)
            .HasDatabaseName("IX_SearchHistories_UserId");

        // Unique index for normalized search term per user (prevent duplicates)
        builder.HasIndex(e => new { e.NormalizedSearchTerm, e.UserId })
            .IsUnique()
            .HasDatabaseName("IX_SearchHistories_NormalizedSearchTerm_UserId");
    }
}
