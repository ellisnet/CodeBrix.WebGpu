using System;
using System.Drawing;
using System.Numerics;
using CodeBrix.WebGpu.Graphics;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia;
using SkiaSharp;
using WebGpuModelViewer.Hud;
using WebGpuModelViewer.Scene;

namespace WebGpuModelViewer.Rendering;

/// <summary>
/// Renders the viewer's frames. One GPU device is shared by both CodeBrix.WebGpu packages: a
/// <see cref="WebGpuSkiaDevice" /> creates it (and the SkiaSharp GRContext on it), and a <see cref="GraphicsContext" />
/// wraps it in one call. Each frame:
/// <list type="number">
/// <item>the glTF models (physically based, lit by a procedural sky and a sun light) and the skybox render into a
/// linear RGBA16Float texture, which is tone mapped into the texture of a zero-copy <see cref="WebGpuSkiaTarget" />;
/// the sprite-batch HUD is drawn on top - all inside ONE frame opened on the Skia target (<c>BeginFrame(target)</c>);</item>
/// <item>SkiaSharp draws its overlay straight onto the same pixels;</item>
/// <item>headless: the frame is read back through Skia; live: the target's texture is drawn onto the window surface
/// as one full-screen sprite and presented.</item>
/// </list>
/// </summary>
/// <remarks>
/// The Skia target is an 8-bit UNORM texture. The model renderer, skybox and tone mapper sRGB-encode in their shaders
/// for such targets, so the models' sRGB colour maps come out right; the HUD's sprite colours are display values and
/// are written as they are.
/// </remarks>
internal sealed class ViewerRenderer : IDisposable
{
    /// <summary>The background when the skybox is off (a display colour).</summary>
    private static readonly Color4 FlatSky = Color4.FromBytes(150, 196, 232);

    /// <summary>The same colour as linear light, for the HDR texture (the tone mapper encodes it back).</summary>
    private static readonly Color4 FlatSkyLinear = new Color4(0.305f, 0.552f, 0.807f);

