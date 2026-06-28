CREATE TABLE FabrCoreExecutionRecords (
    TraceId nvarchar(64) NOT NULL,
    SegmentId nvarchar(256) NOT NULL,
    Sequence bigint NOT NULL,
    RecordId nvarchar(64) NOT NULL,
    Kind nvarchar(64) NOT NULL,
    SpanId nvarchar(32) NULL,
    ParentSpanId nvarchar(32) NULL,
    ParentRecordId nvarchar(64) NULL,
    UserHandle nvarchar(256) NULL,
    AgentHandle nvarchar(256) NULL,
    AgentType nvarchar(256) NULL,
    Subject nvarchar(512) NULL,
    PayloadHash nvarchar(128) NULL,
    RecordJson nvarchar(max) NOT NULL,
    CreatedUtc datetimeoffset NOT NULL CONSTRAINT DF_FabrCoreExecutionRecords_CreatedUtc DEFAULT sysutcdatetime(),
    CONSTRAINT PK_FabrCoreExecutionRecords PRIMARY KEY (TraceId, SegmentId, Sequence),
    CONSTRAINT UQ_FabrCoreExecutionRecords_RecordId UNIQUE (RecordId)
);

CREATE TABLE FabrCoreExecutionSignatures (
    SignatureId nvarchar(64) NOT NULL PRIMARY KEY,
    TraceId nvarchar(64) NOT NULL,
    SegmentId nvarchar(256) NOT NULL,
    Sequence bigint NOT NULL,
    RecordId nvarchar(64) NOT NULL,
    PreviousSignatureDigest nvarchar(128) NULL,
    RecordDigest nvarchar(128) NOT NULL,
    SignatureDigest nvarchar(128) NOT NULL,
    SignerIdentity nvarchar(512) NULL,
    SignerIdentityKind nvarchar(64) NOT NULL,
    SignatureAlgorithm nvarchar(64) NULL,
    CertificateChainDigest nvarchar(128) NULL,
    Signature varbinary(max) NOT NULL,
    CreatedUtc datetimeoffset NOT NULL,
    CONSTRAINT FK_FabrCoreExecutionSignatures_Record
        FOREIGN KEY (RecordId) REFERENCES FabrCoreExecutionRecords(RecordId)
);

CREATE TABLE FabrCoreExecutionCertificates (
    Digest nvarchar(128) NOT NULL PRIMARY KEY,
    Subject nvarchar(512) NULL,
    Issuer nvarchar(512) NULL,
    NotBefore datetimeoffset NULL,
    NotAfter datetimeoffset NULL,
    DerChain varbinary(max) NOT NULL,
    CreatedUtc datetimeoffset NOT NULL CONSTRAINT DF_FabrCoreExecutionCertificates_CreatedUtc DEFAULT sysutcdatetime()
);

CREATE TABLE FabrCoreExecutionAttestations (
    AttestationId nvarchar(64) NOT NULL PRIMARY KEY,
    TraceId nvarchar(64) NOT NULL,
    ParentRecordId nvarchar(64) NULL,
    RequestRecordId nvarchar(64) NULL,
    ResponseRecordId nvarchar(64) NULL,
    RequestDigest nvarchar(128) NULL,
    ResponseDigest nvarchar(128) NULL,
    TerminalStatus nvarchar(64) NULL,
    SignerIdentity nvarchar(512) NULL,
    SignatureDigest nvarchar(128) NULL,
    CreatedUtc datetimeoffset NOT NULL
);

CREATE INDEX IX_FabrCoreExecutionRecords_TraceId ON FabrCoreExecutionRecords (TraceId, Sequence);
CREATE INDEX IX_FabrCoreExecutionSignatures_TraceId ON FabrCoreExecutionSignatures (TraceId, Sequence);
CREATE INDEX IX_FabrCoreExecutionAttestations_TraceId ON FabrCoreExecutionAttestations (TraceId);
