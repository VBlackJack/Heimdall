-- Copyright 2026 Julien Bombled
-- Licensed under the Apache License, Version 2.0.
--
-- FROZEN FIXTURE. NEVER REGENERATE THIS FILE.
--
-- This is the TwinShell schema as it stood BEFORE the first schema step, which is what an
-- installation created by an older build still carries: EnsureCreated writes a schema only
-- when the database file is ABSENT, so on an updated installation it is a no-op and every
-- column added to the model since must arrive through a step in TwinShellSchema.
--
-- Regenerating this file from the current model would make the guard that reads it pass by
-- construction and stop protecting anything, silently - the schema under test would already
-- contain the very columns the guard exists to prove a step adds. That is not a hypothetical:
-- BL-0093 is exactly this class of defect reaching a user, whose command library opened empty
-- on 'no such column: a.LinuxExamplesJson'.
--
-- When a model column is added, do NOT touch this file: add a schema step. The guard will go
-- green again once the step exists, and that is the whole point.
--
-- Contents: the schema EnsureCreated produced at the time, minus what the steps add -
-- PublicId on Actions, CommandBatches, CommandTemplates and CustomCategories with their unique
-- indexes (step 1), and WindowsExamplesJson / LinuxExamplesJson on Actions (step 4).
CREATE TABLE "ActionCategoryMappings" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ActionCategoryMappings" PRIMARY KEY,
    "ActionId" TEXT NOT NULL,
    "CategoryId" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_ActionCategoryMappings_Actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES "Actions" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ActionCategoryMappings_CustomCategories_CategoryId" FOREIGN KEY ("CategoryId") REFERENCES "CustomCategories" ("Id") ON DELETE CASCADE
);
CREATE TABLE "ActionTranslations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ActionTranslations" PRIMARY KEY,
    "ActionId" TEXT NOT NULL,
    "CultureCode" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Description" TEXT NOT NULL,
    "Notes" TEXT NULL,
    CONSTRAINT "FK_ActionTranslations_Actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES "Actions" ("Id") ON DELETE CASCADE
);
CREATE TABLE "Actions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Actions" PRIMARY KEY,
    "Title" TEXT NOT NULL,
    "Description" TEXT NOT NULL,
    "Category" TEXT NOT NULL,
    "Platform" INTEGER NOT NULL,
    "Level" INTEGER NOT NULL,
    "TagsJson" TEXT NOT NULL,
    "WindowsCommandTemplateId" TEXT NULL,
    "LinuxCommandTemplateId" TEXT NULL,
    "ExamplesJson" TEXT NOT NULL,
    "Notes" TEXT NULL,
    "LinksJson" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "IsUserCreated" INTEGER NOT NULL,
    CONSTRAINT "FK_Actions_CommandTemplates_LinuxCommandTemplateId" FOREIGN KEY ("LinuxCommandTemplateId") REFERENCES "CommandTemplates" ("Id") ON DELETE SET NULL,
    CONSTRAINT "FK_Actions_CommandTemplates_WindowsCommandTemplateId" FOREIGN KEY ("WindowsCommandTemplateId") REFERENCES "CommandTemplates" ("Id") ON DELETE SET NULL
);
CREATE TABLE "CommandBatches" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CommandBatches" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "ExecutionMode" INTEGER NOT NULL,
    "CommandsJson" TEXT NOT NULL,
    "TagsJson" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "LastExecutedAt" TEXT NULL,
    "IsUserCreated" INTEGER NOT NULL
);
CREATE TABLE "CommandHistories" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CommandHistories" PRIMARY KEY,
    "UserId" TEXT NULL,
    "ActionId" TEXT NOT NULL,
    "GeneratedCommand" TEXT NOT NULL,
    "ParametersJson" TEXT NOT NULL,
    "Platform" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Category" TEXT NOT NULL,
    "ActionTitle" TEXT NOT NULL,
    "IsExecuted" INTEGER NOT NULL,
    "ExitCode" INTEGER NULL,
    "ExecutionDurationTicks" INTEGER NULL,
    "ExecutionSuccess" INTEGER NULL,
    CONSTRAINT "FK_CommandHistories_Actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES "Actions" ("Id") ON DELETE CASCADE
);
CREATE TABLE "CommandTemplates" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CommandTemplates" PRIMARY KEY,
    "Platform" INTEGER NOT NULL,
    "Name" TEXT NOT NULL,
    "CommandPattern" TEXT NOT NULL,
    "ParametersJson" TEXT NOT NULL
);
CREATE TABLE "CustomCategories" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_CustomCategories" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "IconKey" TEXT NOT NULL,
    "ColorHex" TEXT NOT NULL,
    "IsSystemCategory" INTEGER NOT NULL,
    "DisplayOrder" INTEGER NOT NULL,
    "IsHidden" INTEGER NOT NULL,
    "Description" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "ModifiedAt" TEXT NULL
);
CREATE TABLE "SearchHistories" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SearchHistories" PRIMARY KEY,
    "SearchTerm" TEXT NOT NULL,
    "NormalizedSearchTerm" TEXT NOT NULL,
    "SearchCount" INTEGER NOT NULL,
    "ResultCount" INTEGER NOT NULL,
    "LastSearchedAt" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "WasSuccessful" INTEGER NOT NULL,
    "UserId" TEXT NULL
);
CREATE TABLE "SyncHistories" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SyncHistories" PRIMARY KEY,
    "OperationType" TEXT NOT NULL,
    "Success" INTEGER NOT NULL,
    "ErrorCode" INTEGER NOT NULL,
    "Message" TEXT NOT NULL,
    "ErrorDetails" TEXT NULL,
    "ItemsCreated" INTEGER NOT NULL,
    "ItemsUpdated" INTEGER NOT NULL,
    "ItemsExported" INTEGER NOT NULL,
    "ItemsSkipped" INTEGER NOT NULL,
    "ConflictsDetected" INTEGER NOT NULL,
    "CommitsMerged" INTEGER NOT NULL,
    "DurationMs" INTEGER NOT NULL,
    "RemoteUrl" TEXT NULL,
    "Branch" TEXT NULL,
    "StartedAt" TEXT NOT NULL,
    "CompletedAt" TEXT NOT NULL
);
CREATE TABLE "UserFavorites" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_UserFavorites" PRIMARY KEY,
    "UserId" TEXT NULL,
    "ActionId" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "DisplayOrder" INTEGER NOT NULL,
    CONSTRAINT "FK_UserFavorites_Actions_ActionId" FOREIGN KEY ("ActionId") REFERENCES "Actions" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX "IX_ActionCategoryMappings_ActionId_CategoryId" ON "ActionCategoryMappings" ("ActionId", "CategoryId");