    /// <summary>The direction the sunlight travels.</summary>
    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.45f, -1f, -0.35f));

    private readonly SKPaint overlayFill = new SKPaint { Color = new SKColor(16, 20, 28, 190), IsAntialias = true };
    private readonly SKPaint overlayStroke = new SKPaint { Color = new SKColor(255, 200, 87), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
    private readonly SKPaint overlayText = new SKPaint { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint overlayAccent = new SKPaint { Color = new SKColor(255, 200, 87), IsAntialias = true };
    private readonly SKFont titleFont;
    private readonly SKFont bodyFont;
    private readonly HudOverlay hud;
    private readonly ViewerOptions options;
    private readonly EnvironmentMap environment;
    private readonly SkyboxRenderer skybox;
    private readonly ToneMapper toneMapper;
    private WebGpuSkiaTarget target;
    private GpuTexture depth;
    private GpuTexture targetTexture;
    private HeadlessRenderTarget hdrTarget;

    /// <summary>Creates the device, loads the scene and makes a target of the given size.</summary>
    /// <param name="assets">The Assets folder.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="options">How to render, or null for the defaults.</param>
    public ViewerRenderer(string assets, int width, int height, ViewerOptions options = null)
    {
        this.options = options ?? new ViewerOptions();
        Device = WebGpuSkiaDevice.Create();
        if (!Device.IsAvailable) throw new InvalidOperationException("No WebGPU device: " + Device.Reason);
        GrContext = Device.CreateGrContext() ?? throw new InvalidOperationException("No SkiaSharp GRContext on the WebGPU device: " + Device.GrContextReason);
        Context = GraphicsContext.Wrap(Device);
        Models = Context.CreateModelRenderer();
        Sprites = new SpriteBatch(Context);
        Scene = new ViewerScene(Context, assets);
        hud = new HudOverlay(Context);
        skybox = Context.CreateSkyboxRenderer();
        toneMapper = Context.CreateToneMapper();

        // Khronos PBR Neutral keeps the kits' flat colours true and only rolls off the highlights.
        toneMapper.Operator = ToneMapOperator.KhronosPbrNeutral;
        SKTypeface typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold) ?? SKTypeface.Default;
        titleFont = new SKFont(typeface, 20);
        bodyFont = new SKFont(SKTypeface.FromFamilyName("sans-serif") ?? SKTypeface.Default, 14);
        Camera = new Camera { FieldOfView = 50f * MathF.PI / 180f, NearPlane = 0.1f, FarPlane = 200 };
        Orbit = new OrbitCamera { Target = new Vector3(0, 0.9f, 0), Distance = 10.5f, Pitch = 0.30f };

        // A late-morning sky whose bright side matches the sun light, baked once into an environment map; the
        // models take their ambient light and reflections from it, the skybox shows it. Its ground half is a dark
        // meadow green, so the light bouncing up onto the animals is green-tinted like the grass they stand on.
        if (string.IsNullOrEmpty(this.options.EnvironmentPath))
        {
            environment = Context.CreateProceduralSky(new ProceduralSkyOptions
            {
                SunDirection = SunDirection,
                ZenithColor = new Color4(0.14f, 0.32f, 0.70f),
                HorizonColor = new Color4(0.40f, 0.58f, 0.85f),
                GroundColor = new Color4(0.10f, 0.15f, 0.07f),
                Turbidity = 2f,
            });
            EnvironmentDescription = "procedural sky";
            Lighting = new SceneLighting
            {
                LightDirection = SunDirection,
                LightColor = new Color4(1f, 0.95f, 0.86f),
                LightIntensity = 1.6f,
                Environment = environment,
                EnvironmentIntensity = 1f,
                Exposure = 1f,
            };
        }
        else
        {
            environment = Context.CreateEnvironment(this.options.EnvironmentPath);
            EnvironmentDescription = System.IO.Path.GetFileName(this.options.EnvironmentPath);
            Lighting = new SceneLighting { LightIntensity = 0f, Environment = environment, Exposure = 1.2f };
        }

        Resize(width, height);
    }

    /// <summary>The shared device (owned here).</summary>
    public WebGpuSkiaDevice Device { get; }

    /// <summary>The SkiaSharp context on the device.</summary>
    public GRContext GrContext { get; }

    /// <summary>The Graphics context wrapping the device.</summary>
    public GraphicsContext Context { get; }

    /// <summary>The physically based glTF model renderer.</summary>
    public ModelRenderer Models { get; }

    /// <summary>The 2D renderer.</summary>
    public SpriteBatch Sprites { get; }

    /// <summary>The scene.</summary>
    public ViewerScene Scene { get; }

    /// <summary>The camera.</summary>
    public Camera Camera { get; }

    /// <summary>The orbit controller driving the camera.</summary>
    public OrbitCamera Orbit { get; }

    /// <summary>An extra orbit angle the user added by dragging, in radians.</summary>
    public float UserYaw { get; set; }

    /// <summary>The lighting: the sun (key light), the environment and the exposure.</summary>
    public SceneLighting Lighting { get; }

    /// <summary>What lights the scene ("procedural sky" or the panorama's file name).</summary>
    public string EnvironmentDescription { get; }

    /// <summary>The options the renderer was made with.</summary>
    public ViewerOptions Options => options;

    /// <summary>The frame statistics shown by the HUD.</summary>
    public FrameStats Stats { get; } = new FrameStats();

    /// <summary>The Skia target the frame is rendered into.</summary>
    public WebGpuSkiaTarget Target => target;

    /// <summary>Recreates the target, the depth buffer and the HDR texture for a new size.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (target != null && target.Width == width && target.Height == height) return;
        Sprites.Forget(targetTexture);
        targetTexture?.Dispose();
        depth?.Dispose();
        hdrTarget?.Dispose();
        target?.Dispose();
        target = WebGpuSkiaTarget.Create(Device, GrContext, width, height);
        if (!target.IsAvailable) throw new InvalidOperationException("No Skia target: " + target.TierReason);
        if (options.Hdr)
        {
            depth = null;
            hdrTarget = Context.CreateHeadlessTarget(width, height, TextureFormat.RGBA16Float, withDepth: true, TextureFormat.Depth24Plus);
        }
        else
        {
            hdrTarget = null;
            depth = Context.CreateDepthTexture(width, height, TextureFormat.Depth24Plus, "scene depth");
        }

        targetTexture = Context.WrapTexture(target, "skia target");
    }

    /// <summary>Renders the scene, the HUD and the Skia overlay into the target.</summary>
    /// <param name="time">The scene time in seconds.</param>
    public void RenderFrame(float time)
    {
        Scene.Update(time);
        Orbit.Yaw = 0.7f + (time * 0.18f) + UserYaw;
        Orbit.Apply(Camera);

        using (GraphicsFrame frame = Context.BeginFrame(target, depth))
        {
            if (options.Hdr)
            {
                using (RenderPass pass = frame.BeginRenderPass(hdrTarget.View, options.Skybox ? Color4.Black : FlatSkyLinear))
                {
                    DrawScene(pass);
                }

                using (RenderPass pass = frame.BeginRenderPass(Color4.Black, null))
                {
                    toneMapper.Apply(pass, hdrTarget.Color);
                    DrawHud(pass);
                }
            }
            else
            {
                using (RenderPass pass = frame.BeginRenderPass(FlatSky))
                {
                    DrawScene(pass);
                    DrawHud(pass);
                }
            }

            frame.Submit();
        }

        DrawSkiaOverlay(target.Surface.Canvas, target.Width, target.Height);
    }

    /// <summary>Draws the rendered frame onto a window surface as one full-screen sprite and presents it.</summary>
    /// <param name="surface">The configured surface.</param>
    /// <returns>True when a frame was presented.</returns>
    public bool PresentTo(WindowSurface surface)
    {
        if (surface.TryAcquire(out RenderTargetView view) != SurfaceAcquireResult.Ready) return false;
        using (GraphicsFrame frame = Context.BeginFrame(target))
        {
            using (RenderPass pass = frame.BeginRenderPass(view, Color4.Black, null))
            {
                Sprites.Begin(pass, null, BlendPreset.Opaque);
                Sprites.Draw(targetTexture, new RectangleF(0, 0, view.Width, view.Height), Color4.White);
                Sprites.End();
            }

            frame.Submit();
        }

        surface.Present();
        return true;
    }

    /// <summary>Reads the frame back as an image (Skia's overlay included).</summary>
    /// <returns>The image.</returns>
    public SKImage ReadImage() => target.ReadImage();

    /// <summary>Releases everything, the device last.</summary>
    public void Dispose()
    {
        Context.WaitIdle();
        targetTexture?.Dispose();
        depth?.Dispose();
        hdrTarget?.Dispose();
        target?.Dispose();
        Models.Dispose();
        skybox.Dispose();
        toneMapper.Dispose();
        Scene.Dispose();
        environment.Dispose();
        Context.Dispose();
        titleFont.Dispose();
        bodyFont.Dispose();
        overlayFill.Dispose();
        overlayStroke.Dispose();
        overlayText.Dispose();
        overlayAccent.Dispose();
        Device.Dispose();
    }

    private void DrawScene(RenderPass pass)
    {
        Models.Begin(pass, Camera, Lighting);
        Scene.Draw(Models);
        Models.End();

        // After the opaque models: the sky only fills the pixels nothing covered.
        if (options.Skybox) skybox.Draw(pass, Camera, Lighting);
    }

    private void DrawHud(RenderPass pass)
    {
        Sprites.Begin(pass);
        hud.Draw(Sprites, target.Width, target.Height, Stats, Scene, Models.LastInstanceCount, Models.LastDrawCallCount, Orbit.Yaw);
        Sprites.End();
    }

    private void DrawSkiaOverlay(SKCanvas canvas, int width, int height)
    {
        const string Title = "Cube Pets party";
        string line1 = "SkiaSharp overlay on the same GPU texture (" + target.Tier + ")";
        string line2 = "models: Kenney Cube Pets and Survival Kit, Avian - CC0";
        float textWidth = Math.Max(titleFont.MeasureText(Title), Math.Max(bodyFont.MeasureText(line1), bodyFont.MeasureText(line2)));
        SKRect box = new SKRect(width - textWidth - 46, height - 92, width - 14, height - 14);
        canvas.DrawRoundRect(box, 12, 12, overlayFill);
        canvas.DrawRoundRect(box, 12, 12, overlayStroke);
        canvas.DrawText(Title, box.Left + 16, box.Top + 26, SKTextAlign.Left, titleFont, overlayAccent);
        canvas.DrawText(line1, box.Left + 16, box.Top + 48, SKTextAlign.Left, bodyFont, overlayText);
        canvas.DrawText(line2, box.Left + 16, box.Top + 68, SKTextAlign.Left, bodyFont, overlayText);
    }
}
