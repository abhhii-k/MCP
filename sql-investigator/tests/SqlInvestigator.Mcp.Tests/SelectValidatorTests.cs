using SqlInvestigator.Mcp.Sql;

namespace SqlInvestigator.Mcp.Tests;

/// <summary>Regression tests for layer 2 (the parser). Same cases as the Step 7 manual tests.</summary>
public class SelectValidatorTests
{
    [Theory]
    [InlineData("SELECT TOP 5 OrderId, Status FROM sales.Orders ORDER BY OrderId")]
    [InlineData("WITH c AS (SELECT CustomerId, COUNT(*) AS n FROM sales.Orders GROUP BY CustomerId) " +
                "SELECT cu.Email, c.n FROM c JOIN dbo.Customers cu ON cu.CustomerId = c.CustomerId")]
    [InlineData("SELECT OrderId FROM sales.Orders WHERE Status = 'DELETE'")]          // keyword inside a string
    [InlineData("-- DELETE FROM sales.Orders\nSELECT COUNT(*) FROM sales.Orders")]   // keyword inside a comment
    [InlineData("SELECT 1 UNION ALL SELECT 2")]
    [InlineData("SELECT name FROM sys.tables")]
    public void Allows_plain_selects(string sql)
    {
        Assert.Null(SelectValidator.Validate(sql));
    }

    [Theory]
    [InlineData("DELETE FROM sales.Orders", "Delete statement")]
    [InlineData("SELECT 1; DELETE FROM sales.Orders", "Exactly one statement")]
    [InlineData("WITH x AS (SELECT 1 AS a) DELETE FROM sales.Orders", "Delete statement")]
    [InlineData("SELECT * INTO #copy FROM sales.Orders", "SELECT ... INTO")]
    [InlineData("EXEC('DEL' + 'ETE FROM sales.Orders')", "Execute statement")]
    [InlineData("SELECT 1\nGO\nSELECT 2", "Exactly one statement")]
    [InlineData("UPDATE sales.Orders SET Status = 'X'", "Update statement")]
    [InlineData("DROP TABLE sales.Orders", "DropTable statement")]
    [InlineData("TRUNCATE TABLE sales.Orders", "TruncateTable statement")]
    [InlineData("MERGE sales.Orders AS t USING dbo.Customers AS s ON 1 = 0 WHEN NOT MATCHED THEN INSERT (CustomerId) VALUES (s.CustomerId);", "Merge statement")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'C:\\secret.txt', SINGLE_CLOB) AS x", "OPENROWSET")]
    [InlineData("SELECT * FROM PRODSRV.ShopDb.sales.Orders", "linked servers")]
    [InlineData("SELECT * FROM sales.Orders WITH (UPDLOCK)", "UPDLOCK")]
    [InlineData("WAITFOR DELAY '00:00:10'", "WaitFor statement")]
    [InlineData("SELEC * FORM sales.Orders", "syntax error")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    public void Blocks_unsafe_sql(string sql, string expectedReasonFragment)
    {
        var reason = SelectValidator.Validate(sql);

        Assert.NotNull(reason);
        Assert.Contains(expectedReasonFragment, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blocks_sql_longer_than_limit()
    {
        var sql = "SELECT 1 AS a" + new string(' ', SelectValidator.MaxSqlLength);

        Assert.Contains("longer than", SelectValidator.Validate(sql));
    }
}
