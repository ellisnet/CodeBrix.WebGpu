using System;
using System.Globalization;
using System.IO;
using WebGpuModelViewer.Hosting;
using WebGpuModelViewer.Rendering;

namespace WebGpuModelViewer;

/// <summary>
/// Shows animated glTF models rendered by CodeBrix.WebGpu.Graphics, with a SkiaSharp overlay drawn through
/// CodeBrix.WebGpu.Skia on the same GPU device.
/// <code>
///   dotnet run --project samples/WebGpuModelViewer [-- --seconds 10]            live window (Escape to quit)
///   dotnet run --project samples/WebGpuModelViewer -- --headless out [--frames 120] [--size 960x540]
///   either mode: [--ldr] [--no-skybox] [--environment panorama.hdr]
/// </code>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string headless = null;
        int frames = 120;
        int width = 0;
        int height = 0;
        double seconds = 0;
        string videoDriver = null;
        ViewerOptions options = new ViewerOptions();
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--headless" when index + 1 < args.Length:
                    headless = args[++index];
                    break;
                case "--frames" when index + 1 < args.Length:
                    frames = Math.Max(1, int.Parse(args[++index], CultureInfo.InvariantCulture));
                    break;
                case "--seconds" when index + 1 < args.Length:
                    seconds = double.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--video-driver" when index + 1 < args.Length:
                    videoDriver = args[++index];
                    break;
                case "--size" when index + 1 < args.Length:
                    string[] parts = args[++index].Split('x', 'X');
                    width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    break;
                case "--ldr":
                    options.Hdr = false;
                    break;
                case "--no-skybox":
                    options.Skybox = false;
                    break;
                case "--environment" when index + 1 < args.Length:
                    options.EnvironmentPath = Path.GetFullPath(args[++index]);
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("WebGpuModelViewer [--headless <folder> [--frames N]] [--seconds S] [--video-driver x11|wayland|windows|cocoa] [--size WIDTHxHEIGHT] [--ldr] [--no-skybox] [--environment <file.hdr>]");
                    Console.WriteLine("  (no arguments)      open a window and render at vsync; Escape or closing the window quits");
                    Console.WriteLine("  --headless <folder> render N frames (default 120) to PNG files in <folder>");
                    Console.WriteLine("  --seconds S         live mode: close the window by itself after S seconds");
                    Console.WriteLine("  --video-driver D    live mode: force a windowing system (for example x11 or wayland on Linux)");
                    Console.WriteLine("  --ldr               draw straight into the 8-bit target (no RGBA16Float scene texture, no tone mapping)");
                    Console.WriteLine("  --no-skybox         clear the background to a flat colour instead of drawing the sky");
                    Console.WriteLine("  --environment F     light the scene with a Radiance .hdr (or PNG/JPEG) panorama instead of the procedural sky");
                    return 0;
                default:
                    Console.Error.WriteLine("Unknown argument '" + args[index] + "'. Try --help.");
                    return 1;
            }
        }

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (!Directory.Exists(assets))
        {
            Console.Error.WriteLine("The Assets folder is missing next to the program: " + assets);
            return 1;
        }

        if (options.EnvironmentPath != null && !File.Exists(options.EnvironmentPath))
        {
            Console.Error.WriteLine("The environment panorama does not exist: " + options.EnvironmentPath);
            return 1;
        }

        return headless != null
            ? HeadlessRunner.Run(assets, headless, frames, width > 0 ? width : 960, height > 0 ? height : 540, options)
            : LiveRunner.Run(assets, width > 0 ? width : 1280, height > 0 ? height : 720, seconds, videoDriver, options);
    }
}
