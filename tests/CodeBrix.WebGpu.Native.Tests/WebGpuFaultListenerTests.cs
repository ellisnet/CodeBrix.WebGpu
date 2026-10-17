using System;
using System.Collections.Generic;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// <see cref="WebGpuFaultListener" /> (wgpuCodebrixDeviceAddFaultListener / RemoveFaultListener): fan-out after the
/// device's own uncaptured-error callback, never instead of it. Each test owns its device (errors are provoked).
/// </summary>
public unsafe class WebGpuFaultListenerTests
{
    private static void ProvokeValidationError(Device device)
    {
        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.MapRead | BufferUsage.MapWrite, Size = 16 };
        Buffer buffer = WebGpu.DeviceCreateBuffer(device, &descriptor);
        if (!buffer.IsNull) WebGpu.BufferRelease(buffer);
    }

    private static GpuSession Session()
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

    [Fact]
    public void an_uncaptured_error_reaches_the_host_callback_first_then_the_listener()
    {
        //Arrange
        using GpuSession session = Session();
        using WebGpuFaultListener listener = WebGpuFaultListener.Attach(session.Device);
        List<(ErrorType Type, string Message, int HostCountThen)> seen = new List<(ErrorType, string, int)>();
        listener.UncapturedError += (type, message) => seen.Add((type, message, session.UncapturedErrors.Count));

        //Act
        ProvokeValidationError(session.Device);

        //Assert
        listener.Id.Should().NotBe(0UL);
        session.UncapturedErrors.Should().ContainSingle();
        seen.Should().ContainSingle();
        seen[0].Type.Should().Be(ErrorType.Validation);
        seen[0].Message.Should().NotBeNullOrEmpty();
        seen[0].HostCountThen.Should().Be(1, "the host's own callback ran before the listener");
    }

    [Fact]
    public void two_listeners_both_hear_and_a_disposed_one_never_again()
    {
        //Arrange
        using GpuSession session = Session();
        WebGpuFaultListener first = WebGpuFaultListener.Attach(session.Device);
        using WebGpuFaultListener second = WebGpuFaultListener.Attach(session.Device);
        int firstCount = 0;
        int secondCount = 0;
        first.UncapturedError += (_, _) => firstCount++;
        second.UncapturedError += (_, _) => secondCount++;

        //Act
        ProvokeValidationError(session.Device);
        first.Dispose();
        ProvokeValidationError(session.Device);

        //Assert
        firstCount.Should().Be(1);
        secondCount.Should().Be(2);
        session.UncapturedErrors.Count.Should().Be(2, "the host callback keeps running for every error");
        first.Id.Should().Be(0UL);
        first.Dispose();
    }

    [Fact]
    public void a_listener_cannot_be_disposed_from_inside_its_handler()
    {
        //Arrange
        using GpuSession session = Session();
        using WebGpuFaultListener listener = WebGpuFaultListener.Attach(session.Device);
        Exception thrown = null;
        listener.UncapturedError += (_, _) =>
        {
            try
            {
                listener.Dispose();
            }
            catch (WgpuException exception)
            {
                thrown = exception;
            }
        };

        //Act
        ProvokeValidationError(session.Device);

        //Assert
        thrown.Should().NotBeNull();
        listener.Id.Should().NotBe(0UL);
    }

    [Fact]
    public void the_raw_functions_refuse_bad_input()
    {
        //Arrange
        using GpuSession session = Session();
        CodebrixFaultCallbacks none = default;

        //Act
        ulong noCallbacks = WebGpu.CodebrixDeviceAddFaultListener(session.Device, &none);
        ulong noStruct = WebGpu.CodebrixDeviceAddFaultListener(session.Device, null);
        Status unknownId = WebGpu.CodebrixDeviceRemoveFaultListener(session.Device, 0xFFFF_FFFF_FFFFUL);
        Status nullDevice = WebGpu.CodebrixDeviceRemoveFaultListener(Device.Null, 1);
        Action attachNull = () => WebGpuFaultListener.Attach(Device.Null);

        //Assert
        noCallbacks.Should().Be(0UL);
        noStruct.Should().Be(0UL);
        unknownId.Should().Be(Status.Error);
        nullDevice.Should().Be(Status.Error);
        attachNull.Should().Throw<ArgumentException>();
    }
}
