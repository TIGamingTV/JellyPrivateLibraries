using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PrivateLibraries.Services;

/// <summary>
/// Payload the File Transformation plugin passes to a registered callback when it
/// intercepts a matching web file request. See
/// https://github.com/IAmParadox27/jellyfin-plugin-file-transformation for the
/// (reflection-based) integration contract this mirrors.
/// </summary>
public class FileTransformationPayload
{
    /// <summary>
    /// Gets or sets the current contents of the file being served.
    /// </summary>
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