CREATE INDEX "IX_ActionCategoryMappings_CategoryId" ON "ActionCategoryMappings" ("CategoryId");
CREATE UNIQUE INDEX "IX_ActionTranslations_ActionId_CultureCode" ON "ActionTranslations" ("ActionId", "CultureCode");
CREATE INDEX "IX_Actions_Category" ON "Actions" ("Category");
CREATE INDEX "IX_Actions_IsUserCreated" ON "Actions" ("IsUserCreated");
CREATE INDEX "IX_Actions_Level" ON "Actions" ("Level");
CREATE INDEX "IX_Actions_LinuxCommandTemplateId" ON "Actions" ("LinuxCommandTemplateId");
CREATE INDEX "IX_Actions_Platform" ON "Actions" ("Platform");
CREATE INDEX "IX_Actions_Title" ON "Actions" ("Title");
CREATE INDEX "IX_Actions_WindowsCommandTemplateId" ON "Actions" ("WindowsCommandTemplateId");
CREATE INDEX "IX_CommandBatches_CreatedAt" ON "CommandBatches" ("CreatedAt");
CREATE INDEX "IX_CommandBatches_LastExecutedAt" ON "CommandBatches" ("LastExecutedAt");
CREATE INDEX "IX_CommandBatches_Name" ON "CommandBatches" ("Name");
CREATE INDEX "IX_CommandHistories_ActionId" ON "CommandHistories" ("ActionId");
CREATE INDEX "IX_CommandHistories_ActionTitle" ON "CommandHistories" ("ActionTitle");
CREATE INDEX "IX_CommandHistories_Category" ON "CommandHistories" ("Category");
CREATE INDEX "IX_CommandHistories_CreatedAt" ON "CommandHistories" ("CreatedAt");
CREATE INDEX "IX_CommandHistories_Platform" ON "CommandHistories" ("Platform");
CREATE INDEX "IX_CommandHistories_UserId" ON "CommandHistories" ("UserId");
CREATE INDEX "IX_CustomCategories_DisplayOrder" ON "CustomCategories" ("DisplayOrder");
CREATE INDEX "IX_CustomCategories_Name" ON "CustomCategories" ("Name");
CREATE INDEX "IX_SearchHistories_LastSearchedAt" ON "SearchHistories" ("LastSearchedAt");
CREATE INDEX "IX_SearchHistories_NormalizedSearchTerm" ON "SearchHistories" ("NormalizedSearchTerm");
CREATE UNIQUE INDEX "IX_SearchHistories_NormalizedSearchTerm_UserId" ON "SearchHistories" ("NormalizedSearchTerm", "UserId");
CREATE INDEX "IX_SearchHistories_SearchCount" ON "SearchHistories" ("SearchCount");
CREATE INDEX "IX_SearchHistories_UserId" ON "SearchHistories" ("UserId");
CREATE INDEX "IX_SyncHistories_OperationType" ON "SyncHistories" ("OperationType");
CREATE INDEX "IX_SyncHistories_StartedAt" ON "SyncHistories" ("StartedAt");
CREATE INDEX "IX_SyncHistories_StartedAt_OperationType" ON "SyncHistories" ("StartedAt", "OperationType");
CREATE INDEX "IX_SyncHistories_Success" ON "SyncHistories" ("Success");
CREATE INDEX "IX_UserFavorites_ActionId" ON "UserFavorites" ("ActionId");
CREATE INDEX "IX_UserFavorites_DisplayOrder" ON "UserFavorites" ("DisplayOrder");
CREATE INDEX "IX_UserFavorites_UserId" ON "UserFavorites" ("UserId");
CREATE UNIQUE INDEX "IX_UserFavorites_UserId_ActionId" ON "UserFavorites" ("UserId", "ActionId");
