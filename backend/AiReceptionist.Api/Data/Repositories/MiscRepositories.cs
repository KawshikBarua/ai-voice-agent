using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

// ---------- Calls ----------

public interface ICallRepository
{
    Task<PagedResult<CallLog>> ListAsync(int orgId, int page, int pageSize);
    Task<CallLog?> GetAsync(int orgId, int id);

    /// <summary>Records a call. Returns 0 when this Retell call is already on record — the
    /// existence check callers make first is a read, so two deliveries of the same webhook can both
    /// pass it; the unique index is what actually decides, and losing that race is not an error.</summary>
    Task<int> CreateAsync(CallLog call);
    Task<bool> UpdateAnalysisAsync(int orgId, string retellCallId, string summary);
    Task<bool> ExistsByRetellIdAsync(int orgId, string retellCallId);
}

public class CallRepository : ICallRepository
{
    private readonly IDbConnectionFactory _db;
    public CallRepository(IDbConnectionFactory db) => _db = db;

    public async Task<PagedResult<CallLog>> ListAsync(int orgId, int page, int pageSize)
    {
        using var conn = _db.Create();
        var p = new { orgId, skip = (page - 1) * pageSize, take = pageSize };
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM CallLogs WHERE OrganizationId=@orgId AND IsDeleted=0", p);
        var items = await conn.QueryAsync<CallLog>(@"
            SELECT cl.*, c.Name AS CustomerName FROM CallLogs cl
            LEFT JOIN Customers c ON c.Id = cl.CustomerId
            WHERE cl.OrganizationId=@orgId AND cl.IsDeleted=0
            ORDER BY cl.StartedAt DESC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY", p);
        return new PagedResult<CallLog> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<CallLog?> GetAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<CallLog>(@"
            SELECT cl.*, c.Name AS CustomerName FROM CallLogs cl
            LEFT JOIN Customers c ON c.Id = cl.CustomerId
            WHERE cl.OrganizationId=@orgId AND cl.Id=@id AND cl.IsDeleted=0", new { orgId, id });
    }

    public async Task<int> CreateAsync(CallLog call)
    {
        using var conn = _db.Create();
        try
        {
            return await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO CallLogs (OrganizationId, CustomerId, RetellCallId, FromNumber, Direction, Status, DurationSeconds, Transcript, RecordingUrl, Summary, StartedAt)
                OUTPUT INSERTED.Id
                VALUES (@OrganizationId, @CustomerId, @RetellCallId, @FromNumber, @Direction, @Status, @DurationSeconds, @Transcript, @RecordingUrl, @Summary, @StartedAt)", call);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // UX_CallLogs_Org_RetellCallId: a concurrent delivery of the same webhook got there
            // first. That is the index doing its job, not a fault — the call is on record either
            // way, and the caller must not turn this into a 500 that has Retell redeliver forever.
            return 0;
        }
    }

    public async Task<bool> ExistsByRetellIdAsync(int orgId, string retellCallId)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM CallLogs
            WHERE OrganizationId=@orgId AND RetellCallId=@retellCallId AND IsDeleted=0",
            new { orgId, retellCallId }) > 0;
    }

    public async Task<bool> UpdateAnalysisAsync(int orgId, string retellCallId, string summary)
    {
        using var conn = _db.Create();
        var rows = await conn.ExecuteAsync(@"
            UPDATE CallLogs SET Summary=@summary
            WHERE OrganizationId=@orgId AND RetellCallId=@retellCallId AND IsDeleted=0",
            new { orgId, retellCallId, summary });
        return rows > 0;
    }
}

// ---------- Knowledge Base ----------

public interface IKnowledgeRepository
{
    Task<IEnumerable<KnowledgeBaseEntry>> ListAsync(int orgId);
    Task<int> CreateAsync(KnowledgeBaseEntry entry);
    Task UpdateAsync(KnowledgeBaseEntry entry);
    Task DeleteAsync(int orgId, int id);
}

public class KnowledgeRepository : IKnowledgeRepository
{
    private readonly IDbConnectionFactory _db;
    public KnowledgeRepository(IDbConnectionFactory db) => _db = db;

    public async Task<IEnumerable<KnowledgeBaseEntry>> ListAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<KnowledgeBaseEntry>(
            "SELECT * FROM KnowledgeBase WHERE OrganizationId=@orgId AND IsDeleted=0 ORDER BY SortOrder, Id", new { orgId });
    }

    public async Task<int> CreateAsync(KnowledgeBaseEntry e)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO KnowledgeBase (OrganizationId, Category, Title, Content, SortOrder)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @Category, @Title, @Content, @SortOrder)", e);
    }

    public async Task UpdateAsync(KnowledgeBaseEntry e)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE KnowledgeBase SET Category=@Category, Title=@Title, Content=@Content, SortOrder=@SortOrder, ModifiedAt=GETUTCDATE()
            WHERE OrganizationId=@OrganizationId AND Id=@Id AND IsDeleted=0", e);
    }

    public async Task DeleteAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("UPDATE KnowledgeBase SET IsDeleted=1 WHERE OrganizationId=@orgId AND Id=@id", new { orgId, id });
    }
}

