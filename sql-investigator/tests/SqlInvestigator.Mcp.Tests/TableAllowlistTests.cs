using SqlInvestigator.Mcp.Sql;

namespace SqlInvestigator.Mcp.Tests;

public class TableAllowlistTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" , ")]
    public void Empty_setting_turns_feature_off(string? value)
    {
        Assert.Null(TableAllowlist.Parse(value));
    }

    [Theory]
    [InlineData("Orders")]
    [InlineData("a.b.c")]
    [InlineData("sales.")]
    public void Malformed_entry_throws(string value)
    {
        Assert.Throws<ArgumentException>(() => TableAllowlist.Parse(value));
    }

    [Fact]
    public void Matches_exact_tables_and_schema_wildcards_case_insensitively()
    {
        var allowlist = TableAllowlist.Parse("sales.*, [dbo].[Customers]")!;

        Assert.True(allowlist.IsAllowed("sales", "Orders"));
        Assert.True(allowlist.IsAllowed("SALES", "anything"));
        Assert.True(allowlist.IsAllowed("dbo", "customers"));
        Assert.False(allowlist.IsAllowed("dbo", "Products"));
    }

    [Fact]
    public void Unqualified_names_are_treated_as_dbo()
    {
        var allowlist = TableAllowlist.Parse("dbo.Customers")!;

        Assert.True(allowlist.IsAllowed(null, "Customers"));
        Assert.False(allowlist.IsAllowed(null, "Products"));
    }

    [Fact]
    public void Catalog_schemas_are_always_allowed()
    {
        var allowlist = TableAllowlist.Parse("sales.Orders")!;

        Assert.True(allowlist.IsAllowed("sys", "tables"));
        Assert.True(allowlist.IsAllowed("INFORMATION_SCHEMA", "COLUMNS"));
    }
}
