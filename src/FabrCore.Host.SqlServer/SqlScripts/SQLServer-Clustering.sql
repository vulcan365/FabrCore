-- For each deployment, there will be only one (active) membership version table version column which will be updated periodically.
IF OBJECT_ID(N'[orlns].[OrleansMembershipVersionTable]', 'U') IS NULL
CREATE TABLE orlns.OrleansMembershipVersionTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	Timestamp DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(),
	Version INT NOT NULL DEFAULT 0,

	CONSTRAINT PK_OrleansMembershipVersionTable_DeploymentId PRIMARY KEY(DeploymentId)
);

-- Every silo instance has a row in the membership table.
IF OBJECT_ID(N'[orlns].[OrleansMembershipTable]', 'U') IS NULL
CREATE TABLE orlns.OrleansMembershipTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	Address VARCHAR(45) NOT NULL,
	Port INT NOT NULL,
	Generation INT NOT NULL,
	SiloName NVARCHAR(150) NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	Status INT NOT NULL,
	ProxyPort INT NULL,
	SuspectTimes VARCHAR(8000) NULL,
	StartTime DATETIME2(3) NOT NULL,
	IAmAliveTime DATETIME2(3) NOT NULL,

	CONSTRAINT PK_MembershipTable_DeploymentId PRIMARY KEY(DeploymentId, Address, Port, Generation),
	CONSTRAINT FK_MembershipTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES orlns.OrleansMembershipVersionTable (DeploymentId)
);

INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT
	'UpdateIAmAlivetimeKey',
	'-- This is expected to never fail by Orleans, so return value
	-- is not needed nor is it checked.
	SET NOCOUNT ON;
	UPDATE orlns.OrleansMembershipTable
	SET
		IAmAliveTime = @IAmAliveTime
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL;
	'
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'UpdateIAmAlivetimeKey'
);

INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT 
	'InsertMembershipVersionKey',
	'SET NOCOUNT ON;
	INSERT INTO orlns.OrleansMembershipVersionTable
	(
		DeploymentId
	)
	SELECT @DeploymentId
	WHERE NOT EXISTS
	(
		SELECT 1
		FROM
			orlns.OrleansMembershipVersionTable WITH(HOLDLOCK, XLOCK, ROWLOCK)
		WHERE
			DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
	);
	
	SELECT @@ROWCOUNT;
	'
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'InsertMembershipVersionKey'
);

INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT
	'InsertMembershipKey',
	'SET XACT_ABORT, NOCOUNT ON;
	DECLARE @ROWCOUNT AS INT;
	BEGIN TRANSACTION;
	INSERT INTO orlns.OrleansMembershipTable
	(
		DeploymentId,
		Address,
		Port,
		Generation,
		SiloName,
		HostName,
		Status,
		ProxyPort,
		StartTime,
		IAmAliveTime
	)
	SELECT
		@DeploymentId,
		@Address,
		@Port,
		@Generation,
		@SiloName,
		@HostName,
		@Status,
		@ProxyPort,
		@StartTime,
		@IAmAliveTime
	WHERE NOT EXISTS
	(
		SELECT 1
		FROM
			orlns.OrleansMembershipTable WITH(HOLDLOCK, XLOCK, ROWLOCK)
		WHERE
			DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
			AND Address = @Address AND @Address IS NOT NULL
			AND Port = @Port AND @Port IS NOT NULL
			AND Generation = @Generation AND @Generation IS NOT NULL
	);

	UPDATE orlns.OrleansMembershipVersionTable
	SET
		Timestamp = GETUTCDATE(),
		Version = Version + 1
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL
		AND @@ROWCOUNT > 0;
	
	SET @ROWCOUNT = @@ROWCOUNT;
	
	IF @ROWCOUNT = 0
		ROLLBACK TRANSACTION
	ELSE
		COMMIT TRANSACTION
	SELECT @ROWCOUNT;
	'
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'InsertMembershipKey'
);

INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT
	'UpdateMembershipKey',
	'SET XACT_ABORT, NOCOUNT ON;
	BEGIN TRANSACTION;
	
	UPDATE orlns.OrleansMembershipVersionTable
	SET
		Timestamp = GETUTCDATE(),
		Version = Version + 1
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL;
	
	UPDATE orlns.OrleansMembershipTable
	SET
		Status = @Status,
		SuspectTimes = @SuspectTimes,
		IAmAliveTime = @IAmAliveTime
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL
		AND @@ROWCOUNT > 0;
	
	SELECT @@ROWCOUNT;
	COMMIT TRANSACTION;
	'
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'UpdateMembershipKey'
);

INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT
	'GatewaysQueryKey',
	'SELECT
		Address,
		ProxyPort,
		Generation
	FROM
		orlns.OrleansMembershipTable
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Status = @Status AND @Status IS NOT NULL
		AND ProxyPort > 0;
	'
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'GatewaysQueryKey'
);

INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT
	'MembershipReadRowKey',
	'SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.SiloName,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.SuspectTimes,
		m.StartTime,
		m.IAmAliveTime,
		v.Version
	FROM
		orlns.OrleansMembershipVersionTable v
		-- This ensures the version table will returned even if there is no matching membership row.
		LEFT OUTER JOIN orlns.OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL
	WHERE
		v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
	'
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'MembershipReadRowKey'
);

INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT
	'MembershipReadAllKey',
	'SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.SiloName,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.SuspectTimes,
		m.StartTime,
		m.IAmAliveTime,
		v.Version
	FROM
		orlns.OrleansMembershipVersionTable v LEFT OUTER JOIN orlns.OrleansMembershipTable m
		ON v.DeploymentId = m.DeploymentId
	WHERE
		v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
	'
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'MembershipReadAllKey'
);

INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT
	'DeleteMembershipTableEntriesKey',
	'DELETE FROM OrleansMembershipTable
	WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
	DELETE FROM orlns.OrleansMembershipVersionTable
	WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'DeleteMembershipTableEntriesKey'
);

-- Migration from Orleans 3.7.0: CleanupDefunctSiloEntriesKey
-- (missing from official script, see https://github.com/dotnet/orleans/issues/8216)
INSERT INTO orlns.OrleansQuery(QueryKey, QueryText)
SELECT
	'CleanupDefunctSiloEntriesKey',
	'DELETE FROM OrleansMembershipTable
	WHERE DeploymentId = @DeploymentId
		AND @DeploymentId IS NOT NULL
		AND IAmAliveTime < @IAmAliveTime
		AND Status != 3;
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM orlns.OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'CleanupDefunctSiloEntriesKey'
);
