/* Run this only if you already created the MountainApp database with an
   earlier version of 01_Create_Database_And_Schema.sql (before two-factor
   login existed). If you are setting up fresh, 01_... already includes
   these columns and you do not need this file. Safe to run more than once. */
USE MountainApp;
GO
IF COL_LENGTH('dbo.Users', 'TotpSecret') IS NULL
    ALTER TABLE dbo.Users ADD TotpSecret NVARCHAR(64) NULL;
GO
IF COL_LENGTH('dbo.Users', 'TwoFactorOn') IS NULL
    ALTER TABLE dbo.Users ADD TwoFactorOn BIT NOT NULL DEFAULT 0;
GO
PRINT 'Two-factor columns are in place.';
