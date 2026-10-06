using SqlInvestigator.Mcp.Sql;

namespace SqlInvestigator.Mcp.Tests;

public class SelectValidatorAllowlistTests
{
    private static readonly TableAllowlist Allowlist = TableAllowlist.Parse("sales.*")!;

    [Theory]
    [InlineData("SELECT OrderId FROM sales.Orders")]
    [InlineData("SELECT o.OrderId FROM sales.Orders o JOIN sales.OrderItems i ON i.OrderId = o.OrderId")]
    [InlineData("WITH recent AS (SELECT OrderId FROM sales.Orders) SELECT * FROM recent")]   // CTE is not a table
    [InlineData("SELECT name FROM sys.tables")]
    [InlineData("SELECT 1 AS no_table")]
    public void Allows_queries_on_allowed_tables(string sql)
    {
        Assert.Null(SelectValidator.Validate(sql, Allowlist));
    }

    [Theory]
    [InlineData("SELECT Email FROM dbo.Customers", "dbo.Customers")]
    [InlineData("SELECT Email FROM Customers", "dbo.Customers")]                            // unqualified -> dbo
    [InlineData("SELECT o.OrderId FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId", "dbo.Customers")]
    [InlineData("SELECT * FROM sales.Orders WHERE CustomerId IN (SELECT CustomerId FROM dbo.Customers)", "dbo.Customers")]
    [InlineData("SELECT * FROM OtherDb.sales.Orders", "Database-qualified")]
    public void Blocks_tables_outside_the_allowlist(string sql, string expectedFragment)
    {
        var reason = SelectValidator.Validate(sql, Allowlist);

        Assert.NotNull(reason);
        Assert.Contains(expectedFragment, reason);
    }

    [Fact]
    public void Rejection_message_lists_allowed_patterns_so_the_llm_can_adapt()
    {
        var reason = SelectValidator.Validate("SELECT * FROM dbo.Products", Allowlist);

        Assert.Contains("allowed: sales.*", reason);
    }

    [Fact]
    public void Without_allowlist_any_table_is_allowed()
    {
        Assert.Null(SelectValidator.Validate("SELECT Email FROM dbo.Customers", allowlist: null));
    }
}
