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
/// <see cref="WebGpuSkiaDevice" /> creates it (and the SkiaSharp GRContext on it), and a
/// <see cref="GraphicsContext" /> wraps it. Each frame:
/// <list type="number">
/// <item>the 3D scene and the sprite-batch HUD render into the texture of a zero-copy <see cref="WebGpuSkiaTarget" />,
/// recorded into the encoder the target's frame protocol hands out;</item>
/// <item>SkiaSharp draws its overlay straight onto the same pixels;</item>
/// <item>headless: the frame is read back through Skia; live: the target's texture is drawn onto the window surface
/// as one full-screen sprite and presented.</item>
/// </list>
/// </summary>
internal sealed class ViewerRenderer : IDisposable
{
    private static readonly Color4 Sky = Color4.FromBytes(150, 196, 232);
    private readonly SKPaint overlayFill = new SKPaint { Color = new SKColor(16, 20, 28, 190), IsAntialias = true };
    private readonly SKPaint overlayStroke = new SKPaint { Color = new SKColor(255, 200, 87), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
    private readonly SKPaint overlayText = new SKPaint { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint overlayAccent = new SKPaint { Color = new SKColor(255, 200, 87), IsAntialias = true };
    private readonly SKFont titleFont;
    private readonly SKFont bodyFont;
    private readonly HudOverlay hud;
    private WebGpuSkiaTarget target;
    private GpuTexture depth;
    private GpuTexture targetTexture;

    /// <summary>Creates the device, loads the scene and makes a target of the given size.</summary>
    /// <param name="assets">The Assets folder.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    public ViewerRenderer(string assets, int width, int height)
    {
        Device = WebGpuSkiaDevice.Create();
        if (!Device.IsAvailable) throw new InvalidOperationException("No WebGPU device: " + Device.Reason);
        GrContext = Device.CreateGrContext() ?? throw new InvalidOperationException("No SkiaSharp GRContext on the WebGPU device: " + Device.GrContextReason);
        Context = GraphicsContext.Wrap(Device.Device, Device.Queue, Device.Instance, Device.Adapter);
        Meshes = new MeshRenderer(Context);
        Sprites = new SpriteBatch(Context);
        Scene = new ViewerScene(Context, Meshes, assets);
        hud = new HudOverlay(Context);
        SKTypeface typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold) ?? SKTypeface.Default;
        titleFont = new SKFont(typeface, 20);
        bodyFont = new SKFont(SKTypeface.FromFamilyName("sans-serif") ?? SKTypeface.Default, 14);
        Camera = new Camera { FieldOfView = 50f * MathF.PI / 180f, NearPlane = 0.1f, FarPlane = 200 };
        Orbit = new OrbitCamera { Target = new Vector3(0, 0.9f, 0), Distance = 10.5f, Pitch = 0.40f };
        Lighting = new SceneLighting
        {
            LightDirection = new Vector3(-0.45f, -1f, -0.35f),
            LightColor = new Color4(1f, 0.96f, 0.88f),
            AmbientColor = new Color4(0.42f, 0.45f, 0.52f),
        };
        Resize(width, height);
    }

    /// <summary>The shared device (owned here).</summary>
    public WebGpuSkiaDevice Device { get; }

    /// <summary>The SkiaSharp context on the device.</summary>
    public GRContext GrContext { get; }

    /// <summary>The Graphics context wrapping the device.</summary>
    public GraphicsContext Context { get; }

    /// <summary>The 3D renderer.</summary>
    public MeshRenderer Meshes { get; }

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

    /// <summary>The lighting.</summary>
    public SceneLighting Lighting { get; }

    /// <summary>The frame statistics shown by the HUD.</summary>
    public FrameStats Stats { get; } = new FrameStats();

    /// <summary>The Skia target the frame is rendered into.</summary>
    public WebGpuSkiaTarget Target => target;

    /// <summary>Recreates the target and depth buffer for a new size.</summary>
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
        target?.Dispose();
        target = WebGpuSkiaTarget.Create(Device, GrContext, width, height);
        if (!target.IsAvailable) throw new InvalidOperationException("No Skia target: " + target.TierReason);
        depth = Context.CreateDepthTexture(width, height, TextureFormat.Depth24Plus, "scene depth");
        targetTexture = Context.WrapTexture(target.Texture, target.TextureView, target.Format, width, height, "skia target");
    }

    /// <summary>Renders the scene, the HUD and the Skia overlay into the target.</summary>
    /// <param name="time">The scene time in seconds.</param>
    public void RenderFrame(float time)
    {
        Scene.Update(time);
        Orbit.Yaw = 0.7f + (time * 0.18f) + UserYaw;
        Orbit.Apply(Camera);

        CommandEncoder encoder = target.BeginWebGpuPass();
        using (GraphicsFrame frame = Context.BeginFrame(encoder))
        {
            RenderTargetView view = new RenderTargetView(target.TextureView, target.Format, target.Width, target.Height).WithDepth(depth);
            using (RenderPass pass = frame.BeginRenderPass(view, Sky))
            {
                Meshes.Begin(pass, Camera, Lighting);
                Scene.Draw(Meshes);
                Meshes.End();

                Sprites.Begin(pass);
                hud.Draw(Sprites, target.Width, target.Height, Stats, Scene, Meshes.LastDrawCallCount, Orbit.Yaw);
                Sprites.End();
            }

            frame.Submit();
        }

        target.EndWebGpuPass();
        DrawSkiaOverlay(target.Surface.Canvas, target.Width, target.Height);
    }

    /// <summary>Draws the rendered frame onto a window surface as one full-screen sprite and presents it.</summary>
    /// <param name="surface">The configured surface.</param>
    /// <returns>True when a frame was presented.</returns>
    public bool PresentTo(WindowSurface surface)
    {
        if (surface.TryAcquire(out RenderTargetView view) != SurfaceAcquireResult.Ready) return false;
        CommandEncoder encoder = target.BeginWebGpuPass();
        using (GraphicsFrame frame = Context.BeginFrame(encoder))
        {
            using (RenderPass pass = frame.BeginRenderPass(view, Color4.Black, null))
            {
                Sprites.Begin(pass, null, BlendPreset.Opaque);
                Sprites.Draw(targetTexture, new RectangleF(0, 0, view.Width, view.Height), Color4.White);
                Sprites.End();
            }

            frame.Submit();
        }

        target.EndWebGpuPass();
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
        target?.Dispose();
        Context.Dispose();
        titleFont.Dispose();
        bodyFont.Dispose();
        overlayFill.Dispose();
        overlayStroke.Dispose();
        overlayText.Dispose();
        overlayAccent.Dispose();
        Device.Dispose();
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
