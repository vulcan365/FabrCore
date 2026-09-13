-- Additive monitoring migration; apply after the operational database schema.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
        IF OBJECT_ID('fabrOps.MonitorStore') IS NULL
        BEGIN
          CREATE TABLE fabrOps.MonitorStore(Id int NOT NULL PRIMARY KEY CHECK(Id=1), StoreId uniqueidentifier NOT NULL);
          INSERT fabrOps.MonitorStore VALUES(1,NEWID());
          CREATE TABLE fabrOps.MonitorRecord(Sequence bigint IDENTITY PRIMARY KEY,RecordId nvarchar(128) NOT NULL,Kind nvarchar(16) NOT NULL,
            AgentHandle nvarchar(512) NULL,TraceId nvarchar(128) NULL,Channel nvarchar(256) NULL,ExecutionCategory nvarchar(16) NOT NULL,
            Timestamp datetimeoffset NOT NULL,Payload nvarchar(max) NOT NULL CHECK(ISJSON(Payload)=1),UNIQUE(RecordId,Kind));
          CREATE INDEX IX_Monitor_Agent ON fabrOps.MonitorRecord(AgentHandle,Sequence);
          CREATE INDEX IX_Monitor_Trace ON fabrOps.MonitorRecord(TraceId,Sequence);
          CREATE INDEX IX_Monitor_Time ON fabrOps.MonitorRecord(Timestamp,Sequence);
        END
COMMIT;
