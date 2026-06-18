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

public sealed class ActionCategoryMappingConfiguration : IEntityTypeConfiguration<ActionCategoryMappingEntity>
{
    public void Configure(EntityTypeBuilder<ActionCategoryMappingEntity> builder)
    {
        builder.ToTable("ActionCategoryMappings");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.ActionId)
            .IsRequired();

        builder.Property(m => m.CategoryId)
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .IsRequired();

        // Configure many-to-many relationship
        builder.HasOne(m => m.Action)
            .WithMany()
            .HasForeignKey(m => m.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Category)
            .WithMany(c => c.ActionMappings)
            .HasForeignKey(m => m.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Prevent duplicate mappings
        builder.HasIndex(m => new { m.ActionId, m.CategoryId })
            .IsUnique();
    }
}
