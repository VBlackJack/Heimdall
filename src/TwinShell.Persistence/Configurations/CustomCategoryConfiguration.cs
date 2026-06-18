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

public sealed class CustomCategoryConfiguration : IEntityTypeConfiguration<CustomCategoryEntity>
{
    public void Configure(EntityTypeBuilder<CustomCategoryEntity> builder)
    {
        builder.ToTable("CustomCategories");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(c => c.IconKey)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(c => c.ColorHex)
            .IsRequired()
            .HasMaxLength(7);

        builder.Property(c => c.Description)
            .HasMaxLength(500);

        builder.Property(c => c.IsSystemCategory)
            .IsRequired();

        builder.Property(c => c.DisplayOrder)
            .IsRequired();

        builder.Property(c => c.IsHidden)
            .IsRequired();

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        builder.HasIndex(c => c.DisplayOrder);
        builder.HasIndex(c => c.Name);

        // PublicId for GitOps synchronization - must be unique
        builder.Property(c => c.PublicId)
            .IsRequired();

        builder.HasIndex(c => c.PublicId)
            .IsUnique()
            .HasDatabaseName("IX_CustomCategories_PublicId");
    }
}
