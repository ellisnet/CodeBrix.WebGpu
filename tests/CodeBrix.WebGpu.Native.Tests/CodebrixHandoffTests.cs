using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CodeBrix.WebGpu.BindingGenerator.Model;
using CodeBrix.WebGpu.BindingGenerator.Rendering;
using SilverAssertions;
using Xunit;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// The native-module hand-off structs (<see cref="CodebrixDeviceHandoff" />, <see cref="CodebrixFrameHandoff" />):
/// their layout is pinned and equal to the generated C header's, they are filled from any device source / texture
/// target (fakes here; the Skia types in the Skia tests), and a "module" written only against the hand-off and the
/// function table renders into a target's texture inside its pass.
/// </summary>
public sealed unsafe class CodebrixHandoffTests : IClassFixture<GpuFixture>
{
    private static readonly string Root =
        BindingGenerator.BindingGenerator.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Cannot find CodeBrix.WebGpu.slnx");

    private readonly GpuSession session;

    public CodebrixHandoffTests(GpuFixture fixture)
    {
        session = fixture.Session;
    }

    private void Require() => Assert.SkipWhen(session.SkipReason != null, session.SkipReason ?? string.Empty);

    private sealed class FakeDeviceSource : IWebGpuDeviceSource
    {
        public Instance Instance { get; set; }

        public Adapter Adapter { get; set; }

        public Device Device { get; set; }

        public Queue Queue { get; set; }

        public BackendType Backend { get; set; }

        public bool HasUncapturedErrorHandler { get; set; }
    }

    /// <summary>A target with made-up values (never used for GPU work), or one owning a real texture.</summary>
    private sealed class FakeTextureTarget : IWebGpuTextureTarget, IDisposable
    {
        private readonly Device device;
        private readonly Queue queue;
        private CommandEncoder encoder;

        public FakeTextureTarget()
        {
            Texture = new Texture((IntPtr)0x1000);
            TextureView = new TextureView((IntPtr)0x2000);
            Format = TextureFormat.BGRA8UnormSrgb;
            Width = 640;
            Height = 360;
            SampleCount = 4;
            Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding;
        }

        public FakeTextureTarget(GpuSession session, int width, int height)
        {
            device = session.Device;
            queue = session.Queue;
            Format = TextureFormat.RGBA8Unorm;
            Width = width;
            Height = height;
            SampleCount = 1;
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc;
            TextureDescriptor descriptor = new TextureDescriptor
            {
                Usage = Usage,
                Dimension = TextureDimension.Dimension2D,
                Size = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 },
                Format = Format,
                MipLevelCount = 1,
                SampleCount = 1,
            };
            Texture = WebGpu.DeviceCreateTexture(device, &descriptor);
            TextureView = WebGpu.TextureCreateView(Texture, null);
        }

        public Texture Texture { get; }

        public TextureView TextureView { get; }

        public TextureFormat Format { get; }

        public int Width { get; }

        public int Height { get; }

        public int SampleCount { get; }

        public TextureUsage Usage { get; }

        public bool IsInWebGpuPass { get; private set; }

        public CommandEncoder BeginWebGpuPass()
        {
            if (IsInWebGpuPass) throw new InvalidOperationException("A pass is already open.");
            encoder = device.IsNull ? new CommandEncoder((IntPtr)0x3000) : WebGpu.DeviceCreateCommandEncoder(device, null);
            IsInWebGpuPass = true;
            return encoder;
        }

        public void EndWebGpuPass()
        {
            if (!IsInWebGpuPass) throw new InvalidOperationException("No pass is open.");
            IsInWebGpuPass = false;
            if (device.IsNull) return;
            CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
            WebGpu.CommandEncoderRelease(encoder);
            WebGpu.QueueSubmit(queue, 1, &commands);
            WebGpu.CommandBufferRelease(commands);
        }

