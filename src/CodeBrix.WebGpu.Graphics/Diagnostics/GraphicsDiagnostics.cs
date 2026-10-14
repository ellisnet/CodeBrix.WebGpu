using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Process-wide information about the native WebGPU engine and its log output: version and build details, the
/// instance limits, the managed-to-native struct layout check, and a log callback with a level.
/// </summary>
/// <remarks>Every member loads the native library first and throws <see cref="GraphicsException" /> when it cannot be loaded.</remarks>
public static unsafe class GraphicsDiagnostics
{
    private static Action<LogLevel, string> logHandler;
    private static LogLevel logLevel = LogLevel.Warn;

    /// <summary>The native engine's version (major, minor, build, revision), decoded from its packed version number.</summary>
    public static Version EngineVersion
    {
        get
        {
            EnsureLoaded();
            uint packed = Native.WebGpu.GetVersion();
            return new Version((int)(packed >> 24), (int)((packed >> 16) & 0xFF), (int)((packed >> 8) & 0xFF), (int)(packed & 0xFF));
        }
    }

    /// <summary>The version of the native extras contract the native library implements.</summary>
    public static uint NativeApiVersion
    {
        get
        {
            EnsureLoaded();
            return Native.WebGpu.CodebrixGetApiVersion();
        }
    }

    /// <summary>How the native library was built (versions, backends, target, profile).</summary>
    public static GraphicsBuildInfo BuildInfo
    {
        get
        {
            EnsureLoaded();
            CodebrixBuildInfo info = default;
            Native.WebGpu.CodebrixGetBuildInfo(&info);
            return new GraphicsBuildInfo(info);
        }
    }

    /// <summary>
    /// The instance's limit on futures a timed wait may wait for. Always 0 on this engine: timed waits on futures are
    /// not available (task-returning methods such as <see cref="GraphicsContext.WhenSubmittedWorkDoneAsync" /> are the
    /// managed alternative).
    /// </summary>
    public static ulong TimedWaitAnyMaxCount
    {
        get
        {
            EnsureLoaded();
            InstanceLimits limits = default;
            Native.WebGpu.GetInstanceLimits(&limits);
            return limits.TimedWaitAnyMaxCount;
        }
    }

    /// <summary>The most detailed native log level delivered to the <see cref="SetLogCallback" /> handler. Default <see cref="LogLevel.Warn" />.</summary>
    public static LogLevel LogLevel
    {
        get => logLevel;
        set
        {
            if (value > LogLevel.Trace) throw new ArgumentOutOfRangeException(nameof(value), "Unknown log level " + value + ".");
            EnsureLoaded();
            Native.WebGpu.SetLogLevel(value);
            logLevel = value;
        }
    }

    /// <summary>
    /// Routes the native engine's log messages (adapter selection, validation details of the native extras, warnings)
    /// to a handler, or stops routing them (null). The handler may run on any thread, including GPU-driver threads;
    /// exceptions it throws are swallowed. Set <see cref="LogLevel" /> to choose how much is delivered.
    /// </summary>
    /// <param name="handler">The handler (level, message), or null.</param>
    public static void SetLogCallback(Action<LogLevel, string> handler)
    {
        EnsureLoaded();
        logHandler = handler;
        if (handler == null) Native.WebGpu.SetLogCallback(null, null);
        else Native.WebGpu.SetLogCallback(&OnLog, null);
        Native.WebGpu.SetLogLevel(logLevel);
    }

    /// <summary>
    /// Asks the native library for the size and alignment it uses for one of its structs (by its C name, with or
    /// without the <c>WGPU</c> prefix: <c>WGPULimits</c> or <c>Limits</c>) - the check the native binding runs on load,
    /// available for your own interop.
    /// </summary>
    /// <param name="structName">The struct name.</param>
    /// <param name="size">The size in bytes.</param>
    /// <param name="alignment">The alignment in bytes.</param>
    /// <returns>True when the native library knows the struct.</returns>
    public static bool TryGetNativeStructLayout(string structName, out ulong size, out ulong alignment)
    {
        ArgumentNullException.ThrowIfNull(structName);
        EnsureLoaded();
        byte[] utf8 = Encoding.UTF8.GetBytes(structName.StartsWith("WGPU", StringComparison.Ordinal) ? structName : "WGPU" + structName);
        nuint nativeSize = 0;
        nuint nativeAlignment = 0;
        Status status;
        fixed (byte* text = utf8)
        {
            status = Native.WebGpu.CodebrixGetStructLayout(WgpuString.FromPinned(text, utf8.Length), &nativeSize, &nativeAlignment);
        }

        size = nativeSize;
        alignment = nativeAlignment;
        return status == Status.Success;
    }

    private static void EnsureLoaded()
    {
        try
        {
            CodeBrixWebGpuNative.EnsureLoaded();
        }
        catch (Exception exception) when (exception is WgpuException || exception is DllNotFoundException || exception is EntryPointNotFoundException || exception is BadImageFormatException)
        {
            throw new GraphicsException("The native WebGPU library cannot be used on this platform: " + exception.Message, exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnLog(LogLevel level, StringView message, void* userdata)
    {
        Action<LogLevel, string> handler = logHandler;
        if (handler == null) return;
        try
        {
            handler(level, WgpuString.ToManaged(message) ?? string.Empty);
        }
        catch
        {
            // A log handler must never let an exception reach native code.
        }
    }
}
