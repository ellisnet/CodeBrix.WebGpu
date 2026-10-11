using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>A WebGPU error that no error scope caught (a validation error in a draw, an out-of-memory error, ...).</summary>
public sealed class GraphicsError
{
    /// <summary>Creates the error.</summary>
    /// <param name="type">The error type.</param>
    /// <param name="message">The message.</param>
    public GraphicsError(ErrorType type, string message)
    {
        Type = type;
        Message = message ?? string.Empty;
    }

    /// <summary>The error type.</summary>
    public ErrorType Type { get; }

    /// <summary>The native library's message.</summary>
    public string Message { get; }

    /// <summary>The type and the message.</summary>
    /// <returns>For example <c>Validation: ...</c>.</returns>
    public override string ToString() => Type + ": " + Message;
}
