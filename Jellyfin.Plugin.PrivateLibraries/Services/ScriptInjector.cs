using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.Services;

/// <summary>
/// Injects the widget loader script into the Jellyfin web client's index.html on
/// startup so the home-screen button appears. This follows the same approach used
/// by plugins such as Intro Skipper and JellyScrub.
/// </summary>
public class ScriptInjector : IHostedService
{
    private const string StartMarker = "<!-- PrivateLibraries:begin -->";
    private const string EndMarker = "<!-- PrivateLibraries:end -->";
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<ScriptInjector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjector"/> class.
    /// </summary>
    /// <param name="appPaths">The server application paths.</param>
    /// <param name="logger">The logger.</param>
    public ScriptInjector(IServerApplicationPaths appPaths, ILogger<ScriptInjector> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            InjectIntoIndex();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not inject the Private Libraries widget script into index.html. "
                + "The server may lack write access to the web root; the restriction still works, "
                + "only the home-screen widget button will be missing.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void InjectIntoIndex()
    {
        var webPath = _appPaths.WebPath;
        var indexPath = Path.Combine(webPath, "index.html");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("index.html not found at {Path}; skipping widget injection", indexPath);
            return;
        }

        var html = File.ReadAllText(indexPath);
        var snippet = StartMarker
                      + "<script defer src=\"PrivateLibraries/ClientScript\"></script>"
                      + EndMarker;

        // Remove any stale injected block first (keeps it idempotent across versions).
        var startIdx = html.IndexOf(StartMarker, StringComparison.Ordinal);
        if (startIdx >= 0)
        {
            var endIdx = html.IndexOf(EndMarker, startIdx, StringComparison.Ordinal);
            if (endIdx >= 0)
            {
                endIdx += EndMarker.Length;
                var existing = html.Substring(startIdx, endIdx - startIdx);
                if (existing == snippet)
                {
                    return; // Already up to date.
                }

                html = html.Remove(startIdx, endIdx - startIdx);
            }
        }

        var closeBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closeBody < 0)
        {
            _logger.LogWarning("No </body> tag in index.html; skipping widget injection");
            return;
        }

        html = html.Insert(closeBody, snippet);
        File.WriteAllText(indexPath, html);
        _logger.LogInformation("Injected Private Libraries widget script into {Path}", indexPath);
    }
}
