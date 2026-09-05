using System.Text.Json;
using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using PushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace AiReceptionist.Api.Services;

/// <summary>The VAPID pair and the contact address that identify this deployment to the push
/// services. Absent keys are not an error — push is simply off, and the rest of the app is
/// unaffected. See <see cref="VapidKeys"/> for why there is nothing to buy here.</summary>
public class PushOptions
{
    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }
    /// <summary>Where a push service should complain if this deployment misbehaves. Required by
    /// the spec; a mailto: address of whoever runs the server.</summary>
    public string Subject { get; set; } = "mailto:admin@localhost";

    /// <summary>How long an unacknowledged urgent alert waits before it is sent again.</summary>
    public int EscalateAfterMinutes { get; set; } = 5;
    /// <summary>How many times in total an urgent alert is sent. Capped so a business that has
    /// closed for the night is not buzzed until morning about something nobody will action.</summary>
    public int MaxEscalations { get; set; } = 4;

    public bool Configured => VapidKeys.IsValidPublicKey(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);

    /// <summary>
    /// Whether <see cref="Subject"/> is one the push services will actually accept.
    ///
    /// Worth checking separately from the keys, because the services disagree about it. Google's
    /// and Mozilla's take almost anything; Apple validates the VAPID <c>sub</c> claim and rejects
    /// a subject that is not a routable mailto: or https: address — so a placeholder like
    /// <c>mailto:admin@localhost</c> works everywhere except iPhones, which is the hardest version
    /// of this bug to find. The domain must contain a dot for the same reason: <c>localhost</c>
    /// and other bare hostnames are exactly what Apple turns down.
    /// </summary>
    public bool SubjectUsable => IsUsableSubject(Subject);

    public static bool IsUsableSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        var value = subject.Trim();

        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            var address = value["mailto:".Length..];
            var at = address.LastIndexOf('@');
            if (at <= 0) return false;
            var domain = address[(at + 1)..];
            return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var url)
               && url.Scheme == Uri.UriSchemeHttps
               && url.Host.Contains('.');
    }
}

public interface INotificationService
{
    bool Enabled { get; }
    string? PublicKey { get; }

    /// <summary>Records something that happened unattended and tells the org's devices about it.
    /// Urgent alerts stay on the dashboard until acknowledged; see <see cref="Alert"/>.</summary>
    Task<Alert> RaiseAsync(Alert alert);

    /// <summary>Sends one alert to the devices that want it. Used for the first delivery and by
    /// the escalation worker for every repeat.</summary>
    Task DeliverAsync(Alert alert, CancellationToken cancellationToken = default);

    /// <summary>A push to one device, to prove to somebody setting this up that it works.</summary>
    Task<PushTestResult> SendTestAsync(int orgId, int deviceId);
}

/// <summary>
/// What a test push actually did. A bare false was not enough: the two common failures need
/// opposite responses from whoever is standing there — re-granting permission on the device fixes
/// a dead subscription and does nothing at all for a credential the push service refused — and
/// telling someone to do the wrong one costs them the afternoon.
/// </summary>
public record PushTestResult(bool Ok, string Message);

public class NotificationService : INotificationService
{
    private readonly INotificationRepository _notifications;
    private readonly PushServiceClient _push;
    private readonly PushOptions _options;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(INotificationRepository notifications, PushServiceClient push,
        PushOptions options, ILogger<NotificationService> logger)
    {
        _notifications = notifications;
        _push = push;
        _options = options;
        _logger = logger;
    }

    public bool Enabled => _options.Configured;
    public string? PublicKey => _options.PublicKey;

    public async Task<Alert> RaiseAsync(Alert alert)
    {
        alert.Id = await _notifications.CreateAlertAsync(alert);

        // The alert is recorded whether or not it can be delivered. A deployment with no VAPID
        // keys, or an owner who has not turned notifications on yet, still gets the dashboard
        // queue — which is the half that cannot be missed.
        await DeliverAsync(alert);
        return alert;
    }

    public async Task DeliverAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return;

        var urgent = alert.Severity == AlertSeverity.Urgent;
        var devices = (await _notifications.ListTargetsAsync(alert.OrganizationId, urgent)).ToList();
        if (devices.Count == 0) return;

        // What the service worker receives. Everything it needs to render the notification is in
        // here: the worker never calls back to us, so a push still displays correctly on a phone
        // that is offline by the time it is tapped.
        var payload = JsonSerializer.Serialize(new
        {
            id = alert.Id,
            title = alert.Title,
            body = alert.Body,
            url = alert.Url ?? "/",
            severity = alert.Severity,
            kind = alert.Kind,
        });

        var message = new PushMessage(payload)
        {
            // Urgent messages must wake a sleeping phone; routine ones may be held until it is
            // next awake, which is easier on the battery and is what "Normal" means here.
            Urgency = urgent ? PushMessageUrgency.High : PushMessageUrgency.Normal,
            // Collapses repeats of the same alert at the push service, so a phone that was off for
            // an hour shows one emergency notification on waking rather than four.
            Topic = $"alert-{alert.Id}",
            TimeToLive = urgent ? 3600 : 86400,
        };

