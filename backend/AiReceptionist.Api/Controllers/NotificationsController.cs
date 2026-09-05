using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

/// <summary>What a browser sends after the user grants permission — the three opaque strings that
/// make up a Web Push subscription.</summary>
/// <summary>What a browser sends after the user grants permission — the three opaque strings
/// that make up a Web Push subscription. <c>UrgentOnly</c> is nullable because the page re-sends
/// this on every load to keep the keys fresh, and that must not quietly reset a preference the
/// owner set on this device months ago.</summary>
public record SubscribeRequest(string Endpoint, string P256dh, string Auth, string? Label, bool? UrgentOnly);

/// <summary>
/// Browser notifications, and the queue of things nobody has looked at yet.
///
/// The problem this solves: an owner does not sit in the dashboard, so a booking taken at 2am —
/// or an emergency taken at any hour — is invisible until they next happen to sign in. Web Push
/// reaches the phone with the app closed, and costs nothing to send.
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationRepository _notifications;
    private readonly INotificationService _push;
    private readonly ITenantProvider _tenant;

    public NotificationsController(INotificationRepository notifications, INotificationService push,
        ITenantProvider tenant)
    {
        _notifications = notifications;
        _push = push;
        _tenant = tenant;
    }

    /// <summary>What the browser needs before it can subscribe: the deployment's public VAPID key.
    /// It is public by design — it is handed to every browser that subscribes.</summary>
    [HttpGet("config")]
    public IActionResult Config() =>
        Ok(ApiResponse<object>.Ok(new { enabled = _push.Enabled, publicKey = _push.PublicKey }));

    /// <summary>Registers this browser. Called on every load, so it must stay idempotent — the
    /// endpoint is the identity, and re-subscribing the same browser yields the same one.</summary>
    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(SubscribeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Endpoint) ||
            string.IsNullOrWhiteSpace(request.P256dh) || string.IsNullOrWhiteSpace(request.Auth))
            return BadRequest(ApiResponse<object>.Fail("That is not a usable push subscription."));

        // Endpoints are push-service URLs, never our own. Storing anything else would have this
        // server making requests to an address a client chose for it.
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
            return BadRequest(ApiResponse<object>.Fail("A push endpoint must be an https URL."));

        var id = await _notifications.UpsertDeviceAsync(new PushDevice
        {
            OrganizationId = _tenant.OrganizationId,
            UserId = _tenant.UserId,
            Endpoint = request.Endpoint,
            P256dh = request.P256dh,
            Auth = request.Auth,
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim()[..Math.Min(200, request.Label.Trim().Length)],
        }, request.UrgentOnly);

        return Ok(ApiResponse<object>.Ok(new { id }, "This device will be notified."));
    }

    /// <summary>Turns this browser off. The browser has usually already dropped its subscription
    /// by the time this arrives, which is why an unknown endpoint is a success rather than a 404.</summary>
    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(SubscribeRequest request)
    {
        await _notifications.RemoveDeviceAsync(request.Endpoint);
        return Ok(ApiResponse<object>.Ok(new { }, "This device will no longer be notified."));
    }

    /// <summary>The devices registered for this organization, so somebody can see what is being
    /// notified and switch off a phone they no longer carry.</summary>
    [HttpGet("devices")]
    public async Task<IActionResult> Devices() =>
        Ok(ApiResponse<IEnumerable<PushDevice>>.Ok(await _notifications.ListDevicesAsync(_tenant.OrganizationId)));

    [HttpDelete("devices/{id:int}")]
    public async Task<IActionResult> RemoveDevice(int id)
    {
        await _notifications.RemoveDeviceAsync(_tenant.OrganizationId, id);
        return Ok(ApiResponse<object>.Ok(new { }, "Device removed."));
    }

    /// <summary>Proves the whole chain works while someone is standing there to see it. Setting up
    /// notifications and finding out weeks later that they never arrived is the failure worth
    /// designing against.</summary>
    [HttpPost("devices/{id:int}/test")]
    public async Task<IActionResult> Test(int id)
    {
        // The service says what went wrong, because "switch it off and on again" is the wrong
        // advice for half of these and the person following it has no way to tell which half.
        var result = await _push.SendTestAsync(_tenant.OrganizationId, id);
        return result.Ok
            ? Ok(ApiResponse<object>.Ok(new { }, result.Message))
            : BadRequest(ApiResponse<object>.Fail(result.Message));
    }

    // ---------- the queue ----------

    /// <summary>What has happened that nobody has looked at. <paramref name="pendingOnly"/> false
    /// includes what has already been dealt with, for the history behind the list.</summary>
    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts([FromQuery] bool pendingOnly = true, [FromQuery] int take = 50) =>
        Ok(ApiResponse<IEnumerable<Alert>>.Ok(
            await _notifications.ListAlertsAsync(_tenant.OrganizationId, pendingOnly, Math.Clamp(take, 1, 100))));

    /// <summary>Marks one alert as seen, which is what stops an urgent one being re-sent.</summary>
    [HttpPost("alerts/{id:int}/acknowledge")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> Acknowledge(int id)
    {
        await _notifications.AcknowledgeAsync(_tenant.OrganizationId, id, _tenant.UserId);
        return Ok(ApiResponse<object>.Ok(new { }, "Marked as seen."));
    }

    [HttpPost("alerts/acknowledge-all")]
    [Authorize(Roles = Roles.Staff)]
    public async Task<IActionResult> AcknowledgeAll()
    {
        var count = await _notifications.AcknowledgeAllAsync(_tenant.OrganizationId, _tenant.UserId);
        return Ok(ApiResponse<object>.Ok(new { count }, $"{count} marked as seen."));
    }
}
