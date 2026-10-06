/*
  One-time setup for the LOCAL test database used while building the MCP server.
  Run manually against LocalDB ONLY:
      sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i db\01-create-testdb.sql

  This script WRITES (creates a DB, tables, data). It is NOT part of the MCP server,
  which is strictly read-only.
*/
SET NOCOUNT ON;
IF DB_ID('McpTestDb') IS NOT NULL
BEGIN
    ALTER DATABASE McpTestDb SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE McpTestDb;
END
GO
CREATE DATABASE McpTestDb;
GO
-- LocalDB/Express default AUTO_CLOSE ON: the DB closes when the last connection ends and
-- its cached plans are discarded, which would leave slow_queries (Step 8) with nothing to show.
ALTER DATABASE McpTestDb SET AUTO_CLOSE OFF;
GO
USE McpTestDb;
GO

-- ---------- Schema ----------
CREATE SCHEMA sales;
GO
CREATE TABLE dbo.Customers (
    CustomerId   INT IDENTITY PRIMARY KEY,
    Email        NVARCHAR(200) NOT NULL UNIQUE,
    FullName     NVARCHAR(200) NOT NULL,
    Country      CHAR(2)       NOT NULL,
    CreatedAtUtc DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE dbo.Products (
    ProductId INT IDENTITY PRIMARY KEY,
    Sku       VARCHAR(30)   NOT NULL UNIQUE,
    Name      NVARCHAR(200) NOT NULL,
    Price     DECIMAL(10,2) NOT NULL CHECK (Price >= 0),
    IsActive  BIT           NOT NULL DEFAULT 1
);
CREATE TABLE sales.Orders (
    OrderId      INT IDENTITY PRIMARY KEY,
    CustomerId   INT          NOT NULL REFERENCES dbo.Customers(CustomerId),
    Status       VARCHAR(20)  NOT NULL DEFAULT 'Pending',
    TotalAmount  DECIMAL(12,2) NULL,
    CreatedAtUtc DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Orders_CustomerId ON sales.Orders(CustomerId) INCLUDE (Status);
CREATE TABLE sales.OrderItems (
    OrderItemId INT IDENTITY PRIMARY KEY,
    OrderId     INT NOT NULL REFERENCES sales.Orders(OrderId),
    ProductId   INT NOT NULL REFERENCES dbo.Products(ProductId),
    Quantity    INT NOT NULL CHECK (Quantity > 0),
    UnitPrice   DECIMAL(10,2) NOT NULL
);
CREATE TABLE dbo.AuditLog (
    AuditId   BIGINT IDENTITY PRIMARY KEY,
    TableName SYSNAME       NOT NULL,
    Action    VARCHAR(10)   NOT NULL,
    Details   NVARCHAR(MAX) NULL,
    AtUtc     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ---------- Code objects (used in Step 6) ----------
CREATE VIEW sales.vw_OrderSummary AS
SELECT o.OrderId, c.Email, o.Status, o.TotalAmount, o.CreatedAtUtc,
       COUNT(oi.OrderItemId) AS ItemCount
FROM sales.Orders o
JOIN dbo.Customers c ON c.CustomerId = o.CustomerId
LEFT JOIN sales.OrderItems oi ON oi.OrderId = o.OrderId
GROUP BY o.OrderId, c.Email, o.Status, o.TotalAmount, o.CreatedAtUtc;
GO
CREATE PROCEDURE sales.usp_RecalculateOrderTotal @OrderId INT AS
BEGIN
    SET NOCOUNT ON;
    -- Known bug for the Step 10 investigation: ignores Quantity!
    UPDATE sales.Orders
    SET TotalAmount = (SELECT SUM(UnitPrice) FROM sales.OrderItems WHERE OrderId = @OrderId)
    WHERE OrderId = @OrderId;
END
GO
CREATE TRIGGER sales.trg_Orders_Audit ON sales.Orders AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.AuditLog (TableName, Action, Details)
    SELECT 'sales.Orders', 'UPDATE', CONCAT('OrderId=', i.OrderId, ' Status=', i.Status)
    FROM inserted i;
END
GO

-- ---------- Sample data ----------
INSERT dbo.Customers (Email, FullName, Country) VALUES
 ('anna@example.com',  N'Anna Schmidt', 'DE'),
 ('raj@example.com',   N'Raj Patel',    'IN'),
 ('maria@example.com', N'Maria Rossi',  'IT'),
 ('john@example.com',  N'John Smith',   'US');
INSERT dbo.Products (Sku, Name, Price) VALUES
 ('COF-001', N'Espresso Beans 1kg', 24.50),
 ('COF-002', N'Decaf Beans 500g',   12.00),
 ('TEA-001', N'Green Tea 100 bags',  8.75),
 ('MUG-001', N'Ceramic Mug',         9.99);
INSERT sales.Orders (CustomerId, Status) VALUES (1,'Paid'),(2,'Pending'),(3,'Paid'),(1,'Shipped');
INSERT sales.OrderItems (OrderId, ProductId, Quantity, UnitPrice) VALUES
 (1,1,2,24.50),(1,4,1,9.99),(2,3,3,8.75),(3,2,1,12.00),(4,1,1,24.50),(4,3,2,8.75);
EXEC sales.usp_RecalculateOrderTotal 1;
EXEC sales.usp_RecalculateOrderTotal 2;
EXEC sales.usp_RecalculateOrderTotal 3;
EXEC sales.usp_RecalculateOrderTotal 4;
GO

-- ---------- Read-only principal for the MCP server (layer 4) ----------
-- A database user with NO login: nobody can sign in as it. Our MCP server
-- switches into it with EXECUTE AS USER, so even a powerful Windows account
-- only has these rights inside MCP sessions.
CREATE USER mcp_reader WITHOUT LOGIN;
ALTER ROLE db_datareader ADD MEMBER mcp_reader;   -- SELECT on all tables/views
GRANT VIEW DEFINITION     TO mcp_reader;          -- read proc/view source (Step 6)
GRANT VIEW DATABASE STATE TO mcp_reader;          -- some DMVs (Step 8)
-- Belt and braces: explicit DENY wins over any GRANT from another role.
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER, CREATE TABLE, CREATE PROCEDURE,
     CREATE VIEW, CREATE FUNCTION TO mcp_reader;
GO
PRINT 'McpTestDb created with sample data and read-only user mcp_reader.';
