using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// A failed <c>DevicePoll</c> wait returns false and reports through the device's error path instead of ending
/// the process (wgpu-native-tools/patches/0007). Every test skips, with the reason, when the machine has no
/// adapter.
/// </summary>
public unsafe class DevicePollTests
{
    private static GpuSession CreateSession()
    {
        GpuSession session = GpuSession.CreateForThisPlatform();
        if (session.SkipReason != null)
        {
            string reason = session.SkipReason;
            session.Dispose();
            Assert.Skip(reason);
        }

        return session;
    }

    // Submits `count` empty command buffers on the session's queue and returns the last submission index.
    private static ulong SubmitEmpty(GpuSession session, int count)
    {
        ulong index = 0;
        for (int i = 0; i < count; i++)
        {
            CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(session.Device, null);
            CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
            index = WebGpu.QueueSubmitForIndex(session.Queue, 1, &commands);
            WebGpu.CommandBufferRelease(commands);
            WebGpu.CommandEncoderRelease(encoder);
        }

        return index;
    }

    [Fact]
    public void DevicePoll_with_another_devices_submission_index_returns_false_and_a_scope_captures_the_error()
    {
        //Arrange
        using GpuSession other = CreateSession();
        using GpuSession session = CreateSession();
        ulong foreign = SubmitEmpty(other, 3);
        WebGpu.DevicePoll(other.Device, 1, null);

        //Act
        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.Validation);
        uint result = WebGpu.DevicePoll(session.Device, 1, &foreign);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) scope = session.PopErrorScope();
        ulong own = SubmitEmpty(session, 1);
        uint after = WebGpu.DevicePoll(session.Device, 1, &own);

        //Assert
        foreign.Should().BeGreaterThan(0UL);
        result.Should().Be(0u);
        scope.Type.Should().Be(ErrorType.Validation);
        scope.Message.Should().Contain("submission index");
        after.Should().Be(1u);
        session.UncapturedErrors.Should().BeEmpty();
    }

    [Fact]
    public void DevicePoll_with_a_foreign_submission_index_and_no_scope_reaches_the_uncaptured_error_callback()
    {
        //Arrange
        using GpuSession other = CreateSession();
        using GpuSession session = CreateSession();
        ulong foreign = SubmitEmpty(other, 2);
        WebGpu.DevicePoll(other.Device, 1, null);

        //Act
        uint result = WebGpu.DevicePoll(session.Device, 1, &foreign);

        //Assert
        result.Should().Be(0u);
        session.UncapturedErrors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void DevicePoll_returns_a_clean_WGPUBool()
    {
        //Arrange
        using GpuSession session = CreateSession();
        ulong index = SubmitEmpty(session, 1);

        //Act
        uint waited = WebGpu.DevicePoll(session.Device, 1, &index);
        uint polled = WebGpu.DevicePoll(session.Device, 0, null);

        //Assert (all 32 bits: the engine once returned a one-byte bool with garbage above it)
        waited.Should().Be(1u);
        polled.Should().Be(1u);
    }

    [Fact]
    public void DeviceStartGraphicsDebuggerCapture_returns_a_clean_WGPUBool()
    {
        //Arrange
        using GpuSession session = CreateSession();

        //Act
        uint started = WebGpu.DeviceStartGraphicsDebuggerCapture(session.Device);
        WebGpu.DeviceStopGraphicsDebuggerCapture(session.Device);

        //Assert
        started.Should().Be(1u);
    }

    [Fact]
    public void DevicePoll_and_InstanceProcessEvents_on_a_destroyed_device_do_not_end_the_process()
    {
        //Arrange
        using GpuSession session = CreateSession();
        ulong index = SubmitEmpty(session, 1);
        WebGpu.DeviceDestroy(session.Device);

        //Act
        uint waited = WebGpu.DevicePoll(session.Device, 1, &index);
        uint polled = WebGpu.DevicePoll(session.Device, 0, null);
        WebGpu.InstanceProcessEvents(session.Instance);

        //Assert (reaching this line is the point: the process is alive)
        (waited <= 1u).Should().BeTrue();
        (polled <= 1u).Should().BeTrue();
    }
}
