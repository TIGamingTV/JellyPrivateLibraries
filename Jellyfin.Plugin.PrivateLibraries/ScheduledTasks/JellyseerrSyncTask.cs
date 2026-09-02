using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PrivateLibraries.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.ScheduledTasks;

/// <summary>
/// Periodically backfills grants from the requests already stored in Jellyseerr, so requests
/// made before the plugin was installed (or while the webhook was misconfigured) are granted
/// too. No-op unless the Jellyseerr URL and API key are configured.
/// </summary>
public class JellyseerrSyncTask : IScheduledTask
{
    private readonly JellyseerrSyncService _syncService;
    private readonly ILogger<JellyseerrSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyseerrSyncTask"/> class.
    /// </summary>
    /// <param name="syncService">The Jellyseerr sync service.</param>
    /// <param name="logger">The logger.</param>
    public JellyseerrSyncTask(JellyseerrSyncService syncService, ILogger<JellyseerrSyncTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Jellyseerr requests";

    /// <inheritdoc />
    public string Key => "PrivateLibrariesJellyseerrSync";

    /// <inheritdoc />
    public string Description => "Reads existing Jellyseerr requests over its API and grants each requester the media they asked for, including requests made before this plugin was installed.";

    /// <inheritdoc />
    public string Category => "Private Libraries";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.JellyseerrSyncEnabled)
        {
            _logger.LogInformation("Jellyseerr sync is disabled in the plugin configuration; skipping");
            progress.Report(100);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.JellyseerrUrl) || string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            // Expected state for anyone using the webhook only, so this is not a warning.
            _logger.LogInformation("Jellyseerr URL or API key is not configured; skipping Jellyseerr sync");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Jellyseerr sync task starting");
        try
        {
            var result = await _syncService.SyncAsync(progress, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Jellyseerr sync task finished: {Summary}", result.Summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A scheduled task that throws is surfaced as a red failure in the dashboard with
            // no detail; log the reason so the admin can act on it.
            _logger.LogError(ex, "Jellyseerr sync task failed");
            throw;
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No startup trigger: a first sync on a mature Jellyseerr instance can touch a lot of
        // library items, and startup is already busy with the reconcile task.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        };
    }
}
