# WebGpuModelViewer

A small CodeBrix.WebGpu sample that shows animated 3D models: eight Cube Pets animals walk a ring, idle, eat and dance around a treasure chest that opens and closes, while the Avian flies circles overhead, in a little camp of trees, rocks, barrels and a fence. The camera orbits the scene under a directional light.

Everything on screen is drawn by the CodeBrix.WebGpu packages on ONE GPU device:

* **CodeBrix.WebGpu.Graphics** renders the 3D scene (a lit mesh renderer with a depth buffer) and the 2D HUD (a sprite batch: the statistics panel with text from a glyph atlas, the frame-time graph and the mini map).
* **CodeBrix.WebGpu.Skia** creates the device and a SkiaSharp context on it; the frame is rendered into the texture of a zero-copy `WebGpuSkiaTarget`, and SkiaSharp then draws the rounded "Cube Pets party" panel directly onto the same pixels.
* The glTF models are loaded with **CodeBrix.Graphics3D.Gltf2** (its runtime scene templates animate the node hierarchies) and converted once into Graphics meshes and materials; their colour-map textures are decoded with SkiaSharp.
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

The sample runs wherever the CodeBrix.WebGpu packages run: Vulkan on Linux, Direct3D 12 (or Vulkan) on Windows, Metal on macOS. On Linux the live window uses X11 or Wayland, whichever the desktop session offers (`--video-driver x11` or `--video-driver wayland` to choose).

## Credits

The models are public-domain (CC0, Creative Commons Zero) assets; only the files the sample uses are included, each pack's licence beside its models in `Assets/`:

* **Cube Pets** by Kenney (www.kenney.nl) - `Assets/cube-pets/` (cat, dog, fox, pig, cow, panda, penguin, bunny), licence `LICENSE-Kenney-cube-pets.txt`
* **Survival Kit** by Kenney (www.kenney.nl) - `Assets/survival-kit/` (chest, trees, rocks, barrel, camp fire, fence, grass), licence `LICENSE-Kenney-survival-kit.txt`
* **Crystals and Avian** by Jeremy Ellis for the Hakunaverse project, made in the style of the Kenney kits - `Assets/crystals-and-avian/avian.glb`, licence `LICENSE-JeremyEllis-crystals-and-avian.txt`

Thank you, Kenney, for the wonderful free assets.
