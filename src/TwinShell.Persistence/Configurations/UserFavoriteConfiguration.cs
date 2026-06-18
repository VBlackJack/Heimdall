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

public sealed class UserFavoriteConfiguration : IEntityTypeConfiguration<UserFavoriteEntity>
{
    public void Configure(EntityTypeBuilder<UserFavoriteEntity> builder)
    {
        builder.ToTable("UserFavorites");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.UserId)
            .HasMaxLength(100);

        builder.Property(e => e.ActionId)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.DisplayOrder)
            .IsRequired();

        // Relationship with Action
        builder.HasOne(e => e.Action)
            .WithMany()
            .HasForeignKey(e => e.ActionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for common queries
        builder.HasIndex(e => e.ActionId)
            .HasDatabaseName("IX_UserFavorites_ActionId");

        builder.HasIndex(e => e.UserId)
            .HasDatabaseName("IX_UserFavorites_UserId");

        builder.HasIndex(e => e.DisplayOrder)
            .HasDatabaseName("IX_UserFavorites_DisplayOrder");

        // Unique constraint to prevent duplicate favorites
        builder.HasIndex(e => new { e.UserId, e.ActionId })
            .IsUnique()
            .HasDatabaseName("IX_UserFavorites_UserId_ActionId");
    }
}
