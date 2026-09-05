using AiReceptionist.SuperAdmin.Models;

namespace AiReceptionist.SuperAdmin.Common;

/// <summary>Formatting shared by the views, kept out of Razor so the same status never gets two
/// different labels on two different screens.</summary>
public static class Display
{
    public static string Money(decimal amount, string currency) => $"{amount:N2} {currency}";

    public static string Duration(int seconds) =>
        seconds <= 0 ? "—" : seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";

    public static string When(DateTime? utc) => utc is null ? "—" : $"{utc:d MMM yyyy, HH:mm} UTC";

    public static string Day(DateTime? utc) => utc is null ? "—" : $"{utc:d MMM yyyy}";

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "never";
        var span = DateTime.UtcNow - utc.Value;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} d ago";
        return $"{utc:d MMM yyyy}";
    }

    public static string BillingLabel(OrganizationRow org) => org.BillingState switch
    {
        BillingState.NotConfigured => "No subscription",
        BillingState.Trial => "Trial",
        BillingState.Paid => "Paid",
        BillingState.DueSoon => "Due",
        _ => "Overdue",
    };

    public static string BillingChip(OrganizationRow org) => org.BillingState switch
    {
        BillingState.NotConfigured => "chip",
        BillingState.Trial => "chip accent",
        BillingState.Paid => "chip ok",
        BillingState.DueSoon => "chip warn",
        _ => "chip danger",
    };

    /// <summary>What the trial is doing, in one line — or null for an organization that was never
    /// given one, so a caller can leave the row alone rather than print "no trial" everywhere.</summary>
    public static string? TrialDetail(OrganizationRow org)
    {
        if (org.TrialEndsAt is null) return null;

        return org.IsOnTrial
            ? $"Free trial ends {org.TrialEndsAt:d MMM yyyy} ({org.TrialDaysRemaining} d left)"
            : $"Free trial ended {org.TrialEndsAt:d MMM yyyy}";
    }

    public static string BillingDetail(OrganizationRow org)
    {
        // A running trial is why this account owes nothing, so it is the honest headline whether or
        // not a subscription sits behind it.
        if (org.IsOnTrial) return TrialDetail(org)!;
        if (!org.HasSubscription) return org.TrialExpired ? TrialDetail(org)! : "Not billed yet";

        var end = org.CurrentPeriodEnd!.Value;
        var days = (int)Math.Floor((end - DateTime.UtcNow).TotalDays);
        return org.BillingState switch
        {
            BillingState.Paid => $"Renews {end:d MMM yyyy} ({days} d)",
            BillingState.DueSoon => $"Due since {end:d MMM yyyy} — grace ends {org.PaidThrough:d MMM yyyy}",
            _ => $"Overdue since {end:d MMM yyyy} ({Math.Abs(days)} d)",
        };
    }

    public static string CallStatusChip(string status) => status switch
    {
        "Missed" => "chip danger",
        "Transferred" => "chip warn",
        _ => "chip ok",
    };

    public static string AppointmentChip(string status) => status switch
    {
        "Cancelled" => "chip danger",
        "Missed" => "chip warn",
        "Completed" => "chip ok",
        _ => "chip accent",
    };
}
