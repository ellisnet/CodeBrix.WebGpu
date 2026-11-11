using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia;
using SkiaSharp;
using VenusMusicVideo.Choreography;
using VenusMusicVideo.Hosting;

namespace VenusMusicVideo.Rendering;

/// <summary>
/// Draws Venus. One GPU device serves both CodeBrix.WebGpu packages: a <see cref="WebGpuSkiaDevice" /> (asked for
/// 16K textures) and a <see cref="GraphicsContext" /> wrapping it. Each frame is ONE full-screen draw of the planet
/// shader into the texture of a <see cref="WebGpuSkiaTarget" /> at the video's size (1080x1920); SkiaSharp can then
/// draw on the same pixels (the preview's overlay), and the frame is read back for the video or drawn, scaled to
/// fit, onto the preview window.
/// </summary>
internal sealed class VenusRenderer : IDisposable
{
    private readonly GpuRenderPipeline pipeline;
    private readonly GpuBuffer uniforms;
    private readonly GpuSampler sampler;
    private GpuTexture colorMap;
    private GpuTexture reliefMap;
    private GpuBindGroup bindGroup;
    private GpuBuffer envelope;
    private int envelopeLength = 2;
    private readonly GpuTexture targetTexture;
    private readonly SpriteBatch sprites;
    private long frameNumber;

    /// <summary>Creates the device and builds the pipeline (the maps come later: <see cref="LoadMaps" />).</summary>
    /// <param name="width">The frame width.</param>
    /// <param name="height">The frame height.</param>
    public VenusRenderer(int width, int height)
    {
        Limits limits = WebGpuSkiaLimits.Undefined();
        limits.MaxTextureDimension2D = 16384;
        Device = WebGpuSkiaDevice.Create(new WebGpuSkiaDeviceOptions { RequiredLimits = limits });
        if (!Device.IsAvailable)
        {
            Console.WriteLine("No device with 16K textures (" + Device.Reason + "); falling back to the 8K colour map.");
            Device.Dispose();
            Device = WebGpuSkiaDevice.Create();
        }

        if (!Device.IsAvailable) throw new InvalidOperationException("No WebGPU device: " + Device.Reason);
        GrContext = Device.CreateGrContext() ?? throw new InvalidOperationException("No SkiaSharp GRContext on the WebGPU device: " + Device.GrContextReason);
        Context = GraphicsContext.Wrap(Device);
        MaxTextureSize = Context.Limits.MaxTextureDimension2D;

        Target = WebGpuSkiaTarget.Create(Device, GrContext, width, height);
        if (!Target.IsAvailable) throw new InvalidOperationException("No Skia target: " + Target.TierReason);
        targetTexture = Context.WrapTexture(Target, "frame");
        sprites = new SpriteBatch(Context);

        sampler = Context.CreateSampler(new SamplerDescription
        {
            AddressModeU = AddressMode.Repeat,
            AddressModeV = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Linear,
            MaxAnisotropy = 16,
            Label = "venus maps",
        });

        string source;
        using (Stream stream = typeof(VenusRenderer).Assembly.GetManifestResourceStream("Venus.wgsl") ?? throw new InvalidOperationException("The Venus.wgsl resource is missing."))
        using (StreamReader reader = new StreamReader(stream))
        {
            source = reader.ReadToEnd();
        }

        using GpuShader shader = Context.CreateShader(source, "venus");
        pipeline = Context.CreateRenderPipeline(shader).WithColorTarget(Target.Format).WithLabel("venus").Build();
        uniforms = Context.CreateUniformBuffer((ulong)Marshal.SizeOf<VenusUniforms>());
        envelope = Context.CreateStorageBuffer<float>(new float[] { 0, 0 });
        EncodeSrgb = !Target.Format.ToString().Contains("Srgb", StringComparison.Ordinal);
    }

    /// <summary>The largest 2D texture the device takes (16384 when the 16K colour map can be used).</summary>
    public uint MaxTextureSize { get; }

    /// <summary>True once the maps are loaded.</summary>
    public bool HasMaps => bindGroup != null;

    /// <summary>Loads the colour and relief maps onto the GPU (with mipmaps).</summary>
    /// <param name="colorPath">The colour map (sRGB).</param>
    /// <param name="reliefPath">The relief map (linear data).</param>
    public void LoadMaps(string colorPath, string reliefPath)
    {
        Stopwatch load = Stopwatch.StartNew();
        bindGroup?.Dispose();
        colorMap?.Dispose();
        reliefMap?.Dispose();
        colorMap = Context.CreateTextureFromFile(colorPath, new TextureLoadOptions { Mipmaps = true, ColorSpace = TextureColorSpace.Srgb });
        reliefMap = Context.CreateTextureFromFile(reliefPath, new TextureLoadOptions { Mipmaps = true, ColorSpace = TextureColorSpace.Linear });
        RebuildBindGroup();
        Console.WriteLine("Maps: " + Path.GetFileName(colorPath) + " (" + colorMap.Width + "x" + colorMap.Height + ") and " + Path.GetFileName(reliefPath) + " loaded in " + load.ElapsedMilliseconds + " ms");
    }

