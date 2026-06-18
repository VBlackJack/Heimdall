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

public sealed class CommandHistoryConfiguration : IEntityTypeConfiguration<CommandHistoryEntity>
{
    public void Configure(EntityTypeBuilder<CommandHistoryEntity> builder)
    {
        builder.ToTable("CommandHistories");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.UserId)
            .HasMaxLength(100);

        builder.Property(e => e.ActionId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.GeneratedCommand)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(e => e.ParametersJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(e => e.Platform)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.Category)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(e => e.ActionTitle)
            .IsRequired()
            .HasMaxLength(300);

        // Relationship with Action
        builder.HasOne(e => e.Action)
            .WithMany()
            .HasForeignKey(e => e.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for common queries
        builder.HasIndex(e => e.CreatedAt)
            .HasDatabaseName("IX_CommandHistories_CreatedAt");

        builder.HasIndex(e => e.ActionId)
            .HasDatabaseName("IX_CommandHistories_ActionId");

        builder.HasIndex(e => e.Category)
            .HasDatabaseName("IX_CommandHistories_Category");

        builder.HasIndex(e => e.Platform)
            .HasDatabaseName("IX_CommandHistories_Platform");

        builder.HasIndex(e => e.UserId)
            .HasDatabaseName("IX_CommandHistories_UserId");

        // PERFORMANCE: Index for text search on GeneratedCommand and ActionTitle
        // Used by SearchAsync with EF.Functions.Like
        builder.HasIndex(e => e.ActionTitle)
            .HasDatabaseName("IX_CommandHistories_ActionTitle");
    }
}
