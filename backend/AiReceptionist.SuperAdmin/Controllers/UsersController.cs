using System.ComponentModel.DataAnnotations;
using AiReceptionist.SuperAdmin.Data.Repositories;
using AiReceptionist.SuperAdmin.Models;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.SuperAdmin.Controllers;

/// <summary>
/// Sign-in accounts for tenants: who can log in to which organization, and how a new one is
/// issued.
///
/// Tenants register themselves, which mints exactly one account — the first admin. Two ordinary
/// situations leave that insufficient: a customer sold over the phone who should find their login
/// waiting for them, and an organization whose only admin has left. Both are handled here, and
/// both are the same act, which is why creating the organization is a step inside creating the
/// account rather than a separate errand.
/// </summary>
[Route("users")]
public class UsersController : PlatformControllerBase
{
    /// <summary>Matches the tenant API's own registration rule, so a password accepted here is
    /// one the customer could have chosen themselves.</summary>
    private const int MinimumPasswordLength = 8;

    private readonly IUserAccountRepository _accounts;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUserAccountRepository accounts, ILogger<UsersController> logger)
    {
        _accounts = accounts;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? search, int? orgId)
    {
        return View(new UserAccountListViewModel
        {
            Accounts = await _accounts.ListAsync(search, orgId),
            Search = search,
            Organization = orgId is > 0 ? await _accounts.FindOrganizationAsync(orgId.Value) : null,
        });
    }

    /// <summary>The form. <paramref name="orgId"/> preselects an organization, so "add someone to
    /// this customer" from the organization screen arrives with the answer already filled in.</summary>
    [HttpGet("new")]
    public async Task<IActionResult> Create(int? orgId)
    {
        var organizations = await _accounts.ListOrganizationOptionsAsync();

        return View(new NewAccountViewModel
        {
            Organizations = organizations,
            Input = new NewAccountInput
            {
                OrganizationId = orgId,
                // With nothing to attach to, offering a picker of nothing is a dead end.
                OrganizationMode = organizations.Count == 0
                    ? NewAccountInput.ModeNew
                    : NewAccountInput.ModeExisting,
            },
        });
    }

    [HttpPost("new")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(NewAccountInput input)
    {
        input.Email = input.Email?.Trim().ToLowerInvariant() ?? "";
        input.FullName = input.FullName?.Trim() ?? "";
        input.OrganizationName = input.OrganizationName?.Trim();

        var errors = await ValidateAsync(input);
        if (errors.Count > 0)
            return View(new NewAccountViewModel
            {
                Input = input,
                Organizations = await _accounts.ListOrganizationOptionsAsync(),
                Errors = errors,
            });

        var account = new NewAccount
        {
            FullName = input.FullName,
            Email = input.Email,
            Role = input.Role,
            // Work factor left at the library default, which is what the tenant API uses. Pinning a
            // number here would silently diverge from it the day either side is tuned.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.Password),
            OrganizationId = input.IsNewOrganization ? null : input.OrganizationId,
            Organization = input.IsNewOrganization
                ? new NewOrganization
                {
                    Name = input.OrganizationName!,
                    Industry = input.OrganizationIndustry?.Trim() ?? "",
                    Phone = Blank(input.OrganizationPhone),
                    Email = Blank(input.OrganizationEmail) ?? input.Email,
                }
                : null,
        };

        NewAccountResult created;
        try
        {
            created = await _accounts.CreateAsync(account);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // The address was taken between the check above and the insert. The unique index is
            // what actually holds; this turns it into the same sentence the check would have.
            errors["Email"] = "That email address is already in use.";
            return View(new NewAccountViewModel
            {
                Input = input,
                Organizations = await _accounts.ListOrganizationOptionsAsync(),
                Errors = errors,
            });
        }
        catch (InvalidOperationException ex)
        {
            errors["OrganizationId"] = ex.Message;
            return View(new NewAccountViewModel
            {
                Input = input,
                Organizations = await _accounts.ListOrganizationOptionsAsync(),
                Errors = errors,
            });
        }

        // The password is deliberately absent from this line. It is written once, on the screen the
        // operator is about to see, and nowhere that persists.
        _logger.LogInformation(
            "Super admin {UserId} created {Role} account {Email} for organization {OrgId} ({OrgName}){NewOrg}.",
            CurrentUserId, account.Role, account.Email, created.OrganizationId, created.OrganizationName,
            input.IsNewOrganization ? ", which it also created" : "");

        Notify(input.IsNewOrganization
            ? $"Created {created.OrganizationName} and a {AccountRoles.Label(account.Role).ToLowerInvariant()} " +
              $"login for {account.Email}. They can sign in now."
            : $"Added a {AccountRoles.Label(account.Role).ToLowerInvariant()} login for {account.Email} " +
              $"to {created.OrganizationName}. They can sign in now.");

        return RedirectToAction(nameof(Index), new { orgId = created.OrganizationId });
    }

    /// <summary>
    /// Everything wrong with the submission at once, keyed by field.
    ///
    /// All of it, not the first failure: an operator setting up a customer should not discover a
    /// bad password only after fixing the email and submitting again.
    /// </summary>
    private async Task<Dictionary<string, string>> ValidateAsync(NewAccountInput input)
    {
        var errors = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(input.FullName))
            errors["FullName"] = "Enter the person's name.";
        else if (input.FullName.Length > 200)
            errors["FullName"] = "That name is too long (200 characters at most).";

        if (string.IsNullOrWhiteSpace(input.Email))
            errors["Email"] = "Enter an email address — it is what they sign in with.";
        else if (!new EmailAddressAttribute().IsValid(input.Email) || input.Email.Length > 256)
            errors["Email"] = "That does not look like an email address.";
        else if (await _accounts.EmailExistsAsync(input.Email))
            // Said plainly. This console is only reachable by the platform operator, so there is no
            // account-enumeration concern to weigh against telling them what is actually wrong.
            errors["Email"] = "That email address already has an account. Every login must have its own.";

        if (string.IsNullOrWhiteSpace(input.Password))
            errors["Password"] = "Set a password for them.";
        else if (input.Password.Length < MinimumPasswordLength)
            errors["Password"] = $"Use at least {MinimumPasswordLength} characters.";
        else if (input.Password.Length > 100)
            errors["Password"] = "That password is too long (100 characters at most).";
        else if (!string.Equals(input.Password, input.ConfirmPassword, StringComparison.Ordinal))
            errors["ConfirmPassword"] = "The two passwords do not match.";

        if (!AccountRoles.IsAssignable(input.Role))
            errors["Role"] = "Choose one of the roles listed.";

        if (input.IsNewOrganization)
        {
            if (string.IsNullOrWhiteSpace(input.OrganizationName))
                errors["OrganizationName"] = "Give the new organization a name.";
            else if (input.OrganizationName.Length > 200)
                errors["OrganizationName"] = "That name is too long (200 characters at most).";

            if (!string.IsNullOrWhiteSpace(input.OrganizationEmail) &&
                !new EmailAddressAttribute().IsValid(input.OrganizationEmail))
                errors["OrganizationEmail"] = "That does not look like an email address.";
        }
        else if (input.OrganizationId is not > 0)
        {
            errors["OrganizationId"] = "Choose the organization this login belongs to.";
        }
        else if (await _accounts.FindOrganizationAsync(input.OrganizationId.Value) is null)
        {
            errors["OrganizationId"] = "That organization no longer exists.";
        }

        return errors;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
