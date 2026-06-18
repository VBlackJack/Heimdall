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

public sealed class CommandBatchConfiguration : IEntityTypeConfiguration<CommandBatchEntity>
{
    public void Configure(EntityTypeBuilder<CommandBatchEntity> builder)
    {
        builder.ToTable("CommandBatches");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Description)
            .HasMaxLength(1000);

        builder.Property(e => e.ExecutionMode)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(e => e.CommandsJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(e => e.TagsJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .IsRequired();

        builder.Property(e => e.LastExecutedAt);

        builder.Property(e => e.IsUserCreated)
            .IsRequired();

        // Indexes for common queries
        builder.HasIndex(e => e.Name);
        builder.HasIndex(e => e.CreatedAt);
        builder.HasIndex(e => e.LastExecutedAt);

        // PublicId for GitOps synchronization - must be unique
        builder.Property(e => e.PublicId)
            .IsRequired();

        builder.HasIndex(e => e.PublicId)
            .IsUnique()
            .HasDatabaseName("IX_CommandBatches_PublicId");
    }
}