        var delivered = new List<int>();
        foreach (var device in devices)
        {
            var subscription = new PushSubscription { Endpoint = device.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, device.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, device.Auth);

            try
            {
                await _push.RequestPushMessageDeliveryAsync(subscription, message, cancellationToken);
                delivered.Add(device.Id);
            }
            catch (PushServiceClientException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // The browser has revoked this subscription — the app was uninstalled, or the user
                // cleared site data. It will never work again, so it goes rather than failing on
                // every future alert forever.
                _logger.LogInformation("Push subscription {DeviceId} is gone ({Status}); removing it.",
                    device.Id, ex.StatusCode);
                await _notifications.RemoveDeviceAsync(device.Endpoint);
            }
            catch (Exception ex)
            {
                // One unreachable device must not stop the others being told, and must never fail
                // the booking that raised the alert. The push service is named because these
                // failures cluster by vendor — every Apple endpoint failing while Google's succeed
                // is a VAPID subject problem, not a broken device.
                _logger.LogWarning(ex, "Could not push alert {AlertId} to device {DeviceId} via {PushService}.",
                    alert.Id, device.Id,
                    Uri.TryCreate(device.Endpoint, UriKind.Absolute, out var host) ? host.Host : "unknown");
                await _notifications.RecordDeviceFailureAsync(device.Id);
            }
        }

        await _notifications.MarkDeviceNotifiedAsync(delivered);
        await _notifications.MarkAlertNotifiedAsync(alert.Id);
    }

    public async Task<PushTestResult> SendTestAsync(int orgId, int deviceId)
    {
        if (!Enabled)
            return new PushTestResult(false, "Notifications are not set up on this server yet.");

        var device = (await _notifications.ListDevicesAsync(orgId)).FirstOrDefault(d => d.Id == deviceId);
        if (device is null)
            return new PushTestResult(false, "That device is no longer registered.");

        var subscription = new PushSubscription { Endpoint = device.Endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, device.P256dh);
        subscription.SetKey(PushEncryptionKeyName.Auth, device.Auth);

        var payload = JsonSerializer.Serialize(new
        {
            id = 0,
            title = "Notifications are working",
            body = "This is what a new booking will look like on this device.",
            url = "/",
            severity = AlertSeverity.Info,
            kind = "Test",
        });

        var service = Uri.TryCreate(device.Endpoint, UriKind.Absolute, out var host) ? host.Host : "the push service";

        try
        {
            await _push.RequestPushMessageDeliveryAsync(subscription, new PushMessage(payload));
            return new PushTestResult(true, "Sent — it should appear in a moment.");
        }
        catch (PushServiceClientException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            // The device's end of the arrangement is gone. This is the one the owner can fix.
            _logger.LogInformation("Test push to device {DeviceId} found the subscription gone ({Status}).",
                deviceId, ex.StatusCode);
            return new PushTestResult(false,
                "This device's subscription no longer exists — switch notifications off and on again on it.");
        }
        catch (PushServiceClientException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            // Nothing to do with this device: the push service turned down the deployment's VAPID
            // credential. Apple is the one that does this, and it is nearly always Push:Subject —
            // it insists on a routable mailto: or https: address where Google accepts anything.
            _logger.LogError(ex,
                "{Service} rejected this deployment's VAPID credentials ({Status}) when testing device " +
                "{DeviceId}. Check Push:Subject — it is currently {Subject}.",
                service, ex.StatusCode, deviceId, _options.Subject);
            return new PushTestResult(false,
                $"{service} rejected this server's notification credentials. This is a server setting, " +
                "not a problem with the device — check the Push:Subject value in the API configuration.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test push to device {DeviceId} via {Service} failed.", deviceId, service);
            return new PushTestResult(false, $"Could not reach {service}. Please try again.");
        }
    }

    /// <summary>
    /// Builds the alert for a booking the AI took, and decides how loud it should be.
    ///
    /// The two things that cannot wait are an emergency and anything happening today: both are
    /// useless to learn about tomorrow morning. Everything else is worth knowing but not worth
    /// waking someone for, so it is sent once and never chased.
    /// </summary>
    public static Alert ForBooking(int orgId, Appointment appointment, Organization org,
        string serviceName, string customerName, DateTime startLocal, DateTime nowLocal,
        string? assignedTo)
    {
        var sameDay = startLocal.Date == nowLocal.Date;
        var urgent = appointment.IsEmergency || sameDay;

        var when = sameDay ? $"today at {startLocal:HH:mm}" : $"{startLocal:ddd d MMM} at {startLocal:HH:mm}";
        var lead = appointment.IsEmergency ? "Emergency booking" : sameDay ? "Booked for today" : "New booking";

        return new Alert
        {
            OrganizationId = orgId,
            Kind = AlertKind.AppointmentBooked,
            Severity = urgent ? AlertSeverity.Urgent : AlertSeverity.Info,
            Title = $"{lead} — {when}",
            Body = $"{customerName} · {serviceName}" +
                   (assignedTo is null ? "" : $" · with {assignedTo}") +
                   (string.IsNullOrWhiteSpace(appointment.ServiceAddress) ? "" : $" · {appointment.ServiceAddress}"),
            Url = "/appointments",
            AppointmentId = appointment.Id,
        };
    }
}
