using System.Security.Claims;
using AiReceptionist.Api.Domain;

namespace AiReceptionist.Api.Common;

/// <summary>Resolves the current tenant (OrganizationId) and user from the JWT.
/// Every repository call must be scoped by OrganizationId (SRS §4).</summary>
public interface ITenantProvider
{
    int OrganizationId { get; }
    int UserId { get; }
    string Role { get; }
}

public class TenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _accessor;

    public TenantProvider(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public int OrganizationId =>
        int.TryParse(Principal?.FindFirstValue("orgId"), out var id)
            ? id
            : throw new UnauthorizedAccessException("Missing organization claim.");

    public int UserId =>
        int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public string Role => Principal?.FindFirstValue(ClaimTypes.Role) ?? Roles.ReadOnly;
}
