using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Serilog;

namespace AutoDispatchService.Services;

/// <summary>
/// Shadow mode processor — polls the database for work and calls the AutoDispatch web API.
///
/// Two jobs:
/// 1. Find unscored active jobs (ClosestCourierScoredAt is null) → call POST /api/v1/dispatch/suggest/{jobId}
/// 2. Find newly-assigned jobs (courier assigned, no dispatch decision recorded) → call POST /api/v1/dispatch/observe
/// </summary>
public class ShadowModeProcessor
{
    private readonly ShadowModeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public ShadowModeProcessor(ShadowModeOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    public async Task ProcessAsync()
    {
        // Check dispatch mode first — skip if Off
        var mode = await GetDispatchModeAsync();
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug("Dispatch mode is Off — skipping");
            return;
        }

        await ScoreUnscoredJobsAsync();
        await ObserveAssignmentsAsync();
    }

    /// <summary>
    /// Find active jobs that haven't been scored by the engine and call the suggest endpoint.
    /// </summary>
    private async Task ScoreUnscoredJobsAsync()
    {
        var jobIds = new List<int>();

        await using var conn = new SqlConnection(_options.SqlConnection);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 50 ucjbID
            FROM tucJob WITH (NOLOCK)
            WHERE ucjbJobDone = 0
              AND ucjbVoid = 0
              AND ucjbDate >= CAST(GETDATE() AS DATE)
              AND ucjbDate < CAST(DATEADD(day, 1, GETDATE()) AS DATE)
              AND ClosestCourierScoredAt IS NULL
              AND PickUpLatitude IS NOT NULL
              AND PickUpLongitude IS NOT NULL
              AND DeliveryLatitude IS NOT NULL
              AND DeliveryLongitude IS NOT NULL
            ORDER BY ucjbID DESC";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            jobIds.Add(reader.GetInt32(0));
        }

        if (jobIds.Count == 0)
        {
            Log.Debug("No unscored jobs found");
            return;
        }

        Log.Information("Found {Count} unscored jobs to score", jobIds.Count);

        var client = _httpClientFactory.CreateClient("AutoDispatchApi");

        foreach (var jobId in jobIds)
        {
            try
            {
                var response = await client.GetAsync($"/api/v1/dispatch/suggest/{jobId}");
                if (response.IsSuccessStatusCode)
                {
                    Log.Debug("Scored job {JobId}", jobId);
                }
                else
                {
                    Log.Warning("Failed to score job {JobId}: {StatusCode}", jobId, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error scoring job {JobId}", jobId);
            }
        }
    }

    /// <summary>
    /// Find jobs that have been assigned a courier and scored by the engine,
    /// but don't have a dispatch decision recorded yet. Call the observe endpoint.
    /// </summary>
    private async Task ObserveAssignmentsAsync()
    {
        var assignments = new List<(int jobId, int courierId, int? dispatcherId)>();

        await using var conn = new SqlConnection(_options.SqlConnection);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 50 j.ucjbID, j.ucjbCourierID, j.ucjbDispID
            FROM tucJob j WITH (NOLOCK)
            WHERE j.ucjbJobDone = 0
              AND j.ucjbVoid = 0
              AND j.ucjbDate >= CAST(GETDATE() AS DATE)
              AND j.ucjbDate < CAST(DATEADD(day, 1, GETDATE()) AS DATE)
              AND j.ucjbCourierID IS NOT NULL
              AND j.ucjbCourierID > 0
              AND j.ClosestCourierScoredAt IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM Dispatch_Decision d
                  WHERE d.JobId = j.ucjbID
              )
            ORDER BY j.ucjbID DESC";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            assignments.Add((
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2)
            ));
        }

        if (assignments.Count == 0)
        {
            Log.Debug("No unobserved assignments found");
            return;
        }

        Log.Information("Found {Count} assignments to observe", assignments.Count);

        var client = _httpClientFactory.CreateClient("AutoDispatchApi");

        foreach (var (jobId, courierId, dispatcherId) in assignments)
        {
            try
            {
                var response = await client.PostAsJsonAsync("/api/v1/dispatch/observe", new
                {
                    jobId,
                    courierId,
                    dispatcherId
                });

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ObserveResult>();
                    Log.Information("Observed job {JobId} → courier {CourierId} (engine agreed: {Agreed})",
                        jobId, courierId, result?.EngineAgreed);
                }
                else
                {
                    Log.Warning("Failed to observe job {JobId}: {StatusCode}", jobId, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error observing job {JobId}", jobId);
            }
        }
    }

    /// <summary>
    /// Read the current dispatch mode from Dispatch_Config.
    /// </summary>
    private async Task<string> GetDispatchModeAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_options.SqlConnection);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 Mode FROM Dispatch_Config";

            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? "off";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read dispatch mode — defaulting to off");
            return "off";
        }
    }

    private record ObserveResult(bool EngineAgreed, int? EngineTopPick, double? EngineTopScore, int? ChosenCourierRank);
}
