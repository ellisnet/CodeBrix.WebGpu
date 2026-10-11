using System;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A graphics operation failed: no device could be created, or WebGPU rejected an object the library tried to
/// create (the message carries WebGPU's own validation message).
/// </summary>
public class GraphicsException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public GraphicsException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public GraphicsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A WGSL shader did not compile; <see cref="Exception.Message" /> carries the compiler's diagnostics.</summary>
public sealed class ShaderCompilationException : GraphicsException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="label">The shader's label (or null).</param>
    /// <param name="diagnostics">The compiler's diagnostics.</param>
    public ShaderCompilationException(string label, string diagnostics)
        : base("The WGSL shader" + (label == null ? string.Empty : " '" + label + "'") + " did not compile:" + Environment.NewLine + diagnostics)
    {
        Label = label;
        Diagnostics = diagnostics;
    }

    /// <summary>The shader's label, or null.</summary>
    public string Label { get; }

    /// <summary>The compiler's diagnostics (line, column and message), as the native library formats them.</summary>
    public string Diagnostics { get; }
}
