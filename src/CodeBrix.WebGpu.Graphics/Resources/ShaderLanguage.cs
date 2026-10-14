namespace CodeBrix.WebGpu.Graphics;

/// <summary>The source language of a <see cref="GpuShader" />.</summary>
public enum ShaderLanguage
{
    /// <summary>WGSL (<see cref="GraphicsContext.CreateShader(string, string)" />).</summary>
    Wgsl = 0,

    /// <summary>SPIR-V words (<see cref="GraphicsContext.CreateShaderFromSpirV" />), validated and translated by the engine.</summary>
    SpirV,

    /// <summary>SPIR-V handed to the driver unchanged (<see cref="GraphicsContext.CreateShaderFromSpirVPassthrough" />).</summary>
    SpirVPassthrough,

    /// <summary>GLSL for one stage (<see cref="GraphicsContext.CreateShaderFromGlsl" />).</summary>
    Glsl,

    /// <summary>A module another library created (<see cref="GraphicsContext.WrapShader" />).</summary>
    Unknown,
}
