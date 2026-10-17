using AiReceptionist.Api.Common;
using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

// ---------- Calls ----------

public interface ICallRepository
{
    Task<CursorPage<CallLog>> ListAsync(int orgId, KeysetCursor? after, int pageSize);
    Task<CallLog?> GetAsync(int orgId, int id);

    /// <summary>Records a call. Returns 0 when this Retell call is already on record — the
    /// existence check callers make first is a read, so two deliveries of the same webhook can both
    /// pass it; the unique index is what actually decides, and losing that race is not an error.</summary>
    Task<int> CreateAsync(CallLog call);
    Task<bool> UpdateAnalysisAsync(int orgId, string retellCallId, string summary);
    Task<bool> ExistsByRetellIdAsync(int orgId, string retellCallId);

    /// <summary>What is on record for each of these Retell calls — just enough to tell whether it
    /// still matches what Retell says. Keyed by Retell call id; a missing key means not recorded.</summary>
    Task<IReadOnlyDictionary<string, RecordedCall>> GetRecordedAsync(
        int orgId, IReadOnlyCollection<string> retellCallIds);

    /// <summary>
    /// Brings a call already on record into line with Retell's final reading of it, and says what
    /// its duration was before and after. Null when the call is not on record.
    ///
    /// The duration is only ever replaced by a finished call's real, non-zero length, and never on a
    /// call whose minutes have already been claimed by a closed billing period: an invoice that has
    /// gone out stays exactly as it was issued. Such a call is reported through
    /// <see cref="CallCorrection.LockedByInvoice"/> so the difference can be seen and credited by
    /// hand rather than silently re-billed.
    /// </summary>
    Task<CallCorrection?> ReconcileAsync(int orgId, RetellCallReading fresh);
}

/// <summary>The billing-relevant facts about one recorded call.</summary>
public sealed class RecordedCall
{
    public string RetellCallId { get; init; } = "";
    public int DurationSeconds { get; init; }
    public string Status { get; init; } = "";
    public bool HasTranscript { get; init; }
    public bool HasRecording { get; init; }
    public DateTime? BilledPeriodEnd { get; init; }

    /// <summary>True when Retell's finished reading of this call says something the record does
    /// not: a different length, a different ending, or a transcript or recording it is missing.
    /// An invoiced call's length is not counted as a difference — it cannot be changed here.</summary>
    public bool DiffersFrom(RetellCallReading fresh) =>
        fresh.IsFinished && (
            (BilledPeriodEnd is null && fresh.DurationSeconds > 0 && fresh.DurationSeconds != DurationSeconds)
            || (fresh.Status is { } status && status != Status)
            || (!HasTranscript && fresh.Transcript is not null)
            || (!HasRecording && fresh.RecordingUrl is not null));

    /// <summary>Already invoiced at a length Retell does not agree with.</summary>
    public bool InvoicedAtWrongLength(RetellCallReading fresh) =>
        BilledPeriodEnd is not null && fresh.IsFinished
        && fresh.DurationSeconds > 0 && fresh.DurationSeconds != DurationSeconds;
}

/// <summary>What a <see cref="ICallRepository.ReconcileAsync"/> did to a call's duration.</summary>
public sealed record CallCorrection(int CallLogId, int PreviousSeconds, int CurrentSeconds, DateTime? BilledPeriodEnd)
{
    public bool DurationChanged => PreviousSeconds != CurrentSeconds;

    /// <summary>The call's minutes were already on a closed period, so its duration was kept.</summary>
    public bool LockedByInvoice => BilledPeriodEnd is not null;
}

public class CallRepository : ICallRepository
{
    private readonly IDbConnectionFactory _db;
    public CallRepository(IDbConnectionFactory db) => _db = db;

