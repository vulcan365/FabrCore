-- Optional FabrCore credential-protection key ring, operational database.
-- Use when FabrCore:Database:AutoInitialize=false (or Orleans AutoInitDatabase=false
-- for an Orleans-only SQL deployment). Certificate encryption is configured on hosts.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @result int;
EXEC @result=sys.sp_getapplock @Resource='FabrCore.DataProtection.Schema.v1',
    @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=25000;
IF @result < 0 THROW 51000, 'Protection schema lock could not be acquired.', 1;
IF SCHEMA_ID('fabrOps') IS NULL EXEC('CREATE SCHEMA fabrOps');
IF OBJECT_ID('fabrOps.DataProtectionKey','U') IS NULL
    CREATE TABLE fabrOps.DataProtectionKey(
        ApplicationId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Id uniqueidentifier NOT NULL,
        FriendlyName nvarchar(256) NULL,
        Xml nvarchar(max) NOT NULL,
        CONSTRAINT PK_DataProtectionKey PRIMARY KEY(ApplicationId,Id));
COMMIT TRANSACTION;
