using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PrivateLibraries.Services;

/// <summary>
/// Injects the widget loader script into the Jellyfin web client's index.html so the
/// home-screen button appears. Preferred mechanism is registering a transformation with the
/// community "File Transformation" plugin
/// (https://github.com/IAmParadox27/jellyfin-plugin-file-transformation), which patches
/// index.html in memory on every request without ever touching the file on disk, and plays
/// nicely with other plugins (e.g. Intro Skipper, Home Screen Sections) that use the same
/// mechanism to inject their own content.
///
/// If File Transformation is not installed, this falls back to directly patching index.html
/// on disk, the approach originally used by plugins such as Intro Skipper and JellyScrub.
/// </summary>
public class ScriptInjector : IHostedService
{
    private const string StartMarker = "<!-- PrivateLibraries:begin -->";
    private const string EndMarker = "<!-- PrivateLibraries:end -->";

    // Fixed GUID identifying our registration with the File Transformation plugin. Must stay
    // constant across releases: registering again with the same ID on every server restart
    // replaces the previous registration instead of accumulating duplicates.
    private const string TransformationId = "b9a2f8d4-6c1e-4a3f-9b7d-2e5c8a1f6d40";

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

    /// <summary>
    /// Callback invoked by the File Transformation plugin (via reflection) with the current
    /// contents of index.html. Must remain <c>public static</c> with this exact signature -
    /// it is located and invoked by name/type, not through a shared interface.
    /// </summary>
    /// <param name="payload">The file contents supplied by the File Transformation plugin.</param>
    /// <returns>The patched index.html contents.</returns>
    public static string TransformIndexHtml(FileTransformationPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        html = RemoveInjectedBlock(html);

        var closeBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closeBody < 0)
        {
            return html;
        }

        return html.Insert(closeBody, BuildSnippet());
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (TryRegisterFileTransformation())
            {
                // The widget is now injected on-the-fly by the File Transformation plugin.
                // Clean up any stale direct injection left behind by an older version of this
                // plugin (or an earlier run before File Transformation was installed) so the
                // button doesn't end up duplicated.
                RemoveStaleDirectInjection();
            }
            else
            {
                _logger.LogInformation(
                    "File Transformation plugin not found; falling back to directly patching index.html.");
                InjectIntoIndex();
            }
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

    private static string BuildSnippet()
    {
        return StartMarker
               + "<script defer src=\"../PrivateLibraries/ClientScript\"></script>"
               + EndMarker;
    }

    /// <summary>
    /// Strips a previously injected marker block from <paramref name="html"/>, if present.
    /// Used both to keep the direct file-patch fallback idempotent and to avoid double
    /// injection if this method is ever invoked more than once for the same content.
    /// </summary>
    private static string RemoveInjectedBlock(string html)
    {
        var startIdx = html.IndexOf(StartMarker, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            return html;
        }

        var endIdx = html.IndexOf(EndMarker, startIdx, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return html;
        }

        endIdx += EndMarker.Length;
        return html.Remove(startIdx, endIdx - startIdx);
    }

    /// <summary>
    /// Attempts to register <see cref="TransformIndexHtml"/> as an index.html transformation
    /// with the File Transformation plugin, if installed. Per its documented integration
    /// contract, File Transformation plugins cannot be referenced directly (each Jellyfin
    /// plugin loads into its own assembly context), so discovery and invocation go through
    /// reflection. The registration payload is built as a plain JSON string and parsed via the
    /// target's own <c>JObject.Parse</c> (resolved from the same assembly context as the
    /// registration method itself) rather than taking a compile-time dependency on
    /// Newtonsoft.Json, which this single-DLL plugin does not otherwise need and does not ship.
    /// </summary>
    /// <returns><c>true</c> if the transformation was registered; otherwise <c>false</c>.</returns>
    private bool TryRegisterFileTransformation()
    {
        try
        {
            var fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(assembly => assembly.FullName?.Contains(".FileTransformation") ?? false);

            if (fileTransformationAssembly == null)
            {
                return false;
            }

            var pluginInterfaceType =
                fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation");
            var payloadType = registerMethod?.GetParameters().FirstOrDefault()?.ParameterType;
            var parseMethod = payloadType?.GetMethod("Parse", new[] { typeof(string) });
            if (registerMethod == null || parseMethod == null)
            {
                _logger.LogWarning(
                    "Found the File Transformation plugin assembly but not its registration entry point; "
                    + "falling back to directly patching index.html.");
                return false;
            }

            var payloadJson = "{"
                + "\"id\":\"" + TransformationId + "\","
                + "\"fileNamePattern\":\"index.html\","
                + "\"callbackAssembly\":\"" + JsonEscape(typeof(ScriptInjector).Assembly.FullName) + "\","
                + "\"callbackClass\":\"" + JsonEscape(typeof(ScriptInjector).FullName) + "\","
                + "\"callbackMethod\":\"" + nameof(TransformIndexHtml) + "\""
                + "}";

            var payload = parseMethod.Invoke(null, new object[] { payloadJson });
            registerMethod.Invoke(null, new object?[] { payload });
            _logger.LogInformation(
                "Registered the Private Libraries widget script injection with the File Transformation plugin.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to register with the File Transformation plugin; falling back to directly patching index.html.");
            return false;
        }
    }

    private static string JsonEscape(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes a marker block previously written directly into index.html by this plugin, if
    /// present. No-op (and silent) if index.html is missing or was never directly patched.
    /// </summary>
    private void RemoveStaleDirectInjection()
    {
        var indexPath = Path.Combine(_appPaths.WebPath, "index.html");
        if (!File.Exists(indexPath))
        {
            return;
        }

        var html = File.ReadAllText(indexPath);
        var cleaned = RemoveInjectedBlock(html);
        if (!ReferenceEquals(cleaned, html) && cleaned != html)
        {
            File.WriteAllText(indexPath, cleaned);
            _logger.LogInformation(
                "Removed a stale direct widget script injection from {Path} now that File Transformation handles it.",
                indexPath);
        }
    }

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

        // Remove any stale injected block first (keeps it idempotent across versions).
        var cleaned = RemoveInjectedBlock(html);
        var closeBody = cleaned.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closeBody < 0)
        {
            _logger.LogWarning("No </body> tag in index.html; skipping widget injection");
            return;
        }

        var updated = cleaned.Insert(closeBody, BuildSnippet());
        if (updated == html)
        {
            return; // Already up to date.
        }

        File.WriteAllText(indexPath, updated);
        _logger.LogInformation("Injected Private Libraries widget script into {Path}", indexPath);
    }
}
