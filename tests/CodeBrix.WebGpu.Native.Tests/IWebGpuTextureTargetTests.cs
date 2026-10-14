using System;
using SilverAssertions;
using Xunit;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// The texture-sharing contract: a fake owner implements <see cref="IWebGpuTextureTarget" /> over its own texture
/// (Begin hands out an encoder it owns, End finishes and submits it), and a consumer written only against the
/// interface renders into the texture inside the pass.
/// </summary>
public sealed unsafe class IWebGpuTextureTargetTests : IClassFixture<GpuFixture>
{
    private const int Width = 8;
    private const int Height = 4;
    private readonly GpuSession session;

    public IWebGpuTextureTargetTests(GpuFixture fixture)
    {
        session = fixture.Session;
    }

    private void Require() => Assert.SkipWhen(session.SkipReason != null, session.SkipReason ?? string.Empty);

    /// <summary>A minimal owner of one RGBA texture following the pass protocol.</summary>
    private sealed class FakeTextureTarget : IWebGpuTextureTarget, IDisposable
    {
        private readonly Device device;
        private readonly Queue queue;
        private CommandEncoder encoder;
        private bool disposed;

        public FakeTextureTarget(GpuSession session)
        {
            device = session.Device;
            queue = session.Queue;
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc;
            TextureDescriptor descriptor = new TextureDescriptor
            {
                Usage = Usage,
                Dimension = TextureDimension.Dimension2D,
                Size = new Extent3D { Width = (uint)IWebGpuTextureTargetTests.Width, Height = (uint)IWebGpuTextureTargetTests.Height, DepthOrArrayLayers = 1 },
                Format = TextureFormat.RGBA8Unorm,
                MipLevelCount = 1,
                SampleCount = 1,
            };
            Texture = WebGpu.DeviceCreateTexture(device, &descriptor);
            TextureView = WebGpu.TextureCreateView(Texture, null);
        }

        public Texture Texture { get; private set; }

        public TextureView TextureView { get; private set; }

        public TextureFormat Format => TextureFormat.RGBA8Unorm;

        public int Width => IWebGpuTextureTargetTests.Width;

        public int Height => IWebGpuTextureTargetTests.Height;

        public int SampleCount => 1;

        public TextureUsage Usage { get; }

        public bool IsInWebGpuPass { get; private set; }

        public int SubmittedPasses { get; private set; }

