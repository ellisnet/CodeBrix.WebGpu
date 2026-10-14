namespace WebGpuModelViewer.Rendering;

/// <summary>How the viewer renders (the command-line switches of the desktop sample; the Android host uses the defaults).</summary>
internal sealed class ViewerOptions
{
    /// <summary>
    /// True (the default) renders the scene into a linear RGBA16Float target and tone maps it into the Skia target
    /// (soft highlight roll-off); false draws the models and the sky straight into the Skia target.
    /// </summary>
    public bool Hdr { get; set; } = true;

    /// <summary>True (the default) draws the environment as the background; false clears to a flat sky colour.</summary>
    public bool Skybox { get; set; } = true;

    /// <summary>
    /// A Radiance .hdr (or LDR) panorama to light the scene with instead of the procedural sky, or null. With a
    /// panorama the directional sun light is switched off (the panorama carries its own sun).
    /// </summary>
    public string EnvironmentPath { get; set; }
}
