using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

[ApiController]
[Route("api/v1/customers")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly ICustomerRepository _customers;
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;

    public CustomersController(ICustomerRepository customers, ITenantProvider tenant, IAuditRepository audit)
    {
        _customers = customers;
        _tenant = tenant;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(ApiResponse<PagedResult<Customer>>.Ok(
            await _customers.ListAsync(_tenant.OrganizationId, search, page, Math.Clamp(pageSize, 1, 100))));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var customer = await _customers.GetAsync(_tenant.OrganizationId, id);
        return customer is null
            ? NotFound(ApiResponse<object>.Fail("Customer not found."))
            : Ok(ApiResponse<Customer>.Ok(customer));
    }

    [HttpGet("{id:int}/timeline")]
    public async Task<IActionResult> Timeline(int id) =>
        Ok(ApiResponse<IEnumerable<TimelineEvent>>.Ok(
            await _customers.TimelineAsync(_tenant.OrganizationId, id)));

    [HttpPost]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Create(Customer customer)
    {
        customer.OrganizationId = _tenant.OrganizationId;
        var id = await _customers.CreateAsync(customer);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "CustomerCreated", $"CustomerId={id}");
        customer.Id = id;
        return Ok(ApiResponse<Customer>.Ok(customer, "Customer created successfully."));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Update(int id, Customer customer)
    {
        customer.Id = id;
        customer.OrganizationId = _tenant.OrganizationId;
        await _customers.UpdateAsync(customer);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "CustomerUpdated", $"CustomerId={id}");
        return Ok(ApiResponse<Customer>.Ok(customer, "Customer updated successfully."));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Delete(int id)
    {
        await _customers.SoftDeleteAsync(_tenant.OrganizationId, id);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "CustomerDeleted", $"CustomerId={id}");
        return Ok(ApiResponse<object>.Ok(new { }, "Customer deleted."));
    }
}
