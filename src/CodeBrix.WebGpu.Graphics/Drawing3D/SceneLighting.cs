using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The lighting a <see cref="MeshRenderer" /> applies: one directional light plus an ambient term.</summary>
public sealed class SceneLighting
{
    /// <summary>The direction the light TRAVELS (from the light into the scene). Default a sun high to the front-left.</summary>
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.5f));

    /// <summary>The light's colour. Default white.</summary>
    public Color4 LightColor { get; set; } = Color4.White;

    /// <summary>Scales the light's colour. Default 1.</summary>
    public float LightIntensity { get; set; } = 1f;

    /// <summary>The ambient light added everywhere. Default 25% grey.</summary>
    public Color4 AmbientColor { get; set; } = new Color4(0.25f, 0.25f, 0.25f);
}
