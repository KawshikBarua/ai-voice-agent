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
    private readonly ITenantProvider _tenant;
    private readonly IAuditRepository _audit;
    private readonly IRetellSyncQueue _retellSync;

    public KnowledgeBaseController(IKnowledgeRepository knowledge, IPromptBuilderService prompts,
        ITenantProvider tenant, IAuditRepository audit, IRetellSyncQueue retellSync)
    {
        _knowledge = knowledge;
        _prompts = prompts;
        _tenant = tenant;
        _audit = audit;
        _retellSync = retellSync;
    }

    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(ApiResponse<IEnumerable<KnowledgeBaseEntry>>.Ok(await _knowledge.ListAsync(_tenant.OrganizationId)));

    /// <summary>Read-only preview of what the AI receives (SRS §13): the always-in-context
    /// system prompt, plus the documents uploaded to Retell's knowledge base for on-demand
    /// retrieval. Never editable by clients.</summary>
    [HttpGet("final-prompt")]
    public async Task<IActionResult> FinalPrompt()
    {
        var orgId = _tenant.OrganizationId;
        var prompt = await _prompts.BuildSystemPromptAsync(orgId);
        var docs = await _prompts.BuildKnowledgeDocumentsAsync(orgId);

        return Ok(ApiResponse<object>.Ok(new
        {
            prompt,
            promptCharacters = prompt.Length,
            knowledgeDocuments = docs.Select(d => new { d.Title, d.Text, characters = d.Text.Length }),
            knowledgeCharacters = docs.Sum(d => d.Text.Length),
        }));
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
