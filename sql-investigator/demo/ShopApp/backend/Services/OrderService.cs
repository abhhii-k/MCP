// Demo backend for the Step 10 investigation (illustrative; not compiled).
using System.Data;
using Microsoft.Data.SqlClient;

namespace ShopApp.Api.Services;

public sealed record OrderItemDto(int ProductId, string Name, int Quantity, decimal UnitPrice);

public sealed record OrderDto(int OrderId, string Status, decimal? TotalAmount, IReadOnlyList<OrderItemDto> Items);

public sealed class OrderService(SqlConnection connection)
{
    public async Task<OrderDto?> GetOrderAsync(int orderId, CancellationToken ct)
    {
        await connection.OpenAsync(ct);

        await using var cmd = new SqlCommand("""
            SELECT OrderId, Status, TotalAmount FROM sales.Orders WHERE OrderId = @id;
            SELECT oi.ProductId, p.Name, oi.Quantity, oi.UnitPrice
            FROM sales.OrderItems oi JOIN dbo.Products p ON p.ProductId = oi.ProductId
            WHERE oi.OrderId = @id;
            """, connection);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = orderId;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var (id, status, total) = (reader.GetInt32(0), reader.GetString(1),
            reader.IsDBNull(2) ? (decimal?)null : reader.GetDecimal(2));

        await reader.NextResultAsync(ct);
        var items = new List<OrderItemDto>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new OrderItemDto(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetDecimal(3)));
        }

        // The total is stored on the order; the API returns it as-is.
        return new OrderDto(id, status, total, items);
    }

    public async Task AddItemAsync(int orderId, int productId, int quantity, decimal unitPrice, CancellationToken ct)
    {
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        await using (var insert = new SqlCommand("""
            INSERT sales.OrderItems (OrderId, ProductId, Quantity, UnitPrice)
            VALUES (@orderId, @productId, @quantity, @unitPrice);
            """, connection, tx))
        {
            insert.Parameters.Add("@orderId", SqlDbType.Int).Value = orderId;
            insert.Parameters.Add("@productId", SqlDbType.Int).Value = productId;
            insert.Parameters.Add("@quantity", SqlDbType.Int).Value = quantity;
            insert.Parameters.Add("@unitPrice", SqlDbType.Decimal).Value = unitPrice;
            await insert.ExecuteNonQueryAsync(ct);
        }

        // Keep Orders.TotalAmount in sync with the items.
        await using (var recalc = new SqlCommand("sales.usp_RecalculateOrderTotal", connection, tx))
        {
            recalc.CommandType = CommandType.StoredProcedure;
            recalc.Parameters.Add("@OrderId", SqlDbType.Int).Value = orderId;
            await recalc.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}
