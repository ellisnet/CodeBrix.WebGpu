namespace CodeBrix.WebGpu.Skia;

/// <summary>
/// One requirement of <see cref="WebGpuSkiaDeviceOptions" /> an adapter could not satisfy, as reported in
/// <see cref="WebGpuSkiaDevice.UnmetRequirements" /> (the device is then unavailable; nothing is silently dropped).
/// </summary>
public sealed class WebGpuSkiaUnmetRequirement
{
    internal WebGpuSkiaUnmetRequirement(WebGpuSkiaBackend backend, string adapterName, WebGpuSkiaRequirementKind kind, string name, ulong requestedValue, ulong adapterValue, string message)
    {
        Backend = backend;
        AdapterName = adapterName;
        Kind = kind;
        Name = name;
        RequestedValue = requestedValue;
        AdapterValue = adapterValue;
        Message = message;
    }

    /// <summary>The backend whose adapter was asked.</summary>
    public WebGpuSkiaBackend Backend { get; }

    /// <summary>The adapter's description (GPU name and driver), or null when unknown.</summary>
    public string AdapterName { get; }

    /// <summary>Whether a feature, a native feature or a limit is missing.</summary>
    public WebGpuSkiaRequirementKind Kind { get; }

    /// <summary>
    /// The feature's enum name (for example <c>ShaderF16</c>, <c>Immediates</c>) or the limit's field name (for
    /// example <c>MaxTextureDimension2D</c>); <c>DeviceRequest</c> for <see cref="WebGpuSkiaRequirementKind.DeviceRequest" />.
    /// </summary>
    public string Name { get; }

    /// <summary>The requested value: the limit's value, or the feature's numeric value.</summary>
    public ulong RequestedValue { get; }

    /// <summary>
    /// The adapter's value of the limit (its best: the maximum, or for an alignment the minimum); 0 for a feature
    /// (the adapter does not have it).
    /// </summary>
    public ulong AdapterValue { get; }

    /// <summary>A sentence that says what was asked for and what the adapter offers.</summary>
    public string Message { get; }

    /// <summary>The <see cref="Message" />.</summary>
    /// <returns>The message.</returns>
    public override string ToString() => Message;
}
