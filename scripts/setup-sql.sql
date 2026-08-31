-- Grant the Container Apps managed identity access to the jobs database.
-- Run this ONCE, connected as the Entra ID (AAD) admin of the SQL server.
--
-- The modern sqlcmd (go-sqlcmd) passes -v uamiName=... which fills :uamiName below:
--   sqlcmd -S <server>.database.windows.net -d jobs -G -i scripts/setup-sql.sql -v uamiName="acajobs-id"
--
-- Or replace $(uamiName) manually with the user-assigned identity name printed
-- by deploy.ps1 (output: managedIdentityName) and run in Azure Data Studio / SSMS.

CREATE USER [$(uamiName)] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$(uamiName)];
ALTER ROLE db_datawriter ADD MEMBER [$(uamiName)];
ALTER ROLE db_ddladmin   ADD MEMBER [$(uamiName)];  -- app creates dbo.JobExecutions on first run
GO
