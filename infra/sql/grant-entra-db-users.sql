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
-- Resolving the two placeholder object IDs below:
--   Container App identity (system-assigned managed identity):
--     az containerapp show --name <container-app-name> --resource-group <resource-group> \
--       --query identity.principalId -o tsv
--   CI identity (energy-tracker-devops-uami, pre-provisioned — see infra/README.md's
--   "One-time identity bootstrap" section):
--     az identity show --name energy-tracker-devops-uami --resource-group energy-tracker-devops-rg \
--       --query principalId -o tsv
--
-- Substitute the resolved GUIDs for the placeholder tokens below before running. Never commit a
-- literal object ID from a live environment into this file.

-- Container App's system-assigned identity: read/write only, no schema-change rights.
CREATE USER [<CONTAINER_APP_PRINCIPAL_ID>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<CONTAINER_APP_PRINCIPAL_ID>];
ALTER ROLE db_datawriter ADD MEMBER [<CONTAINER_APP_PRINCIPAL_ID>];

-- energy-tracker-devops-uami (CI): read/write plus DDL for EF Core migrations, including
-- migrations that carry data-migration DML (e.g. AddSmartPlugReadingUniqueIndex's dedup DELETE)
-- alongside schema changes — db_ddladmin alone grants no DML rights, so db_datareader/
-- db_datawriter are genuinely needed alongside it, not redundant.
CREATE USER [<CI_UAMI_PRINCIPAL_ID>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<CI_UAMI_PRINCIPAL_ID>];
ALTER ROLE db_datawriter ADD MEMBER [<CI_UAMI_PRINCIPAL_ID>];
ALTER ROLE db_ddladmin ADD MEMBER [<CI_UAMI_PRINCIPAL_ID>];
