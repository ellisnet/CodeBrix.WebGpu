using System;

namespace CodeBrix.WebGpu.Skia;

/// <summary>
/// Thrown by <see cref="WebGpuSkiaTarget.EndWebGpuPass" /> when the commands recorded in the pass failed WebGPU
/// validation. Nothing was submitted (the native engine would end the process on an invalid submission, so the
/// target checks first); the pass is closed and the target is ready for Skia drawing and the next pass.
/// </summary>
public sealed class WebGpuSkiaValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="webGpuMessage">WebGPU's own validation message.</param>
    public WebGpuSkiaValidationException(string message, string webGpuMessage)
        : base(message)
    {
        WebGpuMessage = webGpuMessage;
    }

    /// <summary>WebGPU's own validation message.</summary>
    public string WebGpuMessage { get; }
}
