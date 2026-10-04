/* =====================================================================
   Mountain Trading & Contracting W.L.L - Management App
   Run this whole file in SQL Server Management Studio (SSMS), once.
   Creates the database, all tables, and starter data.
   ===================================================================== */

IF DB_ID('MountainApp') IS NULL
    CREATE DATABASE MountainApp;
GO
USE MountainApp;
GO

CREATE TABLE dbo.Branches(
    Id      INT IDENTITY PRIMARY KEY,
    Name    NVARCHAR(60) NOT NULL UNIQUE,
    CR      NVARCHAR(40) NOT NULL DEFAULT ''
);
GO

CREATE TABLE dbo.Users(
    Id          INT IDENTITY PRIMARY KEY,
    Username    NVARCHAR(40) NOT NULL UNIQUE,
    FullName    NVARCHAR(80) NOT NULL,
    PasswordHash NVARCHAR(200) NOT NULL,
    Role        NVARCHAR(20) NOT NULL CHECK(Role IN('superadmin','admin','user')),
    Active      BIT NOT NULL DEFAULT 1,
    Theme       NVARCHAR(10) NOT NULL DEFAULT 'system',
    Lang        NVARCHAR(5)  NOT NULL DEFAULT 'en',
    TotpSecret  NVARCHAR(64) NULL,
    TwoFactorOn BIT NOT NULL DEFAULT 0,
    Created     DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.UserBranches(
    UserId   INT NOT NULL REFERENCES dbo.Users(Id),
    BranchId INT NOT NULL REFERENCES dbo.Branches(Id),
    PRIMARY KEY(UserId, BranchId)
);
GO

CREATE TABLE dbo.Transactions(
    Id            INT IDENTITY PRIMARY KEY,
    ReceiptNo     NVARCHAR(20) NULL,
    [Date]        DATE NOT NULL,
    BranchId      INT NOT NULL REFERENCES dbo.Branches(Id),
    [Type]        NVARCHAR(10) NOT NULL CHECK([Type] IN('income','expense','transfer')),
    Category      NVARCHAR(60) NOT NULL,
    Description   NVARCHAR(300) NOT NULL DEFAULT '',
    Amount        DECIMAL(12,2) NOT NULL CHECK(Amount > 0),
    PaidBy        NVARCHAR(60) NOT NULL DEFAULT '',
    WorkerQID     NVARCHAR(20) NOT NULL DEFAULT '',
    ExtCode       NVARCHAR(20) NOT NULL DEFAULT '',
    Status        NVARCHAR(10) NOT NULL DEFAULT 'pending' CHECK(Status IN('pending','approved','cancelled')),
    CancelReason  NVARCHAR(200) NOT NULL DEFAULT '',
    CreatedBy     INT NULL REFERENCES dbo.Users(Id),
    ApprovedBy    INT NULL REFERENCES dbo.Users(Id),
    Created       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_Transactions_Branch_Date_Status ON dbo.Transactions(BranchId, [Date], Status);
GO

CREATE TABLE dbo.Workers(
    QID             NVARCHAR(20) PRIMARY KEY,
    FullName        NVARCHAR(80) NOT NULL,
    Nationality     NVARCHAR(40) NOT NULL DEFAULT '',
    BranchId        INT NOT NULL REFERENCES dbo.Branches(Id),
    RPExpiry        DATE NULL,
    PassportNo      NVARCHAR(20) NOT NULL DEFAULT '',
    PassportExpiry  DATE NULL,
    Mobile          NVARCHAR(20) NOT NULL DEFAULT '',
    Fee             DECIMAL(12,2) NOT NULL DEFAULT 0,
    EmploymentStatus NVARCHAR(20) NOT NULL DEFAULT 'Active',
    Created         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.Links(
    Id        INT IDENTITY PRIMARY KEY,
    Name      NVARCHAR(80) NOT NULL,
    Url       NVARCHAR(500) NOT NULL,
    Notes     NVARCHAR(200) NOT NULL DEFAULT '',
    SortOrder INT NOT NULL DEFAULT 0,
    CreatedBy INT NULL REFERENCES dbo.Users(Id),
    Created   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.ActivityLog(
    Id       INT IDENTITY PRIMARY KEY,
    Ts       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UserId   INT NULL,
    Action   NVARCHAR(100) NOT NULL,
    Detail   NVARCHAR(400) NOT NULL DEFAULT '',
    Ip       NVARCHAR(60) NOT NULL DEFAULT '',
    Ua       NVARCHAR(300) NOT NULL DEFAULT '',
    Ok       BIT NOT NULL DEFAULT 1
);
GO

CREATE TABLE dbo.AuditLog(
    Id     INT IDENTITY PRIMARY KEY,
    Ts     DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UserId INT NULL,
    Tbl    NVARCHAR(40) NOT NULL,
    RecId  NVARCHAR(40) NOT NULL,
    OldVal NVARCHAR(MAX) NULL,
    NewVal NVARCHAR(MAX) NULL
);
GO

CREATE TABLE dbo.Settings(
    [Key]   NVARCHAR(60) PRIMARY KEY,
    [Value] NVARCHAR(400) NOT NULL
);
GO

CREATE TABLE dbo.WorkerPayments(
    Id          INT IDENTITY PRIMARY KEY,
    QID         NVARCHAR(20) NOT NULL REFERENCES dbo.Workers(QID),
    PaymentDate DATE NOT NULL,
    Amount      DECIMAL(12,2) NOT NULL CHECK(Amount > 0),
    Method      NVARCHAR(20) NOT NULL DEFAULT 'Cash',
    ReceivedBy  NVARCHAR(60) NOT NULL DEFAULT '',
    ReferenceNo NVARCHAR(30) NOT NULL DEFAULT '',
    Remarks     NVARCHAR(200) NOT NULL DEFAULT '',
    CreatedBy   INT NULL REFERENCES dbo.Users(Id),
    Created     DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE INDEX IX_WorkerPayments_QID ON dbo.WorkerPayments(QID);
GO

CREATE TABLE dbo.ExternalContacts(
    Code        NVARCHAR(20) PRIMARY KEY,
    Name        NVARCHAR(80) NOT NULL,
    ContactType NVARCHAR(30) NOT NULL DEFAULT 'External Employee',
    Company     NVARCHAR(80) NOT NULL DEFAULT '',
    Position    NVARCHAR(60) NOT NULL DEFAULT '',
    IdNo        NVARCHAR(30) NOT NULL DEFAULT '',
    Mobile      NVARCHAR(20) NOT NULL DEFAULT '',
    Email       NVARCHAR(80) NOT NULL DEFAULT '',
    BranchId    INT NULL REFERENCES dbo.Branches(Id),
    [Status]    NVARCHAR(20) NOT NULL DEFAULT 'Active',
    Remarks     NVARCHAR(200) NOT NULL DEFAULT '',
    CreatedBy   INT NULL REFERENCES dbo.Users(Id),
    Created     DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.Attachments(
    Id          INT IDENTITY PRIMARY KEY,
    Section     NVARCHAR(30) NOT NULL,   -- 'worker', 'tx', 'external_contact', 'company_document' ...
    RecordId    NVARCHAR(40) NOT NULL,   -- QID, Transaction Id, Contact Code, etc. (stored as text)
    FileName    NVARCHAR(200) NOT NULL,
    StoredName  NVARCHAR(200) NOT NULL,  -- random name actually saved on disk
    ContentType NVARCHAR(100) NOT NULL DEFAULT 'application/octet-stream',
    SizeBytes   BIGINT NOT NULL DEFAULT 0,
    Note        NVARCHAR(200) NOT NULL DEFAULT '',
    UploadedBy  INT NULL REFERENCES dbo.Users(Id),
    Uploaded    DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    Deleted     BIT NOT NULL DEFAULT 0
);
GO
CREATE INDEX IX_Attachments_Section_Record ON dbo.Attachments(Section, RecordId);
GO

CREATE TABLE dbo.Customers(
    Id INT IDENTITY PRIMARY KEY, Name NVARCHAR(80) NOT NULL, Company NVARCHAR(80) NOT NULL DEFAULT '',
    Phone NVARCHAR(20) NOT NULL DEFAULT '', Email NVARCHAR(80) NOT NULL DEFAULT '', Address NVARCHAR(200) NOT NULL DEFAULT '',
    BranchId INT NULL REFERENCES dbo.Branches(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TABLE dbo.Suppliers(
    Id INT IDENTITY PRIMARY KEY, Name NVARCHAR(80) NOT NULL, Company NVARCHAR(80) NOT NULL DEFAULT '',
    Phone NVARCHAR(20) NOT NULL DEFAULT '', Email NVARCHAR(80) NOT NULL DEFAULT '', Address NVARCHAR(200) NOT NULL DEFAULT '',
    BranchId INT NULL REFERENCES dbo.Branches(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TABLE dbo.Products(
    Id INT IDENTITY PRIMARY KEY, Sku NVARCHAR(40) NOT NULL UNIQUE, Name NVARCHAR(100) NOT NULL,
    Category NVARCHAR(60) NOT NULL DEFAULT '', Unit NVARCHAR(20) NOT NULL DEFAULT 'pcs',
    CostPrice DECIMAL(12,2) NOT NULL DEFAULT 0, SalePrice DECIMAL(12,2) NOT NULL DEFAULT 0,
    StockQty DECIMAL(12,2) NOT NULL DEFAULT 0, ReorderLevel DECIMAL(12,2) NOT NULL DEFAULT 0,
    BranchId INT NULL REFERENCES dbo.Branches(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TABLE dbo.StockMovements(
    Id INT IDENTITY PRIMARY KEY, ProductId INT NOT NULL REFERENCES dbo.Products(Id), BranchId INT NULL REFERENCES dbo.Branches(Id),
    MoveType NVARCHAR(10) NOT NULL CHECK(MoveType IN('in','out','adjust')), Qty DECIMAL(12,2) NOT NULL,
    RefType NVARCHAR(20) NOT NULL DEFAULT '', RefId NVARCHAR(20) NOT NULL DEFAULT '', MoveDate DATE NOT NULL,
    Note NVARCHAR(200) NOT NULL DEFAULT '', CreatedBy INT NULL REFERENCES dbo.Users(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TABLE dbo.Estimates(
    Id INT IDENTITY PRIMARY KEY, EstimateNo NVARCHAR(20) NULL, CustomerId INT NOT NULL REFERENCES dbo.Customers(Id),
    BranchId INT NOT NULL REFERENCES dbo.Branches(Id), EstDate DATE NOT NULL, ExpiryDate DATE NULL,
    [Status] NVARCHAR(12) NOT NULL DEFAULT 'draft' CHECK([Status] IN('draft','sent','accepted','declined','converted')),
    Subtotal DECIMAL(12,2) NOT NULL DEFAULT 0, Discount DECIMAL(12,2) NOT NULL DEFAULT 0, Tax DECIMAL(12,2) NOT NULL DEFAULT 0,
    Total DECIMAL(12,2) NOT NULL DEFAULT 0, Notes NVARCHAR(300) NOT NULL DEFAULT '',
    CreatedBy INT NULL REFERENCES dbo.Users(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TABLE dbo.EstimateItems(
    Id INT IDENTITY PRIMARY KEY, EstimateId INT NOT NULL REFERENCES dbo.Estimates(Id), ProductId INT NULL REFERENCES dbo.Products(Id),
    Description NVARCHAR(200) NOT NULL, Qty DECIMAL(12,2) NOT NULL, UnitPrice DECIMAL(12,2) NOT NULL, LineTotal DECIMAL(12,2) NOT NULL
);
GO
CREATE TABLE dbo.Invoices(
    Id INT IDENTITY PRIMARY KEY, InvoiceNo NVARCHAR(20) NULL, CustomerId INT NOT NULL REFERENCES dbo.Customers(Id),
    BranchId INT NOT NULL REFERENCES dbo.Branches(Id), InvDate DATE NOT NULL, DueDate DATE NOT NULL,
    [Status] NVARCHAR(12) NOT NULL DEFAULT 'sent' CHECK([Status] IN('draft','sent','partial','paid','overdue','cancelled')),
    Subtotal DECIMAL(12,2) NOT NULL DEFAULT 0, Discount DECIMAL(12,2) NOT NULL DEFAULT 0, Tax DECIMAL(12,2) NOT NULL DEFAULT 0,
    Total DECIMAL(12,2) NOT NULL DEFAULT 0, Notes NVARCHAR(300) NOT NULL DEFAULT '',
    CreatedBy INT NULL REFERENCES dbo.Users(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TABLE dbo.InvoiceItems(
    Id INT IDENTITY PRIMARY KEY, InvoiceId INT NOT NULL REFERENCES dbo.Invoices(Id), ProductId INT NULL REFERENCES dbo.Products(Id),
    Description NVARCHAR(200) NOT NULL, Qty DECIMAL(12,2) NOT NULL, UnitPrice DECIMAL(12,2) NOT NULL, LineTotal DECIMAL(12,2) NOT NULL
);
GO
CREATE TABLE dbo.InvoicePayments(
    Id INT IDENTITY PRIMARY KEY, InvoiceId INT NOT NULL REFERENCES dbo.Invoices(Id), PayDate DATE NOT NULL,
    Amount DECIMAL(12,2) NOT NULL CHECK(Amount>0), Method NVARCHAR(20) NOT NULL DEFAULT 'Cash',
    ReferenceNo NVARCHAR(30) NOT NULL DEFAULT '', ReceivedBy NVARCHAR(60) NOT NULL DEFAULT '',
    TxId INT NULL REFERENCES dbo.Transactions(Id), CreatedBy INT NULL REFERENCES dbo.Users(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TABLE dbo.Bills(
    Id INT IDENTITY PRIMARY KEY, BillNo NVARCHAR(20) NULL, SupplierId INT NOT NULL REFERENCES dbo.Suppliers(Id),
    BranchId INT NOT NULL REFERENCES dbo.Branches(Id), BillDate DATE NOT NULL, DueDate DATE NOT NULL,
    [Status] NVARCHAR(12) NOT NULL DEFAULT 'open' CHECK([Status] IN('open','partial','paid','overdue','cancelled')),
    Total DECIMAL(12,2) NOT NULL CHECK(Total>0), Notes NVARCHAR(300) NOT NULL DEFAULT '',
    CreatedBy INT NULL REFERENCES dbo.Users(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE TABLE dbo.BillPayments(
    Id INT IDENTITY PRIMARY KEY, BillId INT NOT NULL REFERENCES dbo.Bills(Id), PayDate DATE NOT NULL,
    Amount DECIMAL(12,2) NOT NULL CHECK(Amount>0), Method NVARCHAR(20) NOT NULL DEFAULT 'Cash',
    ReferenceNo NVARCHAR(30) NOT NULL DEFAULT '', PaidBy NVARCHAR(60) NOT NULL DEFAULT '',
    TxId INT NULL REFERENCES dbo.Transactions(Id), CreatedBy INT NULL REFERENCES dbo.Users(Id), Created DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

/* Nobody, including the app, may edit or delete the two log tables. */
CREATE TRIGGER trg_ActivityLog_NoChange ON dbo.ActivityLog
INSTEAD OF UPDATE, DELETE AS
BEGIN
    RAISERROR('The activity log cannot be changed or deleted.', 16, 1);
    ROLLBACK TRANSACTION;
END;
GO
CREATE TRIGGER trg_AuditLog_NoChange ON dbo.AuditLog
INSTEAD OF UPDATE, DELETE AS
BEGIN
    RAISERROR('The audit log cannot be changed or deleted.', 16, 1);
    ROLLBACK TRANSACTION;
END;
GO

/* ---------------------- Starter data ---------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.Branches)
BEGIN
    INSERT INTO dbo.Branches(Name) VALUES ('Branch 24'), ('Branch 96'), ('Branch 98');
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Settings WHERE [Key]='company_name')
    INSERT INTO dbo.Settings VALUES ('company_name','Mountain Trading & Contracting W.L.L');
IF NOT EXISTS (SELECT 1 FROM dbo.Settings WHERE [Key]='license_end')
    INSERT INTO dbo.Settings VALUES ('license_end', CONVERT(NVARCHAR(10), DATEADD(YEAR,1,GETDATE()), 23));
IF NOT EXISTS (SELECT 1 FROM dbo.Settings WHERE [Key]='closed_through')
    INSERT INTO dbo.Settings VALUES ('closed_through','0000-00');
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Links)
BEGIN
    INSERT INTO dbo.Links(Name, Url, Notes, SortOrder) VALUES
    ('MOI Single Window', 'https://www.moi.gov.qa/site/english/single_window.html', 'QID / RP renewal and services', 1),
    ('Ministry of Labour', 'https://www.mol.gov.qa/', 'Work permits and contracts', 2);
END
GO

/* The Super Admin login is created by the application on its first run
   (see App/README.md), not here, so the password is never stored in
   plain text in this script. */

PRINT 'MountainApp database created. Now set up the C# application (see App folder).';
