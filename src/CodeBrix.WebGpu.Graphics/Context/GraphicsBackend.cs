namespace CodeBrix.WebGpu.Graphics;

/// <summary>The native GPU API a <see cref="GraphicsContext" /> asks for.</summary>
public enum GraphicsBackend
{
    /// <summary>The platform's own: Direct3D 12 or Vulkan on Windows, Metal on macOS, Vulkan on Linux and Android.</summary>
    Default = 0,

    /// <summary>Vulkan (Linux, Android, Windows).</summary>
    Vulkan,

    /// <summary>Direct3D 12 (Windows).</summary>
    Direct3D12,

    /// <summary>Metal (macOS).</summary>
    Metal,

    /// <summary>OpenGL / OpenGL ES, where the native library for the platform carries it.</summary>
    OpenGL,
}
