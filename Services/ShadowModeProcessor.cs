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
        // Check dispatch mode first. Fail-safe: only proceed for an explicitly recognised ACTIVE mode.
        // Anything else — "off", null, empty, "0", or an unexpected value — skips, so a misconfigured
        // or unreadable config can never silently leave the paid HERE Maps calls running.
        var mode = await GetDispatchModeAsync();
        if (!IsActiveMode(mode))
        {
            Log.Debug("Dispatch mode '{Mode}' is not an active mode — skipping (no API calls)", mode);
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
        // Only jobs whose speed/job-type is explicitly opted in (tucJobType.AutoDispatchEnabled = 1)
        // are scored — this is the primary cost guard that keeps non-opted jobs out of the paid
        // HERE Maps pool. The INNER JOIN excludes everything that hasn't been ticked on in Admin Manager.
        cmd.CommandText = @"
            SELECT TOP 50 j.ucjbID
            FROM tucJob j WITH (NOLOCK)
            INNER JOIN tucJobType jt WITH (NOLOCK)
                ON jt.ucjtID = j.ucjbSpeed
               AND jt.AutoDispatchEnabled = 1
            WHERE j.ucjbJobDone = 0
              AND j.ucjbVoid = 0
              AND j.ucjbDate >= CAST(GETDATE() AS DATE)
              AND j.ucjbDate < CAST(DATEADD(day, 1, GETDATE()) AS DATE)
              AND CAST(j.ucjbTime AS TIME) BETWEEN
                  CAST(DATEADD(minute, -15, GETDATE()) AS TIME) AND
                  CAST(DATEADD(hour, 2, GETDATE()) AS TIME)
              AND j.ClosestCourierScoredAt IS NULL
              AND j.PickUpLatitude IS NOT NULL
              AND j.PickUpLongitude IS NOT NULL
              AND j.DeliveryLatitude IS NOT NULL
              AND j.DeliveryLongitude IS NOT NULL
            ORDER BY j.ucjbID DESC";

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
              AND CAST(j.ucjbTime AS TIME) BETWEEN
                  CAST(DATEADD(minute, -15, GETDATE()) AS TIME) AND
                  CAST(DATEADD(hour, 2, GETDATE()) AS TIME)
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

    private static readonly HashSet<string> ActiveModes =
        new(StringComparer.OrdinalIgnoreCase) { "shadow", "suggest", "assist", "auto" };

    /// <summary>Active modes are the only ones that may trigger paid HERE Maps calls.</summary>
    private static bool IsActiveMode(string? mode) =>
        !string.IsNullOrWhiteSpace(mode) && ActiveModes.Contains(mode.Trim());

    /// <summary>
    /// Read the current dispatch mode from Dispatch_Config. Reads the most recently updated row
    /// deterministically (the old TOP 1 with no ORDER BY returned an arbitrary row when the table
    /// had more than one), and defaults to "off" on any error so failures fail safe.
    /// </summary>
    private async Task<string> GetDispatchModeAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_options.SqlConnection);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP 1 Mode FROM Dispatch_Config ORDER BY LastUpdated DESC, ConfigId DESC";

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
