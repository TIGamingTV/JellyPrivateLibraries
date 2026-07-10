using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.Services;

/// <summary>
/// Listens for newly added library items and applies any pending Jellyseerr
/// grants to them in real time (as soon as the requested media is imported).
/// </summary>
public class ItemAddedListener : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly RestrictionManager _restrictionManager;
    private readonly ILogger<ItemAddedListener> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemAddedListener"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="restrictionManager">The restriction manager.</param>
    /// <param name="logger">The logger.</param>
    public ItemAddedListener(
        ILibraryManager libraryManager,
        RestrictionManager restrictionManager,
        ILogger<ItemAddedListener> logger)
    {
        _libraryManager = libraryManager;
        _restrictionManager = restrictionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (item is null)
        {
            return;
        }

        // Fire-and-forget: item events are synchronous, tagging is async.
        _ = Task.Run(async () =>
        {
            try
            {
                await _restrictionManager.OnItemAddedAsync(item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply pending grants to added item {Item}", item.Name);
            }
        });
    }
}
