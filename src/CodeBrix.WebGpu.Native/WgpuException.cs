using System;

namespace CodeBrix.WebGpu.Native;

/// <summary>
/// The native codebrix_wgpu library could not be loaded, reports a contract version this binding was not
/// generated for, or disagrees with the binding about a struct layout.
/// </summary>
/// <remarks>
/// WebGPU errors themselves (validation errors, device loss, failed requests) are not exceptions: they
/// arrive through the callbacks and status values of the API, exactly as in C.
/// </remarks>
public sealed class WgpuException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public WgpuException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public WgpuException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
