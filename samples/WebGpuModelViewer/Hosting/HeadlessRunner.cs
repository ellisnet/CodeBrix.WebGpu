using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using SkiaSharp;
using WebGpuModelViewer.Rendering;

namespace WebGpuModelViewer.Hosting;

/// <summary>Renders a fixed number of frames at a fixed time step and writes each one as a PNG (encoded by SkiaSharp).</summary>
internal static class HeadlessRunner
{
    /// <summary>Runs the headless mode.</summary>
    /// <param name="assets">The Assets folder.</param>
    /// <param name="folder">Where the PNGs go (created if missing).</param>
    /// <param name="frames">How many frames.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string assets, string folder, int frames, int width, int height)
    {
        Directory.CreateDirectory(folder);
        Stopwatch setup = Stopwatch.StartNew();
        using ViewerRenderer renderer = new ViewerRenderer(assets, width, height);
        setup.Stop();
        Console.WriteLine("Device: " + renderer.Device.Backend + " - " + renderer.Device.AdapterName);
        Console.WriteLine("Target: " + renderer.Target);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Loaded {0} model files ({1} placed, {2} textures, {3} materials) in {4:F0} ms", renderer.Scene.ModelFileCount, renderer.Scene.ActorCount, renderer.Scene.Cache.TextureCount, renderer.Scene.Cache.MaterialCount, setup.Elapsed.TotalMilliseconds));

        double renderTotal = 0;
        double encodeTotal = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            float time = frame / 30f;
            Stopwatch render = Stopwatch.StartNew();
            renderer.RenderFrame(time);
            using SKImage image = renderer.ReadImage();
            render.Stop();
            renderer.Stats.Add((float)render.Elapsed.TotalMilliseconds);
            if (frame > 0) renderTotal += render.Elapsed.TotalMilliseconds;

            Stopwatch encode = Stopwatch.StartNew();
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            string path = Path.Combine(folder, string.Format(CultureInfo.InvariantCulture, "frame_{0:D4}.png", frame));
            using (FileStream stream = File.Create(path)) png.SaveTo(stream);
            encode.Stop();
            encodeTotal += encode.Elapsed.TotalMilliseconds;
        }

        string[] errors = renderer.Device.TakeUncapturedErrors();
        foreach (string error in errors) Console.WriteLine("WebGPU error: " + error);
        int timed = Math.Max(1, frames - 1);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Rendered {0} frames of {1}x{2} into {3}", frames, width, height, Path.GetFullPath(folder)));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Average render + readback: {0:F2} ms per frame (first frame excluded); PNG encode + write: {1:F2} ms per frame", renderTotal / timed, encodeTotal / Math.Max(1, frames)));
        return errors.Length == 0 ? 0 : 2;
    }
}
