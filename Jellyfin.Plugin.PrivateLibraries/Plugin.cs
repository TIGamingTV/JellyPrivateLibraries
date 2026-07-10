using System;
using System.Collections.Generic;
using Jellyfin.Plugin.PrivateLibraries.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PrivateLibraries;

/// <summary>
/// The Private Libraries plugin. Restricts every user to only the media they
/// requested through Jellyseerr or were granted through the home-screen widget,
/// implemented on top of Jellyfin's native per-user allowed-tags whitelist.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Private Libraries";

    /// <inheritdoc />
    public override string Description =>
        "Restricts each user to only the media they requested via Jellyseerr or were granted through the home-screen widget.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a3f1c6d2-9b4e-4c8a-bf2d-7e5a1c9d40e1");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