    // Keyset paging on (StartedAt DESC, Id DESC) — see CustomerRepository.ListAsync for why.
    //
    // The row here is the summary only. Transcript, Summary and RecordingUrl are left out: a page
    // of fifty full transcripts is megabytes nobody reads, and GetAsync supplies them for the one
    // call that is opened. What is left is answered entirely from IX_CallLogs_Org_Started.
    public async Task<CursorPage<CallLog>> ListAsync(int orgId, KeysetCursor? after, int pageSize)
    {
        var p = new DynamicParameters();
        p.Add("orgId", orgId);
        // One more than asked for: its presence is what says another page exists.
        p.Add("take", pageSize + 1);
        p.Add("id", after?.Id, System.Data.DbType.Int32);
        p.Add("at", after?.At, System.Data.DbType.DateTime2);

        using var conn = _db.Create();
        var rows = (await conn.QueryAsync<CallLog>($@"
            SELECT TOP (@take) cl.Id, cl.OrganizationId, cl.CustomerId, cl.FromNumber, cl.Status,
                   cl.DurationSeconds, cl.StartedAt, c.Name AS CustomerName
            FROM CallLogs cl
            LEFT JOIN Customers c ON c.Id = cl.CustomerId
            WHERE cl.OrganizationId=@orgId AND cl.IsDeleted=0
            {(after is null ? "" : "AND cl.StartedAt <= @at AND (cl.StartedAt < @at OR cl.Id < @id)")}
            ORDER BY cl.StartedAt DESC, cl.Id DESC", p)).AsList();

        var items = rows.Take(pageSize).ToList();
        return new CursorPage<CallLog>
        {
            Items = items,
            NextCursor = rows.Count > pageSize ? new KeysetCursor(items[^1].StartedAt, items[^1].Id).ToString() : null,
        };
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
            // IsTrial is decided here, from the organization's trial window as it stands when the
            // call is recorded — the one moment that is known to be right (see DbInitializer).
            return await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO CallLogs (OrganizationId, CustomerId, RetellCallId, FromNumber, Direction, Status, DurationSeconds, Transcript, RecordingUrl, Summary, StartedAt, IsTrial)
                OUTPUT INSERTED.Id
                SELECT @OrganizationId, @CustomerId, @RetellCallId, @FromNumber, @Direction, @Status, @DurationSeconds, @Transcript, @RecordingUrl, @Summary, @StartedAt,
                       CASE WHEN o.TrialEndsAt > @StartedAt AND ISNULL(o.TrialStartedAt, @StartedAt) <= @StartedAt
                            THEN 1 ELSE 0 END
                FROM Organizations o WHERE o.Id = @OrganizationId", call);
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

    public async Task<IReadOnlyDictionary<string, RecordedCall>> GetRecordedAsync(
        int orgId, IReadOnlyCollection<string> retellCallIds)
    {
        var found = new Dictionary<string, RecordedCall>(StringComparer.Ordinal);
        if (retellCallIds.Count == 0) return found;

        using var conn = _db.Create();
        // Chunked well under SQL Server's 2,100-parameter limit, since a deep sync can list
        // a thousand calls in a page.
        foreach (var chunk in retellCallIds.Distinct().Chunk(500))
        {
            var rows = await conn.QueryAsync<RecordedCall>(@"
                SELECT RetellCallId, DurationSeconds, Status,
                       CAST(CASE WHEN Transcript IS NULL THEN 0 ELSE 1 END AS BIT) AS HasTranscript,
                       CAST(CASE WHEN RecordingUrl IS NULL THEN 0 ELSE 1 END AS BIT) AS HasRecording,
                       BilledPeriodEnd
                FROM CallLogs
                WHERE OrganizationId = @orgId AND IsDeleted = 0 AND RetellCallId IN @ids",
                new { orgId, ids = chunk });
            foreach (var row in rows) found[row.RetellCallId] = row;
        }
        return found;
    }

    public async Task<CallCorrection?> ReconcileAsync(int orgId, RetellCallReading fresh)
    {
        if (fresh.CallId is null) return null;

        // Only a finished call's reading is final. Anything earlier would be writing a partial
        // transcript or a length that is still growing over the record.
        var finished = fresh.IsFinished;
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<CallCorrection>(@"
            UPDATE CallLogs SET
                DurationSeconds = CASE WHEN @seconds IS NOT NULL AND BilledPeriodEnd IS NULL
                                       THEN @seconds ELSE DurationSeconds END,
                Status       = COALESCE(@status, Status),
                Transcript   = COALESCE(@transcript, Transcript),
                RecordingUrl = COALESCE(@recordingUrl, RecordingUrl),
                Summary      = COALESCE(@summary, Summary)
            OUTPUT INSERTED.Id AS CallLogId,
                   DELETED.DurationSeconds AS PreviousSeconds,
                   INSERTED.DurationSeconds AS CurrentSeconds,
                   DELETED.BilledPeriodEnd AS BilledPeriodEnd
            WHERE OrganizationId = @orgId AND RetellCallId = @callId AND IsDeleted = 0",
            new
            {
                orgId,
                callId = fresh.CallId,
                seconds = finished && fresh.DurationSeconds > 0 ? fresh.DurationSeconds : (int?)null,
                status = finished ? fresh.Status : null,
                transcript = finished ? fresh.Transcript : null,
                recordingUrl = finished ? fresh.RecordingUrl : null,
                summary = fresh.Summary,
            });
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

    /// <summary>The same row straight from the database. For decisions that must not act on a
    /// copy up to <see cref="CacheTtl.Config"/> old — whether a call may be answered — because
    /// suspensions, stopped agents and trials are also written by the console, which cannot clear
    /// this API's cache.</summary>
    Task<Organization?> GetOrganizationFreshAsync(int orgId);

    Task UpdateOrganizationAsync(Organization org);
    Task<AgentConfig?> GetAgentConfigAsync(int orgId);
    Task<AgentConfig?> GetAgentConfigByRetellAgentIdAsync(string retellAgentId);
    Task<int?> FindOrganizationByRetellPhoneNumberAsync(string phoneNumber, int excludeOrgId);
    Task<bool> AnyOtherOrganizationNamedAsync(int orgId, string name);
    Task UpsertAgentConfigAsync(AgentConfig config);
    Task<IEnumerable<int>> GetConnectedOrganizationIdsAsync();
}

public class SettingsRepository : ISettingsRepository
{
    private readonly IDbConnectionFactory _db;
    private readonly ICache _cache;

    public SettingsRepository(IDbConnectionFactory db, ICache cache)
    {
        _db = db;
        _cache = cache;
    }

    /// <summary>
    /// Cached: this row is read on every live-call tool, every prompt build and every dashboard
    /// load, and written only from the two methods below — which drop the key.
    ///
    /// Suspension is deliberately not read through here (see <c>AuthRepository</c>, which asks for
    /// <c>IsActive</c> directly), so an operator switching an account off in the console still
    /// takes effect on the next request rather than at the end of this entry's life.
    /// </summary>
    public Task<Organization?> GetOrganizationAsync(int orgId) =>
        _cache.GetOrSetAsync(CacheKeys.Org(orgId), CacheTtl.Config, () => GetOrganizationFreshAsync(orgId));

    public async Task<Organization?> GetOrganizationFreshAsync(int orgId)
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

        // Business hours and timezone are read by the booking tools mid-call, so this one cannot
        // be left to expire: the agent would keep quoting the old opening times.
        await _cache.RemoveAsync(CacheKeys.Org(org.Id));
    }

    /// <summary>Cached: read by every webhook, every sync pass and every backfill.</summary>
    public Task<AgentConfig?> GetAgentConfigAsync(int orgId) =>
        _cache.GetOrSetAsync(CacheKeys.Agent(orgId), CacheTtl.Config, async () =>
        {
            using var conn = _db.Create();
            return await conn.QuerySingleOrDefaultAsync<AgentConfig>(
                "SELECT * FROM AgentConfig WHERE OrganizationId=@orgId", new { orgId });
        });

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

    /// <summary>True when any other organization goes by this name. Asked before deleting a Retell
    /// resource that can only be recognised by the name it was given, so two tenants called the
    /// same thing can never clean up each other's content. Hidden organizations count: their
    /// resources may still be on the account.</summary>
    public async Task<bool> AnyOtherOrganizationNamedAsync(int orgId, string name)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM Organizations WHERE Id <> @orgId AND Name = @name",
            new { orgId, name }) > 0;
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

        // Connecting, disconnecting or stopping an agent all land here, and every one of them has
        // to be true for the very next webhook — this is the linkage that decides which tenant a
        // call belongs to.
        await _cache.RemoveAsync(CacheKeys.Agent(c.OrganizationId));
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
