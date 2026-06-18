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

/// <summary>
/// EF Core configuration for ActionTranslationEntity
/// </summary>
public sealed class ActionTranslationConfiguration : IEntityTypeConfiguration<ActionTranslationEntity>
{
    public void Configure(EntityTypeBuilder<ActionTranslationEntity> builder)
    {
        builder.ToTable("ActionTranslations");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.ActionId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.CultureCode)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(e => e.Title)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Description)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(e => e.Notes)
            .HasMaxLength(2000);

        // Relationship with Action
        builder.HasOne(e => e.Action)
            .WithMany()
            .HasForeignKey(e => e.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index for faster lookups
        builder.HasIndex(e => new { e.ActionId, e.CultureCode })
            .IsUnique();
    }
}