        public void Dispose()
        {
            if (device.IsNull) return;
            WebGpu.TextureViewRelease(TextureView);
            WebGpu.TextureRelease(Texture);
        }
    }

    // ------------------------------------------------------------------ layout

    [Fact]
    public void the_layouts_are_pinned()
    {
        //Assert
        sizeof(CodebrixDeviceHandoff).Should().Be(64);
        new[] { "Size", "Version", "ApiVersion", "Backend", "Flags", "Reserved", "Procs", "Instance", "Adapter", "Device", "Queue" }
            .Select(name => (int)Marshal.OffsetOf<CodebrixDeviceHandoff>(name))
            .Should().Equal(0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 56);
        sizeof(CodebrixFrameHandoff).Should().Be(56);
        new[] { "Size", "Version", "Format", "Width", "Height", "SampleCount", "Usage", "Texture", "TextureView", "CommandEncoder" }
            .Select(name => (int)Marshal.OffsetOf<CodebrixFrameHandoff>(name))
            .Should().Equal(0, 4, 8, 12, 16, 20, 24, 32, 40, 48);
        CodebrixDeviceHandoff.CurrentVersion.Should().Be(1u);
        CodebrixFrameHandoff.CurrentVersion.Should().Be(1u);
    }

    [Fact]
    public void the_layouts_match_the_generated_c_header()
    {
        //Arrange
        string header = File.ReadAllText(Path.Combine(Root, ModuleRenderer.CHeaderPath));
        ApiModel model = BindingGenerator.BindingGenerator.LoadModel(Root);
        ModuleInterop interop = new ModuleInterop(model, ProcNotes.Empty);
        Dictionary<string, (Type Type, Dictionary<string, string> Fields)> managed = new Dictionary<string, (Type, Dictionary<string, string>)>
        {
            ["WGPUCodebrixDeviceHandoff"] = (typeof(CodebrixDeviceHandoff), interop.HandoffStructs[0].Fields.ToDictionary(f => f.CName, f => f.CsName)),
            ["WGPUCodebrixFrameHandoff"] = (typeof(CodebrixFrameHandoff), interop.HandoffStructs[1].Fields.ToDictionary(f => f.CName, f => f.CsName)),
            ["WGPUCodebrixProcTable"] = (typeof(CodebrixProcTable), interop.ProcTableHeader.ToDictionary(f => f.CName, f => f.CsName)),
        };
        managed["WGPUCodebrixProcTable"].Fields[interop.Procs[0].MemberName] = interop.Procs[0].Function.CsName;
        List<string> checkedAsserts = new List<string>();
        List<string> problems = new List<string>();

        //Act
        foreach (Match match in Regex.Matches(header, @"WGPU_CODEBRIX_STATIC_ASSERT\(sizeof\((\w+)\) == (\d+),"))
        {
            int size = Marshal.SizeOf(managed[match.Groups[1].Value].Type);
            if (size != int.Parse(match.Groups[2].Value)) problems.Add("sizeof " + match.Groups[1].Value);
            checkedAsserts.Add(match.Value);
        }

        foreach (Match match in Regex.Matches(header, @"WGPU_CODEBRIX_STATIC_ASSERT\(offsetof\((\w+), (\w+)\) == (\d+),"))
        {
            (Type type, Dictionary<string, string> fields) = managed[match.Groups[1].Value];
            int offset = (int)Marshal.OffsetOf(type, fields[match.Groups[2].Value]);
            if (offset != int.Parse(match.Groups[3].Value)) problems.Add("offsetof " + match.Groups[1].Value + "." + match.Groups[2].Value);
            checkedAsserts.Add(match.Value);
        }

        //Assert
        checkedAsserts.Count.Should().Be(3 + 11 + 10 + 6);
        problems.Should().BeEmpty();
        header.Should().Contain("#define WGPU_CODEBRIX_DEVICE_HANDOFF_VERSION (" + CodebrixDeviceHandoff.CurrentVersion + ")");
        header.Should().Contain("#define WGPU_CODEBRIX_FRAME_HANDOFF_VERSION (" + CodebrixFrameHandoff.CurrentVersion + ")");
        header.Should().Contain("#define WGPU_CODEBRIX_PROC_TABLE_FUNCTION_COUNT (" + CodebrixProcTable.EntryCount + ")");
        header.Should().Contain("#define WGPU_CODEBRIX_PROC_TABLE_SIGNATURE_HASH (UINT64_C(0x" + CodebrixProcTable.ExpectedSignatureHash.ToString("X16") + "))");
    }

    // ------------------------------------------------------------------ filling from fakes

    [Fact]
    public void a_device_hand_off_is_filled_from_any_device_source()
    {
        //Arrange
        FakeDeviceSource source = new FakeDeviceSource
        {
            Instance = new Instance((IntPtr)0x10),
            Adapter = new Adapter((IntPtr)0x20),
            Device = new Device((IntPtr)0x30),
            Queue = new Queue((IntPtr)0x40),
            Backend = BackendType.Vulkan,
            HasUncapturedErrorHandler = true,
        };
        CodebrixDeviceHandoff* native = (CodebrixDeviceHandoff*)NativeMemory.AllocZeroed((nuint)sizeof(CodebrixDeviceHandoff));

        //Act
        CodebrixDeviceHandoff.Fill(source, native);
        CodebrixDeviceHandoff copy = *native;
        NativeMemory.Free(native);
        source.HasUncapturedErrorHandler = false;
        source.Device = Device.Null;
        CodebrixDeviceHandoff noDevice = CodebrixDeviceHandoff.From(source);

        //Assert
        copy.Size.Should().Be((uint)sizeof(CodebrixDeviceHandoff));
        copy.Version.Should().Be(CodebrixDeviceHandoff.CurrentVersion);
        copy.ApiVersion.Should().Be(Constants.ApiVersion);
        copy.Backend.Should().Be(BackendType.Vulkan);
        copy.Flags.Should().Be(CodebrixDeviceHandoff.FlagUncapturedErrorHandler);
        copy.Reserved.Should().Be(0u);
        ((nint)copy.Procs).Should().Be((nint)CodeBrixWebGpuNative.GetProcTable());
        copy.Instance.Should().Be(source.Instance);
        copy.Adapter.Should().Be(source.Adapter);
        copy.Device.Handle.Should().Be((IntPtr)0x30);
        copy.Queue.Should().Be(source.Queue);
        noDevice.Flags.Should().Be(0u);
        noDevice.Device.IsNull.Should().BeTrue();
    }

    [Fact]
    public void a_frame_hand_off_is_filled_from_any_texture_target()
    {
        //Arrange
        using FakeTextureTarget target = new FakeTextureTarget();
        CodebrixFrameHandoff outside = default;
        CodebrixFrameHandoff inside = default;

        //Act
        CodebrixFrameHandoff.Fill(target, CommandEncoder.Null, ref outside);
        CommandEncoder encoder = CodebrixFrameHandoff.BeginWebGpuPass(target, &inside);
        bool open = target.IsInWebGpuPass;
        target.EndWebGpuPass();

        //Assert
        outside.Size.Should().Be((uint)sizeof(CodebrixFrameHandoff));
        outside.Version.Should().Be(CodebrixFrameHandoff.CurrentVersion);
        outside.Format.Should().Be(TextureFormat.BGRA8UnormSrgb);
        outside.Width.Should().Be(640u);
        outside.Height.Should().Be(360u);
        outside.SampleCount.Should().Be(4u);
        outside.Usage.Should().Be(TextureUsage.RenderAttachment | TextureUsage.TextureBinding);
        outside.Texture.Should().Be(target.Texture);
        outside.TextureView.Should().Be(target.TextureView);
        outside.CommandEncoder.IsNull.Should().BeTrue();
        open.Should().BeTrue();
        inside.CommandEncoder.Should().Be(encoder);
        inside.CommandEncoder.Handle.Should().Be((IntPtr)0x3000);
        inside.Texture.Should().Be(target.Texture);
        CodebrixFrameHandoff.From(target, encoder).Should().Be(inside);
    }

    [Fact]
    public void fill_refuses_null_arguments()
    {
        //Arrange
        using FakeTextureTarget target = new FakeTextureTarget();

        //Act
        Action nullSource = () => CodebrixDeviceHandoff.From(null);
        Action nullDeviceDestination = () => CodebrixDeviceHandoff.Fill(new FakeDeviceSource(), (CodebrixDeviceHandoff*)null);
        Action nullTarget = () => CodebrixFrameHandoff.From(null, CommandEncoder.Null);
        Action nullFrameDestination = () => CodebrixFrameHandoff.Fill(target, CommandEncoder.Null, (CodebrixFrameHandoff*)null);
        Action nullPassDestination = () => CodebrixFrameHandoff.BeginWebGpuPass(target, null);

        //Assert
        nullSource.Should().Throw<ArgumentNullException>();
        nullDeviceDestination.Should().Throw<ArgumentNullException>();
        nullTarget.Should().Throw<ArgumentNullException>();
        nullFrameDestination.Should().Throw<ArgumentNullException>();
        nullPassDestination.Should().Throw<ArgumentNullException>();
        target.IsInWebGpuPass.Should().BeFalse();
    }

    // ------------------------------------------------------------------ a module on the hand-offs

    /// <summary>
    /// What a native module does with the two hand-off pointers, written against them and the function table only:
    /// check versions, clear the target in the provided encoder and copy it into a buffer it creates on the device.
    /// </summary>
    private static Buffer ModuleFrame(CodebrixDeviceHandoff* device, CodebrixFrameHandoff* frame, uint bytesPerRow)
    {
        if (device->Version != 1 || device->Size < 64 || device->ApiVersion != 1 || frame->Version != 1 || frame->CommandEncoder.IsNull) return Buffer.Null;
        CodebrixProcTable* t = device->Procs;
        if (t->FunctionCount != CodebrixProcTable.EntryCount || t->SignatureHash != CodebrixProcTable.ExpectedSignatureHash) return Buffer.Null;

        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.CopyDst | BufferUsage.MapRead, Size = bytesPerRow * frame->Height };
        Buffer readback = t->DeviceCreateBuffer(device->Device, &descriptor);
        RenderPassColorAttachment color = new RenderPassColorAttachment
        {
            View = frame->TextureView,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 0, G = 1, B = 0.5, A = 1 },
        };
        RenderPassDescriptor pass = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &color };
        RenderPassEncoder renderPass = t->CommandEncoderBeginRenderPass(frame->CommandEncoder, &pass);
        t->RenderPassEncoderEnd(renderPass);
        t->RenderPassEncoderRelease(renderPass);
        TexelCopyTextureInfo source = new TexelCopyTextureInfo { Texture = frame->Texture, Aspect = TextureAspect.All };
        TexelCopyBufferInfo copy = new TexelCopyBufferInfo { Buffer = readback, Layout = new TexelCopyBufferLayout { BytesPerRow = bytesPerRow, RowsPerImage = frame->Height } };
        Extent3D size = new Extent3D { Width = frame->Width, Height = frame->Height, DepthOrArrayLayers = 1 };
        t->CommandEncoderCopyTextureToBuffer(frame->CommandEncoder, &source, &copy, &size);
        return readback;
    }

    [Fact]
    public void a_module_renders_into_a_target_through_the_hand_offs()
    {
        //Arrange
        Require();
        const int Width = 16;
        const int Height = 8;
        const uint BytesPerRow = 256;
        FakeDeviceSource source = new FakeDeviceSource
        {
            Instance = session.Instance,
            Adapter = session.Adapter,
            Device = session.Device,
            Queue = session.Queue,
            Backend = WebGpu.CodebrixDeviceGetBackend(session.Device),
            HasUncapturedErrorHandler = true,
        };
        using FakeTextureTarget target = new FakeTextureTarget(session, Width, Height);
        CodebrixDeviceHandoff* device = (CodebrixDeviceHandoff*)NativeMemory.AllocZeroed((nuint)sizeof(CodebrixDeviceHandoff));
        CodebrixFrameHandoff* frame = (CodebrixFrameHandoff*)NativeMemory.AllocZeroed((nuint)sizeof(CodebrixFrameHandoff));
        CodebrixDeviceHandoff.Fill(source, device);

        //Act
        CommandEncoder encoder = CodebrixFrameHandoff.BeginWebGpuPass(target, frame);
        Buffer readback = ModuleFrame(device, frame, BytesPerRow);
        target.EndWebGpuPass();
        MapAsyncStatus status = GpuSession.MapRead(session.Device, readback, BytesPerRow * Height);
        byte* pixels = (byte*)WebGpu.BufferGetConstMappedRange(readback, 0, BytesPerRow * Height);
        int last = (int)(BytesPerRow * (Height - 1)) + ((Width - 1) * 4);
        (byte R, byte G, byte B, byte A) pixel = (pixels[last], pixels[last + 1], pixels[last + 2], pixels[last + 3]);
        WebGpu.BufferUnmap(readback);
        WebGpu.BufferRelease(readback);
        NativeMemory.Free(frame);
        NativeMemory.Free(device);

        //Assert
        encoder.IsNull.Should().BeFalse();
        readback.IsNull.Should().BeFalse();
        status.Should().Be(MapAsyncStatus.Success);
        pixel.R.Should().Be(0);
        pixel.G.Should().Be(255);
        pixel.B.Should().BeInRange(127, 128);
        pixel.A.Should().Be(255);
        session.UncapturedErrors.Should().BeEmpty();
    }
}
