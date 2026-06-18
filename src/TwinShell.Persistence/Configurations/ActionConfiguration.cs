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
using TwinShell.Core.Constants;
using TwinShell.Persistence.Entities;

namespace TwinShell.Persistence.Configurations;

public sealed class ActionConfiguration : IEntityTypeConfiguration<ActionEntity>
{
    public void Configure(EntityTypeBuilder<ActionEntity> builder)
    {
        builder.ToTable("Actions");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .IsRequired()
            .HasMaxLength(50);

        // BUGFIX: Use centralized constants and fix length mismatches
        builder.Property(e => e.Title)
            .IsRequired()
            .HasMaxLength(ValidationConstants.MaxActionTitleLength); // 200

        builder.Property(e => e.Description)
            .IsRequired()
            .HasMaxLength(ValidationConstants.MaxActionDescriptionLength); // 2000

        builder.Property(e => e.Category)
            .IsRequired()
            .HasMaxLength(ValidationConstants.MaxActionCategoryLength); // 100

        builder.Property(e => e.Platform)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(e => e.Level)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(e => e.TagsJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(e => e.ExamplesJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(e => e.WindowsExamplesJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(e => e.LinuxExamplesJson)
            .IsRequired()
            .HasColumnType("TEXT");

        builder.Property(e => e.LinksJson)
            .IsRequired()
            .HasColumnType("TEXT");

        // BUGFIX: Increase from 2000 to 5000 to match UI/validation
        builder.Property(e => e.Notes)
            .HasMaxLength(ValidationConstants.MaxActionNotesLength); // 5000

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .IsRequired();

        builder.Property(e => e.IsUserCreated)
            .IsRequired();

        // Relationships
        builder.HasOne(e => e.WindowsCommandTemplate)
            .WithMany()
            .HasForeignKey(e => e.WindowsCommandTemplateId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.LinuxCommandTemplate)
            .WithMany()
            .HasForeignKey(e => e.LinuxCommandTemplateId)
            .OnDelete(DeleteBehavior.SetNull);

        // Indexes for common queries
        builder.HasIndex(e => e.Title)
            .HasDatabaseName("IX_Actions_Title");

        builder.HasIndex(e => e.Category)
            .HasDatabaseName("IX_Actions_Category");

        builder.HasIndex(e => e.Platform)
            .HasDatabaseName("IX_Actions_Platform");

        builder.HasIndex(e => e.Level)
            .HasDatabaseName("IX_Actions_Level");

        builder.HasIndex(e => e.IsUserCreated)
            .HasDatabaseName("IX_Actions_IsUserCreated");

        // PublicId for GitOps synchronization - must be unique
        builder.Property(e => e.PublicId)
            .IsRequired();

        builder.HasIndex(e => e.PublicId)
            .IsUnique()
            .HasDatabaseName("IX_Actions_PublicId");
    }
}
