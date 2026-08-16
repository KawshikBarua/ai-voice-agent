using AiReceptionist.Api.Domain;
using Dapper;

namespace AiReceptionist.Api.Data.Repositories;

public interface ICallActionSuggestionRepository
{
    /// <summary>Transferred calls (any tenant) whose transcript hasn't been mined yet —
    /// consumed by the background intent worker.</summary>
    Task<IEnumerable<CallLog>> GetTransferredCallsNeedingExtractionAsync(int max);
    Task MarkCallProcessedAsync(int orgId, int callLogId);
    Task<int> CreateAsync(CallActionSuggestion suggestion);
    Task<IEnumerable<CallActionSuggestion>> GetPendingByOrgAsync(int orgId);
    Task<CallActionSuggestion?> GetAsync(int orgId, int id);
    Task ResolveAsync(int orgId, int id, string status, int? resolvedByUserId, int? resultAppointmentId);
}

public class CallActionSuggestionRepository : ICallActionSuggestionRepository
{
    private readonly IDbConnectionFactory _db;
    public CallActionSuggestionRepository(IDbConnectionFactory db) => _db = db;

    public async Task<IEnumerable<CallLog>> GetTransferredCallsNeedingExtractionAsync(int max)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<CallLog>(@"
            SELECT TOP (@max) * FROM CallLogs
            WHERE IsDeleted = 0 AND Status = 'Transferred'
              AND Transcript IS NOT NULL AND LEN(Transcript) > 0
              AND IntentProcessedAt IS NULL
            ORDER BY StartedAt DESC", new { max });
    }

    public async Task MarkCallProcessedAsync(int orgId, int callLogId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(
            "UPDATE CallLogs SET IntentProcessedAt = GETUTCDATE() WHERE OrganizationId = @orgId AND Id = @callLogId",
            new { orgId, callLogId });
    }

    public async Task<int> CreateAsync(CallActionSuggestion s)
    {
        using var conn = _db.Create();
        return await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO CallActionSuggestions
                (OrganizationId, CallLogId, Action, Confidence, CustomerName, Phone, ServiceName, StartAtLocal, Reasoning)
            OUTPUT INSERTED.Id
            VALUES (@OrganizationId, @CallLogId, @Action, @Confidence, @CustomerName, @Phone, @ServiceName, @StartAtLocal, @Reasoning)", s);
    }

    public async Task<IEnumerable<CallActionSuggestion>> GetPendingByOrgAsync(int orgId)
    {
        using var conn = _db.Create();
        return await conn.QueryAsync<CallActionSuggestion>(@"
            SELECT s.*, cl.StartedAt AS CallStartedAt, cl.FromNumber AS FromNumber
            FROM CallActionSuggestions s
            JOIN CallLogs cl ON cl.Id = s.CallLogId
            WHERE s.OrganizationId = @orgId AND s.Status = 'Pending'
            ORDER BY s.CreatedAt DESC", new { orgId });
    }

    public async Task<CallActionSuggestion?> GetAsync(int orgId, int id)
    {
        using var conn = _db.Create();
        return await conn.QuerySingleOrDefaultAsync<CallActionSuggestion>(
            "SELECT * FROM CallActionSuggestions WHERE OrganizationId = @orgId AND Id = @id", new { orgId, id });
    }

    public async Task ResolveAsync(int orgId, int id, string status, int? resolvedByUserId, int? resultAppointmentId)
    {
        using var conn = _db.Create();
        await conn.ExecuteAsync(@"
            UPDATE CallActionSuggestions
            SET Status = @status, ResolvedByUserId = @resolvedByUserId,
                ResultAppointmentId = @resultAppointmentId, ResolvedAt = GETUTCDATE()
            WHERE OrganizationId = @orgId AND Id = @id",
            new { orgId, id, status, resolvedByUserId, resultAppointmentId });
    }
}
