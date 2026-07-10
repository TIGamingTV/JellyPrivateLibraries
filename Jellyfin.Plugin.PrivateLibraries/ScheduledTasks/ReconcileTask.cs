using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PrivateLibraries.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.ScheduledTasks;

/// <summary>
/// Periodically reconciles user policies and item tags with the plugin
/// configuration. This also (re)applies grants whose media has since been
/// imported and repairs tags removed by metadata refreshes.
/// </summary>
public class ReconcileTask : IScheduledTask
{
    private readonly RestrictionManager _restrictionManager;
    private readonly ILogger<ReconcileTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconcileTask"/> class.
    /// </summary>
    /// <param name="restrictionManager">The restriction manager.</param>
    /// <param name="logger">The logger.</param>
    public ReconcileTask(RestrictionManager restrictionManager, ILogger<ReconcileTask> logger)
    {
        _restrictionManager = restrictionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Reconcile Private Libraries";

    /// <inheritdoc />
    public string Key => "PrivateLibrariesReconcile";

    /// <inheritdoc />
    public string Description => "Syncs per-user allowed-tags policies and applies media grants for the Private Libraries plugin.";

    /// <inheritdoc />
    public string Category => "Private Libraries";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Private Libraries reconcile task starting");
        await _restrictionManager.ReconcileAllAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Private Libraries reconcile task finished");
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(30).Ticks
        };
    }
}
