using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

[ApiController]
[Route("api/v1/services")]
[Authorize]
public class ServicesController : ControllerBase
{
    private readonly ICatalogRepository _catalog;
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;
    private readonly IRetellSyncQueue _retellSync;

    public ServicesController(ICatalogRepository catalog, ITenantProvider tenant, IAuditRepository audit,
        IRetellSyncQueue retellSync)
    {
        _catalog = catalog;
        _tenant = tenant;
        _audit = audit;
        _retellSync = retellSync;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(ApiResponse<PagedResult<Service>>.Ok(
            await _catalog.ListServicesAsync(_tenant.OrganizationId, page, Math.Clamp(pageSize, 1, 200))));

    [HttpPost]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Create(Service service)
    {
        service.OrganizationId = _tenant.OrganizationId;
        service.Id = await _catalog.CreateServiceAsync(service);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "ServiceCreated", $"ServiceId={service.Id}");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<Service>.Ok(service, "Service created successfully."));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Update(int id, Service service)
    {
        service.Id = id;
        service.OrganizationId = _tenant.OrganizationId;
        await _catalog.UpdateServiceAsync(service);
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<Service>.Ok(service, "Service updated successfully."));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Delete(int id)
    {
        await _catalog.DeleteServiceAsync(_tenant.OrganizationId, id);
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<object>.Ok(new { }, "Service deleted."));
    }
}

[ApiController]
[Route("api/v1/products")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly ICatalogRepository _catalog;
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;
    private readonly IRetellSyncQueue _retellSync;

    public ProductsController(ICatalogRepository catalog, ITenantProvider tenant, IAuditRepository audit,
        IRetellSyncQueue retellSync)
    {
        _catalog = catalog;
        _tenant = tenant;
        _audit = audit;
        _retellSync = retellSync;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(ApiResponse<PagedResult<Product>>.Ok(
            await _catalog.ListProductsAsync(_tenant.OrganizationId, search, page, Math.Clamp(pageSize, 1, 200))));

    [HttpPost]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Create(Product product)
    {
        product.OrganizationId = _tenant.OrganizationId;
        product.Id = await _catalog.CreateProductAsync(product);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "ProductCreated", $"ProductId={product.Id}");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<Product>.Ok(product, "Product created successfully."));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Update(int id, Product product)
    {
        product.Id = id;
        product.OrganizationId = _tenant.OrganizationId;
        await _catalog.UpdateProductAsync(product);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "ProductUpdated", $"ProductId={id}");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<Product>.Ok(product, "Product updated successfully."));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Delete(int id)
    {
        await _catalog.DeleteProductAsync(_tenant.OrganizationId, id);
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<object>.Ok(new { }, "Product deleted."));
    }
}
