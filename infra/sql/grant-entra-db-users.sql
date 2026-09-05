-- Story 1.11 (AD-21): manual, out-of-band DB-user provisioning for Azure SQL Entra-only auth.
--
-- This script is NEVER run by CI or Bicep — it is deliberately excluded from both, per AD-21's
-- three-phase cutover sequence (see docs/local-vs-azure-deltas.md#D6 and infra/README.md's
-- "Azure SQL Entra-only auth cutover" runbook). Run it by hand (sqlcmd / Azure Data Studio),
-- authenticated as the Microsoft Entra Admin configured on the server (infra/modules/
-- database-sqlserver.bicep's `administrators` block), against the target database — after
-- Deploy A (Entra Admin added, azureADOnlyAuthentication still false/omitted) and before
-- Deploy B (azureADOnlyAuthentication: true).
--
-- Re-run this same script after any from-scratch SQL Server (re)creation (disaster recovery,
-- resource-group rebuild, region move) before Deploy B is (re)applied against the new server —
-- a fresh server has an Entra Admin but zero contained database users until this runs.
--
-- CREATE USER ... FROM EXTERNAL PROVIDER resolves the bracketed value as the principal's Entra
-- ID **display name**, not its object ID — a raw object ID fails with
-- "Msg 33130 Principal '<guid>' could not be found or this principal type is not supported."
-- (verified live during this story's Task 4 execution). For a managed identity, the display name
-- equals the identity resource's own name:
--   Container App identity (system-assigned managed identity) — display name is the Container
--   App's own resource name:
--     az containerapp list --resource-group <resource-group> --query "[0].name" -o tsv
--   CI identity (energy-tracker-devops-uami, pre-provisioned — see infra/README.md's
--   "One-time identity bootstrap" section) — display name is the identity's own resource name,
--   i.e. literally "energy-tracker-devops-uami".
-- To double-check a display name against its object ID (e.g. after resolving the object ID via
-- az containerapp show --query identity.principalId / az identity show --query principalId):
--     az ad sp show --id <object-id> --query displayName -o tsv
--
-- Substitute the resolved display names for the placeholder tokens below before running. Never
-- commit a literal identity name from a live environment into this file.

-- Container App's system-assigned identity: read/write only, no schema-change rights.
CREATE USER [<CONTAINER_APP_DISPLAY_NAME>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<CONTAINER_APP_DISPLAY_NAME>];
ALTER ROLE db_datawriter ADD MEMBER [<CONTAINER_APP_DISPLAY_NAME>];

-- energy-tracker-devops-uami (CI): read/write plus DDL for EF Core migrations, including
-- migrations that carry data-migration DML (e.g. AddSmartPlugReadingUniqueIndex's dedup DELETE)
-- alongside schema changes — db_ddladmin alone grants no DML rights, so db_datareader/
-- db_datawriter are genuinely needed alongside it, not redundant.
CREATE USER [<CI_UAMI_DISPLAY_NAME>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<CI_UAMI_DISPLAY_NAME>];
ALTER ROLE db_datawriter ADD MEMBER [<CI_UAMI_DISPLAY_NAME>];
ALTER ROLE db_ddladmin ADD MEMBER [<CI_UAMI_DISPLAY_NAME>];
