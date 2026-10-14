namespace CodeBrix.WebGpu.Native;

/// <summary>
/// An object that owns a WebGPU device and lets other code use it - the contract through which two libraries that
/// both build on this binding share ONE device without referencing each other. The owner implements it (for example
/// <c>WebGpuSkiaDevice</c>); a consumer accepts it (for example <c>GraphicsContext.Wrap</c>).
/// </summary>
/// <remarks>
/// <para>
/// Ownership: every handle stays owned by the source. Reading a property takes NO reference. The handles are valid
/// until the source is disposed (or otherwise releases its device); a consumer that keeps a handle longer than one
/// call takes its own reference (<see cref="WebGpu.InstanceAddRef" />, <see cref="WebGpu.AdapterAddRef" />,
/// <see cref="WebGpu.DeviceAddRef" />, <see cref="WebGpu.QueueAddRef" />) and later releases only that reference -
/// then the source and the consumer may be disposed in either order (the native objects live until the last
/// reference is released).
/// </para>
/// <para>
/// Threading and ordering: the source and its consumers submit to the same <see cref="Queue" />, so the order of
/// their calls orders their GPU work. Use the device from one thread unless the source says otherwise.
/// </para>
/// <para>
/// Errors: a consumer installs no callbacks of its own on a device it did not create. Check
/// <see cref="HasUncapturedErrorHandler" /> before relying on uncaptured errors being reported rather than fatal.
/// </para>
/// </remarks>
public interface IWebGpuDeviceSource
{
    /// <summary>
    /// The instance the adapter and device belong to (needed to create window surfaces on the device). May be null
    /// when the source does not have one.
    /// </summary>
    Instance Instance { get; }

    /// <summary>The adapter the device was created on (needed for surface capabilities). May be null when the source does not have one.</summary>
    Adapter Adapter { get; }

    /// <summary>The device. Null when the source has no usable device (for example it could not create one or has been disposed).</summary>
    Device Device { get; }

    /// <summary>The device's queue (the one the source itself submits to). Null exactly when <see cref="Device" /> is null.</summary>
    Queue Queue { get; }

    /// <summary>The native GPU API the device runs on; <see cref="BackendType.Undefined" /> when there is no device.</summary>
    BackendType Backend { get; }

    /// <summary>
    /// True when the source installed an uncaptured-error callback on the device (so validation errors that no error
    /// scope catches are reported to the source). False when it did not - for example a device adopted over a native
    /// device the application created: the engine's default handler then ENDS THE PROCESS on any uncaptured error,
    /// so a consumer checks risky work inside error scopes (<see cref="WebGpu.DevicePushErrorScope" /> /
    /// <see cref="WebGpu.DevicePopErrorScope" />).
    /// </summary>
    bool HasUncapturedErrorHandler { get; }
}