// ---------- Holidays ----------

public interface IHolidayRepository
{
    Task<IEnumerable<Holiday>> ListAsync(int orgId);
    /// <summary>Closures from <paramref name="fromLocalDate"/> onwards — what the AI needs;
    /// last year's holidays would only waste prompt tokens.</summary>
    Task<IEnumerable<Holiday>> ListUpcomingAsync(int orgId, DateTime fromLocalDate, int take = 40);
    Task<Holiday?> FindAsync(int orgId, DateTime localDate);
    Task<int> UpsertAsync(Holiday holiday);
    Task DeleteAsync(int orgId, int id);
}

public class HolidayRepository : IHolidayRepository
{
    private readonly IDbConnectionFactory _db;
    public HolidayRepository(IDbConnectionFactory db) => _db = db;

    public async Task<IEnumerable<Holiday>> ListAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<Holiday>(
            "SELECT * FROM Holidays WHERE OrganizationId=@orgId AND IsDeleted=0 ORDER BY [Date]", new { orgId });
    }

    public async Task<IEnumerable<Holiday>> ListUpcomingAsync(int orgId, DateTime fromLocalDate, int take = 40)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<Holiday>(@"
            SELECT TOP (@take) * FROM Holidays
            WHERE OrganizationId=@orgId AND IsDeleted=0 AND [Date] >= @from
            ORDER BY [Date]", new { orgId, from = fromLocalDate.Date, take });
    }

    public async Task<Holiday?> FindAsync(int orgId, DateTime localDate)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<Holiday>(
            "SELECT * FROM Holidays WHERE OrganizationId=@orgId AND [Date]=@date AND IsDeleted=0",
            new { orgId, date = localDate.Date });
    }

    /// <summary>Adding a date that is already marked renames it rather than failing. A settings
    /// screen that 500s on a double-submit would be worse than silently agreeing.</summary>
    public async Task<int> UpsertAsync(Holiday h)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            MERGE Holidays AS t
            USING (SELECT @OrganizationId AS OrganizationId, @Date AS [Date]) AS s
                ON t.OrganizationId = s.OrganizationId AND t.[Date] = s.[Date] AND t.IsDeleted = 0
            WHEN MATCHED THEN UPDATE SET Name = @Name
            WHEN NOT MATCHED THEN INSERT (OrganizationId, [Date], Name)
                VALUES (@OrganizationId, @Date, @Name)
            OUTPUT INSERTED.Id;",
            new { h.OrganizationId, Date = h.Date.Date, h.Name });
    }

    public async Task DeleteAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync("UPDATE Holidays SET IsDeleted=1 WHERE OrganizationId=@orgId AND Id=@id",
            new { orgId, id });
    }
}

// ---------- Organization / Agent settings ----------

public interface ISettingsRepository
{
    Task<Organization?> GetOrganizationAsync(int orgId);
    Task UpdateOrganizationAsync(Organization org);
    Task<AgentConfig?> GetAgentConfigAsync(int orgId);
    Task<AgentConfig?> GetAgentConfigByRetellAgentIdAsync(string retellAgentId);
    Task<int?> FindOrganizationByRetellPhoneNumberAsync(string phoneNumber, int excludeOrgId);
    Task UpsertAgentConfigAsync(AgentConfig config);
    Task<IEnumerable<int>> GetConnectedOrganizationIdsAsync();
}

public class SettingsRepository : ISettingsRepository
{
    private readonly IDbConnectionFactory _db;
    public SettingsRepository(IDbConnectionFactory db) => _db = db;