    /// <summary>Gives the shader the song's envelope (the sound-wave ripples are drawn from it).</summary>
    public void SetEnvelope(float[] values)
    {
        if (values == null || values.Length < 2) return;
        envelope.Dispose();
        envelope = Context.CreateStorageBuffer<float>(values);
        envelopeLength = values.Length;
        if (colorMap != null) RebuildBindGroup();
    }

    private void RebuildBindGroup()
    {
        bindGroup?.Dispose();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        bindGroup = Context.CreateBindGroup(layout, "venus",
            BindingEntry.ForBuffer(0, uniforms),
            BindingEntry.ForTexture(1, colorMap),
            BindingEntry.ForTexture(2, reliefMap),
            BindingEntry.ForSampler(3, sampler),
            BindingEntry.ForBuffer(4, envelope));
    }

    /// <summary>Draws a status screen (black, a title, a detail line and a progress bar) into the frame.</summary>
    /// <param name="title">The title.</param>
    /// <param name="detail">The detail line, or null.</param>
    /// <param name="step">This step's progress (0..1), or null for no bar.</param>
    /// <param name="overall">The overall progress (0..1), or null.</param>
    public void RenderStatus(string title, string detail, double? step, double? overall)
    {
        using (GraphicsFrame frame = Context.BeginFrame(Target))
        {
            using (RenderPass pass = frame.BeginRenderPass(Color4.Black, null))
            {
            }

            frame.Submit();
        }

        StatusScreen.Draw(Target.Surface.Canvas, Width, Height, title, detail, step, overall);
    }

    /// <summary>The shared device.</summary>
    public WebGpuSkiaDevice Device { get; }

    /// <summary>SkiaSharp's context on it.</summary>
    public GRContext GrContext { get; }

    /// <summary>The graphics context.</summary>
    public GraphicsContext Context { get; }

    /// <summary>The frame (the video's size).</summary>
    public WebGpuSkiaTarget Target { get; }

    /// <summary>Width of the frame.</summary>
    public int Width => Target.Width;

    /// <summary>Height of the frame.</summary>
    public int Height => Target.Height;

    private bool EncodeSrgb { get; }

    /// <summary>Renders one frame of the flight into the target.</summary>
    /// <param name="script">The flight.</param>
    /// <param name="pose">The camera.</param>
    /// <param name="fieldOfViewDegrees">The vertical field of view.</param>
    /// <param name="applyFade">False shows the frame without the video's fades (the preview's waiting screen).</param>
    public void Render(FlightScript script, CameraPose pose, double fieldOfViewDegrees = 55, bool applyFade = true)
    {
        if (bindGroup == null) throw new InvalidOperationException("LoadMaps first.");
        VenusLook look = script.Look;
        float tanY = (float)Math.Tan(fieldOfViewDegrees * Math.PI / 360);
        float tanX = tanY * Width / Height;
        float pixelAngle = 2f * tanY / Height;

        // The light comes from above-left of the camera and a little behind it, so the relief always reads.
        Vector3 forward = pose.Forward.ToFloat();
        Vector3 right = pose.Right.ToFloat();
        Vector3 up = pose.Up.ToFloat();
        // The studio light: from above-left of the camera and a little behind it, so the relief always reads.
        Vector3 studio = Vector3.Normalize((-0.45f * right) + (0.6f * up) - (0.65f * forward));

        // The sun: it goes round the planet as seen from the camera (phase 0 = behind the camera, a "full" Venus;
        // 180 = behind the planet, a "new" Venus), as the phases look from Earth.
        float phase = (float)(pose.PhaseDegrees * Math.PI / 180);
        Vector3 sunDirection = Vector3.Normalize((MathF.Cos(phase) * -forward) + (MathF.Sin(phase) * right) + (0.12f * up));
        float sun = (float)pose.Sunlight;
        Vector3 blended = Vector3.Lerp(studio, sunDirection, sun);
        Vector3 light = blended.LengthSquared() > 1e-6f ? Vector3.Normalize(blended) : sunDirection;
        float ambient = look.Ambient + ((look.NightAmbient - look.Ambient) * sun);
        float diffuseStrength = look.Diffuse + ((look.SunDiffuse - look.Diffuse) * sun);

        double t = pose.VideoTime;
        float fade = !applyFade ? 1f : (float)Math.Clamp(Math.Min(t / Math.Max(1e-3, script.FadeInSeconds), (script.VideoSeconds - t) / Math.Max(1e-3, script.FadeOutSeconds)), 0, 1);
        fade = fade * fade * (3 - (2 * fade));
        float thickness = (float)(look.HazeThicknessKm / FlightScript.RadiusKm);

        VenusUniforms data = new VenusUniforms
        {
            Camera = new Vector4(pose.Position.ToFloat(), (float)pose.SpinAngle),
            Right = new Vector4(right, tanX),
            Up = new Vector4(up, tanY),
            Forward = new Vector4(forward, pixelAngle),
            Light = new Vector4(light, look.ReliefExaggeration * (float)Math.Clamp(pose.AltitudeKm / 4000.0, 0.35, 1.0)),
            Parameters = new Vector4(Width, Height, fade, EncodeSrgb ? 1 : 0),
            Haze = new Vector4(thickness, look.HazeDensity, look.HazeGlow, look.StarBrightness),
            HazeLow = new Vector4(look.HazeLow, ambient),
            HazeHigh = new Vector4(look.HazeHigh, diffuseStrength),
            Extra = new Vector4(look.Exposure, look.StarDensity, frameNumber++ % 997, 0),
            Lighting = new Vector4(sun, look.TerminatorSoftness, 0, 0),
            WaveA = new Vector4((float)t, (float)script.LeadInSeconds, Audio.SongEnvelope.Rate, envelopeLength),
            WaveColor = new Vector4(look.WaveColor, 0),
        };

        // The loudspeakers whose rings may be on screen (positions on the planet, start/end, speed).
        SoundWave.Source[] sources = new SoundWave.Source[SoundWave.MaxSources];
        int count = script.Wave.Visible(t, sources);
        data.WaveB = new Vector4(look.WaveStrength, look.WaveReachDegrees * MathF.PI / 180f, look.WaveRippleHz, count);
        for (int k = 0; k < count; k++)
        {
            SoundWave.Source source = sources[k];
            Vector3 position = FlightScript.Direction(source.Place.Lat, source.Place.Lon).ToFloat();
            float end = double.IsInfinity(source.End) ? 1e9f : (float)source.End;
            data.SetSource(k, new Vector4(position, (float)source.Start), new Vector4(end, (float)source.Speed, 0, 0));
        }
        uniforms.WriteValue(in data);

        using GraphicsFrame frame = Context.BeginFrame(Target);
        using (RenderPass pass = frame.BeginRenderPass(Color4.Black, null))
        {
            pass.SetPipeline(pipeline);
            pass.SetBindGroup(0, bindGroup);
            pass.Draw(3);
        }

        frame.Submit();
        script.Markers.Draw(Target.Surface.Canvas, Width, Height, pose);
    }

