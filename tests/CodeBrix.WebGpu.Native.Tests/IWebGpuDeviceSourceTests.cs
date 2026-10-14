using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// The device-sharing contract: a fake owner implements <see cref="IWebGpuDeviceSource" /> over a raw session, and a
/// consumer written only against the interface uses the device the documented way (its own references, the shared
/// queue).
/// </summary>
public sealed unsafe class IWebGpuDeviceSourceTests : IClassFixture<GpuFixture>
{
    private readonly GpuSession session;

    public IWebGpuDeviceSourceTests(GpuFixture fixture)
    {
        session = fixture.Session;
    }

    private void Require() => Assert.SkipWhen(session.SkipReason != null, session.SkipReason ?? string.Empty);

    /// <summary>A minimal owner: hands out a session's handles (or none) without taking references.</summary>
    private sealed class FakeDeviceSource : IWebGpuDeviceSource
    {
        public FakeDeviceSource(GpuSession session, bool hasHandler)
        {
            if (session != null)
            {
                Instance = session.Instance;
                Adapter = session.Adapter;
                Device = session.Device;
                Queue = session.Queue;
            }

            HasUncapturedErrorHandler = hasHandler;
        }

        public Instance Instance { get; }

        public Adapter Adapter { get; }

        public Device Device { get; }

        public Queue Queue { get; }

        public BackendType Backend => Device.IsNull ? BackendType.Undefined : WebGpu.CodebrixDeviceGetBackend(Device);

        public bool HasUncapturedErrorHandler { get; }
    }

    /// <summary>A consumer that knows only the interface: takes its own references, submits one empty command buffer, releases.</summary>
    private static bool UseLikeAConsumer(IWebGpuDeviceSource source)
    {
        if (source.Device.IsNull || source.Queue.IsNull) return false;
        Device device = source.Device;
        Queue queue = source.Queue;
        WebGpu.DeviceAddRef(device);
        WebGpu.QueueAddRef(queue);
        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(device, null);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(queue, 1, &commands);
        WebGpu.CommandBufferRelease(commands);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.DevicePoll(device, 1, null);
        WebGpu.QueueRelease(queue);
        WebGpu.DeviceRelease(device);
        return true;
    }

    [Fact]
    public void A_source_without_a_device_reports_null_handles_and_an_undefined_backend()
    {
        //Arrange
        IWebGpuDeviceSource source = new FakeDeviceSource(null, false);

        //Act
        bool used = UseLikeAConsumer(source);

        //Assert
        used.Should().BeFalse();
        source.Instance.IsNull.Should().BeTrue();
        source.Adapter.IsNull.Should().BeTrue();
        source.Device.IsNull.Should().BeTrue();
        source.Queue.IsNull.Should().BeTrue();
        source.Backend.Should().Be(BackendType.Undefined);
        source.HasUncapturedErrorHandler.Should().BeFalse();
    }

    [Fact]
    public void A_consumer_uses_the_sources_device_with_its_own_references()
    {
        //Arrange
        Require();
        IWebGpuDeviceSource source = new FakeDeviceSource(session, true);

        //Act
        bool used = UseLikeAConsumer(source);
        bool usedAgain = UseLikeAConsumer(source);

        //Assert
        used.Should().BeTrue();
        usedAgain.Should().BeTrue();
        source.Device.Should().Be(session.Device);
        source.Queue.Should().Be(session.Queue);
        source.Instance.Should().Be(session.Instance);
        source.Adapter.Should().Be(session.Adapter);
        source.Backend.Should().Be(GpuSession.ExpectedBackendType);
        source.HasUncapturedErrorHandler.Should().BeTrue();
        session.UncapturedErrors.Should().BeEmpty();
    }
}
