using Inventory.Api.Contracts;
using Inventory.Api.Data;
using Inventory.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class OrdersController(AppDbContext db, OrderService orders) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Place(PlaceOrderRequest request, CancellationToken ct)
        => ToActionResult(await orders.PlaceOrderAsync(request, ct));

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
        => ToActionResult(await orders.CancelOrderAsync(id, ct));

    [HttpPost("/api/products/{productId:int}/restock")]
    public async Task<IActionResult> Restock(int productId, RestockRequest request, CancellationToken ct)
        => ToActionResult(await orders.RestockAsync(productId, request, ct));

    [HttpGet("recent")]
    public async Task<ActionResult<IReadOnlyList<OrderDto>>> Recent(
        [FromQuery] int status = 0,
        [FromQuery] int days = 7,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        take = Math.Clamp(take, 1, 200);

        var recent = await db.Orders
            .Where(o => (int)o.Status == status && o.OrderDate >= since)
            .OrderByDescending(o => o.OrderDate)
            .Take(take)
            .Select(o => new OrderDto(
                o.Id,
                o.OrderDate,
                o.Status.ToString(),
                o.Lines.Sum(l => l.Quantity * l.UnitPrice),
                o.Lines.Select(l => new OrderLineDto(l.ProductId, l.Product.Name, l.Quantity, l.UnitPrice)).ToList()))
            .ToListAsync(ct);

        return recent;
    }

    private IActionResult ToActionResult(ServiceResult result) => result switch
    {
        ServiceResult.Ok<int> created => Ok(new { id = created.Value }),
        ServiceResult.Ok => NoContent(),
        ServiceResult.Invalid invalid => BadRequest(new { error = invalid.Error }),
        ServiceResult.NotFound notFound => NotFound(new { error = notFound.Error }),
        ServiceResult.Conflict conflict => Conflict(new { error = conflict.Error }),
        _ => throw new InvalidOperationException($"Unhandled service result {result.GetType().Name}.")
    };
}
