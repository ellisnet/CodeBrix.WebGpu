using Android.Util;

namespace WebGpuModelViewer.AndroidHosting;

/// <summary>Writes the viewer's messages to the Android log under the tag "WebGpuModelViewer".</summary>
internal static class ViewerLog
{
    /// <summary>The log tag.</summary>
    public const string Tag = "WebGpuModelViewer";

    /// <summary>Logs an informational message.</summary>
    /// <param name="message">The message.</param>
    public static void Info(string message) => Log.Info(Tag, message);

    /// <summary>Logs a warning.</summary>
    /// <param name="message">The message.</param>
    public static void Warn(string message) => Log.Warn(Tag, message);

    /// <summary>Logs an error.</summary>
    /// <param name="message">The message.</param>
    public static void Error(string message) => Log.Error(Tag, message);
}
