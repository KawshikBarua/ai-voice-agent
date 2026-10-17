using AiReceptionist.Api.Common;
using AiReceptionist.Api.Data.Repositories;
using AiReceptionist.Api.Domain;
using AiReceptionist.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiReceptionist.Api.Controllers;

[ApiController]
[Route("api/v1/knowledge-base")]
[Authorize]
public class KnowledgeBaseController : ControllerBase
{
    private readonly IKnowledgeRepository _knowledge;
    private readonly IPromptBuilderService _prompts;
    private readonly IKnowledgeLookupService _lookup;
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;
    private readonly IRetellSyncQueue _retellSync;

    public KnowledgeBaseController(IKnowledgeRepository knowledge, IPromptBuilderService prompts,
        IKnowledgeLookupService lookup, ITenantProvider tenant, IAuditRepository audit, IRetellSyncQueue retellSync)
    {
        _knowledge = knowledge;
        _prompts = prompts;
        _lookup = lookup;
        _tenant = tenant;
        _audit = audit;
        _retellSync = retellSync;
    }

    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(ApiResponse<IEnumerable<KnowledgeBaseEntry>>.Ok(await _knowledge.ListAsync(_tenant.OrganizationId)));

    /// <summary>Read-only preview of what the AI receives (SRS §13): the always-in-context
    /// general prompt and each call state's prompt, plus what the lookup_info tool answers from
    /// when a caller asks — grouped the way a person would read it. Never editable by clients.</summary>
    [HttpGet("final-prompt")]
    public async Task<IActionResult> FinalPrompt()
    {
        var orgId = _tenant.OrganizationId;
        var prompt = (await _prompts.BuildAgentPromptAsync(orgId)).Render();
        var items = await _lookup.ListItemsAsync(orgId);

        var docs = items
            .GroupBy(i => i.Kind)
            .Select(g => new
            {
                Title = KindTitle(g.Key),
                Text = string.Join("\n\n", g.Select(i => i.Text)),
            })
            .ToList();

        return Ok(ApiResponse<object>.Ok(new
        {
            prompt,
            promptCharacters = prompt.Length,
            knowledgeDocuments = docs.Select(d => new { d.Title, d.Text, characters = d.Text.Length }),
            knowledgeCharacters = docs.Sum(d => d.Text.Length),
        }));

        static string KindTitle(string kind) => kind switch
        {
            InfoKinds.Service => "Services and pricing",
            InfoKinds.Product => "Product catalogue",
            InfoKinds.Hours => "Opening hours and closures",
            InfoKinds.Faq => "Frequently asked questions",
            InfoKinds.Policy => "Business policies",
            InfoKinds.About => "About the business",
            _ => "Additional information",
        };
    }

    [HttpPost]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Create(KnowledgeBaseEntry entry)
    {
        entry.OrganizationId = _tenant.OrganizationId;
        entry.Id = await _knowledge.CreateAsync(entry);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "KnowledgeBaseUpdated", $"EntryId={entry.Id}");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<KnowledgeBaseEntry>.Ok(entry, "Knowledge base entry created."));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Update(int id, KnowledgeBaseEntry entry)
    {
        entry.Id = id;
        entry.OrganizationId = _tenant.OrganizationId;
        await _knowledge.UpdateAsync(entry);
        await _audit.LogAsync(_tenant.OrganizationId, _tenant.UserId, "KnowledgeBaseUpdated", $"EntryId={id}");
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<KnowledgeBaseEntry>.Ok(entry, "Knowledge base entry updated."));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = Roles.Management)]
    public async Task<IActionResult> Delete(int id)
    {
        await _knowledge.DeleteAsync(_tenant.OrganizationId, id);
        _retellSync.Enqueue(_tenant.OrganizationId);
        return Ok(ApiResponse<object>.Ok(new { }, "Knowledge base entry deleted."));
    }
}
