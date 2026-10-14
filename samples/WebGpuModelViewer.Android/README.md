# WebGpuModelViewer.Android

The WebGpuModelViewer sample on Android phones, tablets and laptops (ARM64 and x64 devices with Vulkan; the minimum Android release is set in the project file): the same animated Cube Pets scene, HUD and SkiaSharp overlay as the desktop sample - physically based glTF models with their own animation players, lit by a procedural sky and a sun, drawn into a linear RGBA16Float texture and tone mapped into a zero-copy SkiaSharp target - full screen in landscape, presented at the display's refresh rate. It always uses the desktop sample's default rendering (no command-line switches on Android).

The viewer code (scene, renderer, HUD) is the desktop sample's own source, linked into this project from `../WebGpuModelViewer`; only the hosting is Android-specific:

* `MainActivity.cs` - the activity derives from the Android activity class the CodeBrix.Sdl3 package provides, loads only that package's native library and runs the viewer on the thread the package starts for it.
* `Hosting/AndroidRunner.cs` - opens a full-screen window, creates the WebGPU surface over its `ANativeWindow` (`NativeWindowSource.FromAndroid`), writes six PNG frames as a check, then renders live. One finger drags to orbit the camera; two fingers pinch to zoom. The Back button quits.
* `Hosting/AssetInstaller.cs` - the models and textures travel in the APK as assets (the desktop sample's `Assets` folder, linked, not copied) and are copied once to the app's files folder, where the model loader reads them.

The native WebGPU library is packaged into the APK's `lib/<abi>/` folders from the Native project's `runtimes/android-arm64` and `runtimes/android-x64` folders.

## Building and deploying

Requires the .NET Android workload and a device with USB or network debugging enabled (`adb devices` lists it).

```
dotnet build samples/WebGpuModelViewer.Android -c Release   # the signed APK lands in bin/Release/<target-framework>/
adb -s <serial> install -r samples/WebGpuModelViewer.Android/bin/Release/<target-framework>/com.codebrix.webgpumodelviewer-Signed.apk
adb -s <serial> shell am start -n com.codebrix.webgpumodelviewer/com.codebrix.webgpumodelviewer.MainActivity
```

Or build and install in one step:

```
dotnet build samples/WebGpuModelViewer.Android -c Release -t:Install -p:AdbTarget="-s <serial>"
```

The APK carries both ARM64 and x64 code, so the same file installs on either kind of device.

## Checking it

The viewer logs under the tag `WebGpuModelViewer`: the device, the GPU backend and adapter, the models and environment loaded, the surface format and size, the assets copied, the frame rate every two seconds, and any WebGPU errors.

```
adb -s <serial> logcat -s WebGpuModelViewer
```

At every start the viewer writes six 960x540 PNG frames to the app's external files folder before it goes live; fetch them with:

```
adb -s <serial> pull /sdcard/Android/data/com.codebrix.webgpumodelviewer/files/headless
```

Stop the app with `adb -s <serial> shell am force-stop com.codebrix.webgpumodelviewer`.

## Credits

The models are the desktop sample's public-domain (CC0) assets - Cube Pets and Survival Kit by Kenney (www.kenney.nl), and Crystals and Avian by Jeremy Ellis - each pack's licence beside its models in `../WebGpuModelViewer/Assets/` (and inside the APK's assets).