    public async Task<Organization?> GetOrganizationAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<Organization>(
            "SELECT * FROM Organizations WHERE Id=@orgId AND IsDeleted=0", new { orgId });
    }

    public async Task UpdateOrganizationAsync(Organization org)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE Organizations SET Name=@Name, Industry=@Industry, Logo=@Logo, Address=@Address, Phone=@Phone,
                Email=@Email, Currency=@Currency, TaxRate=@TaxRate, Timezone=@Timezone,
                BusinessHoursJson=@BusinessHoursJson, MaxConcurrentAppointments=@MaxConcurrentAppointments,
                ProductsEnabled=@ProductsEnabled, OnboardingCompleted=@OnboardingCompleted
            WHERE Id=@Id AND IsDeleted=0", org);
    }

    public async Task<AgentConfig?> GetAgentConfigAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<AgentConfig>(
            "SELECT * FROM AgentConfig WHERE OrganizationId=@orgId", new { orgId });
    }

    // Hidden (soft-deleted) organizations are excluded: their stale config must never be
    // pushed to Retell, where it would overwrite the agent of whichever live tenant shares
    // the same agent id.
    public async Task<IEnumerable<int>> GetConnectedOrganizationIdsAsync()
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<int>(@"
            SELECT ac.OrganizationId
            FROM AgentConfig ac
            JOIN Organizations o ON o.Id = ac.OrganizationId
            WHERE ac.RetellAgentId IS NOT NULL AND o.IsDeleted = 0");
    }

    public async Task<AgentConfig?> GetAgentConfigByRetellAgentIdAsync(string retellAgentId)
    {
        using var conn = _db.Create();
        // QueryFirstOrDefault, not QuerySingleOrDefault: if two tenants ever end up sharing an
        // agent id the webhook must still resolve a tenant rather than throw and 500 every
        // inbound call event. Live organizations win over hidden ones.
        return await conn.QueryFirstOrDefaultAsync<AgentConfig>(@"
            SELECT TOP 1 ac.*
            FROM AgentConfig ac
            JOIN Organizations o ON o.Id = ac.OrganizationId
            WHERE ac.RetellAgentId = @retellAgentId AND o.IsDeleted = 0
            ORDER BY ac.OrganizationId",
            new { retellAgentId });
    }

    /// <summary>Returns the organization that already claims this Retell number, if any. One
    /// Retell account is shared by every tenant, so a number may only answer for one of them —
    /// otherwise a tenant could point someone else's number at their own agent.
    /// Compares canonical E.164 values (the form every number is stored in).</summary>
    public async Task<int?> FindOrganizationByRetellPhoneNumberAsync(string phoneNumber, int excludeOrgId)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 ac.OrganizationId
            FROM AgentConfig ac
            JOIN Organizations o ON o.Id = ac.OrganizationId
            WHERE ac.RetellPhoneNumber = @phoneNumber
              AND ac.OrganizationId <> @excludeOrgId
              AND o.IsDeleted = 0",
            new { phoneNumber, excludeOrgId });
    }

    public async Task UpsertAgentConfigAsync(AgentConfig c)
    {
        using var conn = _db.Create();

        // Tenants own their greeting. Only when they leave it blank do we fall back to one
        // generated from the organization name, so a business can never end up greeting
        // callers with someone else's name (or with no name at all).
        if (string.IsNullOrWhiteSpace(c.Greeting))
        {
            var orgName = await conn.ExecuteScalarAsync<string?>(
                "SELECT Name FROM Organizations WHERE Id=@OrganizationId AND IsDeleted=0",
                new { c.OrganizationId });
            c.Greeting = GreetingBuilder.For(orgName);
        }
        else
        {
            c.Greeting = c.Greeting.Trim();
        }

        await conn.ExecuteAsync(@"
            MERGE AgentConfig AS t
            USING (SELECT @OrganizationId AS OrganizationId) AS s ON t.OrganizationId = s.OrganizationId
            WHEN MATCHED THEN UPDATE SET Voice=@Voice, Language=@Language, Greeting=@Greeting,
                TransferNumber=@TransferNumber, RetellAgentId=@RetellAgentId, RetellLlmId=@RetellLlmId,
                RetellKnowledgeBaseId=@RetellKnowledgeBaseId,
                DetachedRetellAgentId=@DetachedRetellAgentId, DetachedRetellLlmId=@DetachedRetellLlmId,
                DetachedRetellKnowledgeBaseId=@DetachedRetellKnowledgeBaseId,
                RetellPhoneNumber=@RetellPhoneNumber, LastSyncedAt=@LastSyncedAt, Enabled=@Enabled,
                EnabledToolsJson=@EnabledToolsJson, ModifiedAt=GETUTCDATE()
            WHEN NOT MATCHED THEN INSERT (OrganizationId, Voice, Language, Greeting, TransferNumber,
                RetellAgentId, RetellLlmId, RetellKnowledgeBaseId, DetachedRetellAgentId, DetachedRetellLlmId,
                DetachedRetellKnowledgeBaseId, RetellPhoneNumber, LastSyncedAt, Enabled, EnabledToolsJson)
                VALUES (@OrganizationId, @Voice, @Language, @Greeting, @TransferNumber,
                @RetellAgentId, @RetellLlmId, @RetellKnowledgeBaseId, @DetachedRetellAgentId, @DetachedRetellLlmId,
                @DetachedRetellKnowledgeBaseId, @RetellPhoneNumber, @LastSyncedAt, @Enabled, @EnabledToolsJson);", c);
    }
}

// ---------- Audit ----------

public interface IAuditRepository
{
    Task LogAsync(int orgId, int? userId, string action, string? details = null, string? ip = null);
}

public class AuditRepository : IAuditRepository
{
    private readonly IDbConnectionFactory _db;
    public AuditRepository(IDbConnectionFactory db) => _db = db;

    public async Task LogAsync(int orgId, int? userId, string action, string? details = null, string? ip = null)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            INSERT INTO AuditLogs (OrganizationId, UserId, Action, Details, IpAddress)
            VALUES (@orgId, @userId, @action, @details, @ip)", new { orgId, userId, action, details, ip });
    }
}