        public CommandEncoder BeginWebGpuPass()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsInWebGpuPass) throw new InvalidOperationException("A pass is already open.");
            encoder = WebGpu.DeviceCreateCommandEncoder(device, null);
            IsInWebGpuPass = true;
            return encoder;
        }

        public void EndWebGpuPass()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsInWebGpuPass) throw new InvalidOperationException("No pass is open.");
            IsInWebGpuPass = false;
            CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
            WebGpu.CommandEncoderRelease(encoder);
            encoder = CommandEncoder.Null;
            WebGpu.QueueSubmit(queue, 1, &commands);
            WebGpu.CommandBufferRelease(commands);
            SubmittedPasses++;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!encoder.IsNull) WebGpu.CommandEncoderRelease(encoder);
            WebGpu.TextureViewRelease(TextureView);
            WebGpu.TextureRelease(Texture);
            TextureView = TextureView.Null;
            Texture = Texture.Null;
        }
    }

    /// <summary>A consumer that knows only the interface: clears the target and copies it out, inside the target's pass.</summary>
    private static void ClearAndCopyOut(IWebGpuTextureTarget target, Buffer destination, uint bytesPerRow)
    {
        CommandEncoder encoder = target.BeginWebGpuPass();
        RenderPassColorAttachment color = new RenderPassColorAttachment
        {
            View = target.TextureView,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 1, G = 0.5, B = 0, A = 1 },
        };
        RenderPassDescriptor pass = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &color };
        RenderPassEncoder renderPass = WebGpu.CommandEncoderBeginRenderPass(encoder, &pass);
        WebGpu.RenderPassEncoderEnd(renderPass);
        WebGpu.RenderPassEncoderRelease(renderPass);
        TexelCopyTextureInfo source = new TexelCopyTextureInfo { Texture = target.Texture, Aspect = TextureAspect.All };
        TexelCopyBufferInfo copy = new TexelCopyBufferInfo { Buffer = destination, Layout = new TexelCopyBufferLayout { BytesPerRow = bytesPerRow, RowsPerImage = (uint)target.Height } };
        Extent3D size = new Extent3D { Width = (uint)target.Width, Height = (uint)target.Height, DepthOrArrayLayers = 1 };
        WebGpu.CommandEncoderCopyTextureToBuffer(encoder, &source, &copy, &size);
        target.EndWebGpuPass();
    }

    [Fact]
    public void A_consumer_renders_into_the_target_inside_its_pass()
    {
        //Arrange
        Require();
        using FakeTextureTarget target = new FakeTextureTarget(session);
        const uint BytesPerRow = 256;
        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.CopyDst | BufferUsage.MapRead, Size = BytesPerRow * Height };
        Buffer readback = WebGpu.DeviceCreateBuffer(session.Device, &descriptor);

        //Act
        ClearAndCopyOut(target, readback, BytesPerRow);
        MapAsyncStatus status = GpuSession.MapRead(session.Device, readback, BytesPerRow * Height);
        byte* pixels = (byte*)WebGpu.BufferGetConstMappedRange(readback, 0, BytesPerRow * Height);
        (byte R, byte G, byte B, byte A) last = (pixels[(BytesPerRow * (Height - 1)) + ((Width - 1) * 4)], pixels[(BytesPerRow * (Height - 1)) + ((Width - 1) * 4) + 1], pixels[(BytesPerRow * (Height - 1)) + ((Width - 1) * 4) + 2], pixels[(BytesPerRow * (Height - 1)) + ((Width - 1) * 4) + 3]);
        WebGpu.BufferUnmap(readback);
        WebGpu.BufferRelease(readback);

        //Assert
        status.Should().Be(MapAsyncStatus.Success);
        last.R.Should().Be(255);
        last.G.Should().BeInRange(127, 128);
        last.B.Should().Be(0);
        last.A.Should().Be(255);
        target.SubmittedPasses.Should().Be(1);
        target.IsInWebGpuPass.Should().BeFalse();
        session.UncapturedErrors.Should().BeEmpty();
    }

    [Fact]
    public void The_target_reports_its_texture_and_enforces_the_pass_protocol()
    {
        //Arrange
        Require();
        FakeTextureTarget fake = new FakeTextureTarget(session);
        IWebGpuTextureTarget target = fake;

        //Act
        Action endWithoutBegin = () => target.EndWebGpuPass();
        CommandEncoder encoder = target.BeginWebGpuPass();
        Action beginTwice = () => target.BeginWebGpuPass();
        beginTwice.Should().Throw<InvalidOperationException>();
        target.EndWebGpuPass();
        uint nativeWidth = WebGpu.TextureGetWidth(target.Texture);
        TextureFormat nativeFormat = WebGpu.TextureGetFormat(target.Texture);
        uint nativeSamples = WebGpu.TextureGetSampleCount(target.Texture);
        fake.Dispose();
        Action beginDisposed = () => target.BeginWebGpuPass();

        //Assert
        endWithoutBegin.Should().Throw<InvalidOperationException>();
        encoder.IsNull.Should().BeFalse();
        nativeWidth.Should().Be((uint)Width);
        nativeFormat.Should().Be(target.Format);
        nativeSamples.Should().Be((uint)target.SampleCount);
        (target.Usage & TextureUsage.RenderAttachment).Should().Be(TextureUsage.RenderAttachment);
        beginDisposed.Should().Throw<ObjectDisposedException>();
        target.Texture.IsNull.Should().BeTrue();
        target.TextureView.IsNull.Should().BeTrue();
    }
}
