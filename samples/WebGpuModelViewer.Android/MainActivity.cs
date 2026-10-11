using System;
using Android.App;
using Android.Content.PM;
using Org.Libsdl.App;
using WebGpuModelViewer.AndroidHosting;

namespace WebGpuModelViewer;

/// <summary>
/// The application's activity. SDL on Android is driven by its Java activity, so this derives from SDL's: it loads
/// only the SDL native library and runs the viewer on SDL's thread.
/// </summary>
[Activity(
    Name = "com.codebrix.webgpumodelviewer.MainActivity",
    Label = "WebGpuModelViewer",
    MainLauncher = true,
    Exported = true,
    Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
    LaunchMode = LaunchMode.SingleInstance,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode)]
public class MainActivity : SDLActivity
{
    /// <summary>Loads the SDL native library only; everything else is loaded by .NET.</summary>
    /// <returns>The libraries for SDL to load.</returns>
    protected override string[] GetLibraries() => ["SDL3"];

    /// <summary>Runs the viewer on SDL's thread; the activity finishes when it returns.</summary>
    protected override void Main()
    {
        try
        {
            AndroidRunner.Run(this);
        }
        catch (Exception exception)
        {
            ViewerLog.Error("The viewer failed: " + exception);
        }
    }
}
