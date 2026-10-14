# WebGpuModelViewer

A small CodeBrix.WebGpu sample that shows animated 3D models: eight Cube Pets animals walk a ring or idle, dance, eat and cheer (cross-fading from one clip to the next) around a treasure chest that opens and closes, while the Avian flies circles overhead, in a little camp of trees, rocks, barrels and a fence. The camera orbits the scene under a late-morning sky.

Everything on screen is drawn by the CodeBrix.WebGpu packages on ONE GPU device:

* **CodeBrix.WebGpu.Skia** creates the device and a SkiaSharp context on it. Each frame renders into the texture of a zero-copy `WebGpuSkiaTarget`, and SkiaSharp then draws the rounded "Cube Pets party" panel directly onto the same pixels.
* **CodeBrix.WebGpu.Graphics** wraps that device in one call (`GraphicsContext.Wrap(skiaDevice)`) and records the whole frame into the Skia target with one `BeginFrame(target)`:
  * the glTF models are loaded once with `LoadModel` (the kits' shared colour maps decoded and uploaded once through a `ModelTextureCache`); every actor is a `ModelInstance` with its own `AnimationPlayer` (`Play`, `CrossFade`, `AnimationPlayer.UpdateAll`), and the physically based `ModelRenderer` draws them all - one instanced draw per model part for every instance of it;
  * the light is a sun (the key light) plus a procedural sky baked once into an environment map (`CreateProceduralSky`), which gives the models their ambient light and reflections and is drawn behind them by the `SkyboxRenderer`;
  * the scene renders into a linear RGBA16Float texture that the `ToneMapper` (Khronos PBR Neutral) writes into the Skia target, so the kits' flat colours stay true and highlights roll off softly;
  * the 2D HUD is a sprite batch: the statistics panel with text from a glyph atlas, the frame-time graph and the mini map.
* The ground is a one-quad glTF model built in memory with **CodeBrix.Graphics3D.Gltf2**.
* The live window comes from **CodeBrix.Sdl3**; the sample hands its native handles (X11, Wayland, Win32 or a Metal layer) to `GraphicsContext.CreateWindowSurface`.

The sample is not packed and is not part of any NuGet package.

## Running it

Live, in a window (Escape or closing the window quits; drag with the left mouse button to orbit, use the wheel to zoom; the average frame rate is printed on exit):

```
dotnet run --project samples/WebGpuModelViewer
dotnet run --project samples/WebGpuModelViewer -- --seconds 10          # closes by itself
dotnet run --project samples/WebGpuModelViewer -- --video-driver wayland  # force a windowing system on Linux
```

Headless, writing PNG frames (encoded by SkiaSharp) and printing the timing:

```
dotnet run --project samples/WebGpuModelViewer -- --headless frames --frames 120
dotnet run --project samples/WebGpuModelViewer -- --headless frames --frames 24 --size 1280x720
```

Headless frames are rendered at a fixed 30 frames per second of scene time, so the same command always produces the same pictures.

Rendering switches (either mode):

```
dotnet run --project samples/WebGpuModelViewer -- --ldr                          # no float scene texture or tone mapping: models and sky straight into the 8-bit target
dotnet run --project samples/WebGpuModelViewer -- --no-skybox                    # a flat background colour instead of the sky
dotnet run --project samples/WebGpuModelViewer -- --environment panorama.hdr     # light the scene with a Radiance .hdr (or PNG/JPEG) panorama instead of the procedural sky
```

No panorama ships with the sample; any equirectangular `.hdr` works, for example the CC0 HDRIs from Poly Haven (polyhaven.com). With a panorama the sun light is switched off, because the panorama carries its own.

The sample runs wherever the CodeBrix.WebGpu packages run: Vulkan on Linux, Direct3D 12 (or Vulkan) on Windows, Metal on macOS. On Linux the live window uses X11 or Wayland, whichever the desktop session offers (`--video-driver x11` or `--video-driver wayland` to choose).

## Credits

The models are public-domain (CC0, Creative Commons Zero) assets; only the files the sample uses are included, each pack's licence beside its models in `Assets/`:

* **Cube Pets** by Kenney (www.kenney.nl) - `Assets/cube-pets/` (cat, dog, fox, pig, cow, panda, penguin, bunny), licence `LICENSE-Kenney-cube-pets.txt`
* **Survival Kit** by Kenney (www.kenney.nl) - `Assets/survival-kit/` (chest, trees, rocks, barrel, camp fire, fence, grass), licence `LICENSE-Kenney-survival-kit.txt`
* **Crystals and Avian** by Jeremy Ellis for the Hakunaverse project, made in the style of the Kenney kits - `Assets/crystals-and-avian/avian.glb`, licence `LICENSE-JeremyEllis-crystals-and-avian.txt`

Thank you, Kenney, for the wonderful free assets.