    /// <summary>Reads the frame's pixels (RGBA or BGRA, see <see cref="IsBgra" />), tightly packed.</summary>
    /// <param name="destination">Width x height x 4 bytes.</param>
    public void ReadPixels(Span<byte> destination)
    {
        if (!Target.ReadPixels(destination, Width * 4)) throw new InvalidOperationException("Reading the frame back failed: " + Target.LastError);
    }

    /// <summary>True when the frame's bytes are BGRA (else RGBA).</summary>
    public bool IsBgra => Target.Format.ToString().StartsWith("BGRA", StringComparison.Ordinal);

    /// <summary>Draws the frame onto a window surface, scaled to fit (letterboxed), and presents it.</summary>
    /// <param name="surface">The configured surface.</param>
    /// <returns>True when a frame was presented.</returns>
    public bool PresentTo(WindowSurface surface)
    {
        if (surface.TryAcquire(out RenderTargetView view) != SurfaceAcquireResult.Ready) return false;
        float scale = Math.Min(view.Width / (float)Width, view.Height / (float)Height);
        float w = Width * scale;
        float h = Height * scale;
        using (GraphicsFrame frame = Context.BeginFrame(Target))
        {
            using (RenderPass pass = frame.BeginRenderPass(view, Color4.Black, null))
            {
                sprites.Begin(pass, null, BlendPreset.Opaque);
                sprites.Draw(targetTexture, new RectangleF((view.Width - w) / 2, (view.Height - h) / 2, w, h), Color4.White);
                sprites.End();
            }

            frame.Submit();
        }

        surface.Present();
        return true;
    }

    /// <summary>Releases everything, the device last.</summary>
    public void Dispose()
    {
        Context.WaitIdle();
        bindGroup?.Dispose();
        envelope.Dispose();
        uniforms.Dispose();
        pipeline.Dispose();
        sampler.Dispose();
        reliefMap?.Dispose();
        colorMap?.Dispose();
        sprites.Dispose();
        targetTexture.Dispose();
        Target.Dispose();
        Context.Dispose();
        Device.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VenusUniforms
    {
        public Vector4 Camera;
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Forward;
        public Vector4 Light;
        public Vector4 Parameters;
        public Vector4 Haze;
        public Vector4 HazeLow;
        public Vector4 HazeHigh;
        public Vector4 Extra;
        public Vector4 Lighting;
        public Vector4 WaveA;
        public Vector4 WaveB;
        public Vector4 WaveColor;
        public Vector4 S0;
        public Vector4 S1;
        public Vector4 S2;
        public Vector4 S3;
        public Vector4 S4;
        public Vector4 S5;
        public Vector4 S6;
        public Vector4 S7;

        public void SetSource(int k, Vector4 a, Vector4 b)
        {
            switch (k)
            {
                case 0: S0 = a; S1 = b; break;
                case 1: S2 = a; S3 = b; break;
                case 2: S4 = a; S5 = b; break;
                case 3: S6 = a; S7 = b; break;
            }
        }
    }
}
