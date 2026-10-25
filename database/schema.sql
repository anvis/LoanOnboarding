/* ---------------------------------------------------------------------------------------------
   LoanOnboarding origination database. Azure SQL / SQL Server.
   Idempotent: safe to run more than once. Keep in sync with src/LoanOnboard.Core/Data/LoanDbContext.cs.
   Run with sqlcmd, SSMS, Azure Data Studio, or the deploy-infra GitHub workflow.
   --------------------------------------------------------------------------------------------- */

SET NOCOUNT ON;
GO

/* ---------- Application ---------- */
IF OBJECT_ID(N'dbo.Application', N'U') IS NULL
CREATE TABLE dbo.Application (
    AckId           CHAR(26)       NOT NULL CONSTRAINT PK_Application PRIMARY KEY,
    DealerId        NVARCHAR(50)   NOT NULL,
    IdempotencyKey  NVARCHAR(100)  NOT NULL,
    ProductType     NVARCHAR(30)   NOT NULL,
    CustomerId      UNIQUEIDENTIFIER NOT NULL,
    RequestedAmount DECIMAL(18,2)  NOT NULL,
    TermMonths      INT            NOT NULL,
    Status          NVARCHAR(30)   NOT NULL,
    CurrentStage    NVARCHAR(30)   NULL,
    LoanNumber      NVARCHAR(20)   NULL,
    CreatedAt       DATETIME2      NOT NULL,
    UpdatedAt       DATETIME2      NOT NULL,
    RowVersion      ROWVERSION     NOT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Application_DealerId_IdempotencyKey')
    CREATE UNIQUE INDEX IX_Application_DealerId_IdempotencyKey ON dbo.Application (DealerId, IdempotencyKey);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Application_LoanNumber')
    CREATE UNIQUE INDEX IX_Application_LoanNumber ON dbo.Application (LoanNumber) WHERE LoanNumber IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Application_DealerId_Status')
    CREATE INDEX IX_Application_DealerId_Status ON dbo.Application (DealerId, Status);
GO

/* ---------- Customer ---------- */
IF OBJECT_ID(N'dbo.Customer', N'U') IS NULL
CREATE TABLE dbo.Customer (
    CustomerId      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    AckId           CHAR(26)       NOT NULL CONSTRAINT FK_Customer_Application REFERENCES dbo.Application (AckId),
    FirstName       NVARCHAR(100)  NOT NULL,
    LastName        NVARCHAR(100)  NOT NULL,
    Email           NVARCHAR(256)  NOT NULL,
    Phone           NVARCHAR(30)   NOT NULL,
    DateOfBirth     DATE           NOT NULL,
    IdentifierHash  CHAR(64)       NOT NULL,   -- HMAC of the national id; the raw value is never stored
    IdentifierLast4 CHAR(4)        NOT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Customer_AckId')
    CREATE UNIQUE INDEX IX_Customer_AckId ON dbo.Customer (AckId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Customer_IdentifierHash')
    CREATE INDEX IX_Customer_IdentifierHash ON dbo.Customer (IdentifierHash);
GO

/* ---------- LoanStatusHistory (append-only audit trail) ---------- */
IF OBJECT_ID(N'dbo.LoanStatusHistory', N'U') IS NULL
CREATE TABLE dbo.LoanStatusHistory (
    Id              BIGINT IDENTITY NOT NULL CONSTRAINT PK_LoanStatusHistory PRIMARY KEY,
    AckId           CHAR(26)       NOT NULL CONSTRAINT FK_LoanStatusHistory_Application REFERENCES dbo.Application (AckId),
    Stage           NVARCHAR(30)   NULL,
    Status          NVARCHAR(30)   NOT NULL,
    Reason          NVARCHAR(400)  NULL,
    EventId         UNIQUEIDENTIFIER NOT NULL,
    CorrelationId   CHAR(26)       NOT NULL,
    OccurredAt      DATETIME2      NOT NULL,
    Source          NVARCHAR(50)   NOT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoanStatusHistory_EventId')
    CREATE UNIQUE INDEX IX_LoanStatusHistory_EventId ON dbo.LoanStatusHistory (EventId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoanStatusHistory_AckId_OccurredAt')
    CREATE INDEX IX_LoanStatusHistory_AckId_OccurredAt ON dbo.LoanStatusHistory (AckId, OccurredAt);
GO

/* ---------- StageExecution ---------- */
IF OBJECT_ID(N'dbo.StageExecution', N'U') IS NULL
CREATE TABLE dbo.StageExecution (
    Id              BIGINT IDENTITY NOT NULL CONSTRAINT PK_StageExecution PRIMARY KEY,
    AckId           CHAR(26)       NOT NULL CONSTRAINT FK_StageExecution_Application REFERENCES dbo.Application (AckId),
    Stage           NVARCHAR(30)   NOT NULL,
    Attempt         INT            NOT NULL,
    Outcome         NVARCHAR(20)   NULL,
    ReasonCode      NVARCHAR(100)  NULL,
    StartedAt       DATETIME2      NOT NULL,
    DueBy           DATETIME2      NOT NULL,
    CompletedAt     DATETIME2      NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StageExecution_AckId_Stage_Attempt')
    CREATE INDEX IX_StageExecution_AckId_Stage_Attempt ON dbo.StageExecution (AckId, Stage, Attempt);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StageExecution_DueBy')
    CREATE INDEX IX_StageExecution_DueBy ON dbo.StageExecution (DueBy) WHERE Outcome IS NULL;
GO

/* ---------- StagePlan (stage order as data) ---------- */
IF OBJECT_ID(N'dbo.StagePlan', N'U') IS NULL
CREATE TABLE dbo.StagePlan (
    ProductType     NVARCHAR(30)   NOT NULL,
    Sequence        INT            NOT NULL,
    Stage           NVARCHAR(30)   NOT NULL,
    StatusName      NVARCHAR(30)   NOT NULL,
    Required        BIT            NOT NULL,
    DueMinutes      INT            NOT NULL,
    CONSTRAINT PK_StagePlan PRIMARY KEY (ProductType, Sequence)
);
GO

/* ---------- Document ---------- */
IF OBJECT_ID(N'dbo.Document', N'U') IS NULL
CREATE TABLE dbo.Document (
    DocId           UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Document PRIMARY KEY,
    AckId           CHAR(26)       NOT NULL CONSTRAINT FK_Document_Application REFERENCES dbo.Application (AckId),
    Type            NVARCHAR(50)   NOT NULL,
    FileName        NVARCHAR(260)  NOT NULL,
    ContentType     NVARCHAR(100)  NOT NULL,
    SizeBytes       BIGINT         NOT NULL,
    BlobPath        NVARCHAR(500)  NOT NULL,
    Sha256          CHAR(64)       NOT NULL,
    ScanStatus      NVARCHAR(20)   NOT NULL,
    UploadedAt      DATETIME2      NOT NULL,
    ExpiresAt       DATETIME2      NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Document_AckId')
    CREATE INDEX IX_Document_AckId ON dbo.Document (AckId);
GO

/* ---------- Outbox ---------- */
IF OBJECT_ID(N'dbo.Outbox', N'U') IS NULL
CREATE TABLE dbo.Outbox (
    Id              BIGINT IDENTITY NOT NULL CONSTRAINT PK_Outbox PRIMARY KEY,
    AckId           CHAR(26)       NOT NULL,
    Type            NVARCHAR(50)   NOT NULL,   -- StageRequested, EmailRequested, LoanBooked
    Topic           NVARCHAR(80)   NOT NULL,   -- loan.stage.commands, loan.notifications, loan.events
    Stage           NVARCHAR(30)   NULL,       -- becomes the "stage" message property
    Payload         NVARCHAR(MAX)  NOT NULL,   -- JSON: ids only, no personal data
    CreatedAt       DATETIME2      NOT NULL,
    AvailableAt     DATETIME2      NOT NULL,
    LockedUntil     DATETIME2      NULL,
    PublishedAt     DATETIME2      NULL,
    Attempts        INT            NOT NULL CONSTRAINT DF_Outbox_Attempts DEFAULT 0
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Outbox_Pending')
    CREATE INDEX IX_Outbox_Pending ON dbo.Outbox (Id) WHERE PublishedAt IS NULL;
GO

/* ---------- ProcessedMessage (inbox) ---------- */
IF OBJECT_ID(N'dbo.ProcessedMessage', N'U') IS NULL
CREATE TABLE dbo.ProcessedMessage (
    EventId         NVARCHAR(100)  NOT NULL,
    Consumer        NVARCHAR(50)   NOT NULL,
    ProcessedAt     DATETIME2      NOT NULL,
    CONSTRAINT PK_ProcessedMessage PRIMARY KEY (EventId, Consumer)
);
GO

/* ---------- AgentReview ---------- */
IF OBJECT_ID(N'dbo.AgentReview', N'U') IS NULL
CREATE TABLE dbo.AgentReview (
    Id              BIGINT IDENTITY NOT NULL CONSTRAINT PK_AgentReview PRIMARY KEY,
    AckId           CHAR(26)       NOT NULL CONSTRAINT FK_AgentReview_Application REFERENCES dbo.Application (AckId),
    AgentId         NVARCHAR(100)  NOT NULL,
    Decision        NVARCHAR(20)   NOT NULL,
    ChecklistJson   NVARCHAR(MAX)  NOT NULL,
    ConsentRef      NVARCHAR(200)  NOT NULL,
    ScriptVersion   NVARCHAR(50)   NOT NULL,
    CallReference   NVARCHAR(200)  NOT NULL,
    Comment         NVARCHAR(1000) NULL,
    DecidedAt       DATETIME2      NOT NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AgentReview_AckId')
    CREATE INDEX IX_AgentReview_AckId ON dbo.AgentReview (AckId);
GO

/* ---------- Loan number sequence (the API adds a prefix and a Luhn check digit) ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = N'LoanNumberSeq' AND schema_id = SCHEMA_ID(N'dbo'))
    CREATE SEQUENCE dbo.LoanNumberSeq AS BIGINT START WITH 1000001 INCREMENT BY 1 NO CYCLE;
GO

/* ---------- Seed: default stage plan. Change or add rows to change the order or add a stage. ---------- */
MERGE dbo.StagePlan AS target
USING (VALUES
    (N'DEFAULT', 1, N'Verification',  N'VERIFICATION',  1,   60),
    (N'DEFAULT', 2, N'CustomerCheck', N'CUSTOMER_CHECK', 1,   60),
    (N'DEFAULT', 3, N'CreditCheck',   N'CREDIT_CHECK',   1,  120),
    (N'DEFAULT', 4, N'Underwriting',  N'UNDERWRITING',   1,  480)
) AS source (ProductType, Sequence, Stage, StatusName, Required, DueMinutes)
ON target.ProductType = source.ProductType AND target.Sequence = source.Sequence
WHEN NOT MATCHED THEN
    INSERT (ProductType, Sequence, Stage, StatusName, Required, DueMinutes)
    VALUES (source.ProductType, source.Sequence, source.Stage, source.StatusName, source.Required, source.DueMinutes);
GO

PRINT 'LoanOnboarding schema is up to date.';
GO

/* ---------- Optional: least-privilege database users for managed identities ----------
   Replace the names with your App Service and Function App names, then run once as the Entra admin.
   Only needed if you switch from SQL authentication to managed identity.

   CREATE USER [<web-app-name>] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datareader ADD MEMBER [<web-app-name>];
   ALTER ROLE db_datawriter ADD MEMBER [<web-app-name>];
   GRANT UPDATE ON OBJECT::dbo.LoanNumberSeq TO [<web-app-name>];   -- NEXT VALUE FOR needs UPDATE on the sequence

   CREATE USER [<function-app-name>] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datareader ADD MEMBER [<function-app-name>];
   ALTER ROLE db_datawriter ADD MEMBER [<function-app-name>];
*/
