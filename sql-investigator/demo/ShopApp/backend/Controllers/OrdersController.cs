// Demo backend for the Step 10 investigation (illustrative; not compiled).
using Microsoft.AspNetCore.Mvc;
using ShopApp.Api.Services;

namespace ShopApp.Api.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(OrderService orders) : ControllerBase
{
    [HttpGet("{orderId:int}")]
    public async Task<ActionResult<OrderDto>> Get(int orderId, CancellationToken ct) =>
        await orders.GetOrderAsync(orderId, ct) is { } order ? order : NotFound();

    public sealed record AddItemRequest(int ProductId, int Quantity, decimal UnitPrice);

    [HttpPost("{orderId:int}/items")]
    public async Task<IActionResult> AddItem(int orderId, AddItemRequest request, CancellationToken ct)
    {
        await orders.AddItemAsync(orderId, request.ProductId, request.Quantity, request.UnitPrice, ct);
        return NoContent();
    }
}
