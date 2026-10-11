using System;
using System.Linq;
using CodeBrix.WebGpu.BindingGenerator.Model;
using CodeBrix.WebGpu.BindingGenerator.Yaml;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

public class ApiModelBuilderTests
{
    private static readonly ApiModel Model = TestPaths.FixtureModel();

    private static ApiEnumValue EnumValue(string enumName, string member) =>
        Model.Enums.Single(e => e.CsName == enumName).Values.Single(v => v.CsName == member);

    private static ApiStruct Struct(string name) => Model.Structs.Single(s => s.CsName == name);

    private static ApiFunction Function(string cName) => Model.Functions.Single(f => f.CName == cName);

    private const string ExtraHeader =
        "copyright: c\nname: extra\nenum_prefix: 0x0005\ndoc: d\nconstants: []\ntypedefs: []\nbitflags: []\n"
        + "callbacks: []\nfunctions: []\nobjects: []\n";

    [Fact]
    public void Build_numbers_core_enum_entries_by_index()
    {
        //Assert
        EnumValue("Status", "Success").Value.Should().Be(0x00000001u);
        EnumValue("CallbackMode", "AllowSpontaneous").Value.Should().Be(0x00000002u);
    }

    [Fact]
    public void Build_uses_an_explicit_entry_value()
        => EnumValue("Status", "Error").Value.Should().Be(0x00000007u);

    [Fact]
    public void Build_numbers_extended_enum_entries_in_the_extension_block()
    {
        //Act
        ApiEnumValue value = EnumValue("SType", "ExtExtras");

        //Assert
        value.Value.Should().Be(0x00030001u);
        value.CName.Should().Be("WGPUSType_ExtExtras");
    }

    [Fact]
    public void Build_numbers_an_extension_enum_in_its_own_block()
        => EnumValue("ExtMode", "Second").Value.Should().Be(0x00030001u);

    [Fact]
    public void Build_numbers_a_webgpu_namespaced_extension_enum_in_the_core_block()
    {
        //Assert
        EnumValue("LogLevel", "Off").Value.Should().Be(0x00000000u);
        EnumValue("LogLevel", "V6_0").Value.Should().Be(0x00000005u);
    }

    [Fact]
    public void Build_prefixes_entries_that_start_with_a_digit()
    {
        //Act
        ApiEnumValue value = EnumValue("TextureDimension", "Dimension1D");

        //Assert
        value.Value.Should().Be(1u);
        value.CName.Should().Be("WGPUTextureDimension_1D");
    }

    [Fact]
    public void Build_computes_implicit_and_combined_bitflag_values()
    {
        //Arrange
        ApiBitflag usage = Model.Bitflags.Single(b => b.CsName == "BufferUsage");

        //Assert
        usage.Values.Select(v => v.Value).Should().Equal(0UL, 1UL, 2UL, 4UL, 3UL);
    }

    [Fact]
    public void Build_starts_an_extensible_struct_with_the_chain_pointer()
    {
        //Act
        ApiField first = Struct("SampleDescriptor").Fields[0];

        //Assert
        first.CName.Should().Be("nextInChain");
        first.CsType.Should().Be("ChainedStruct*");
    }

    [Fact]
    public void Build_starts_an_extension_struct_with_the_chain_header_and_finds_its_stype()
    {
        //Act
        ApiStruct extension = Struct("SampleExtension");

        //Assert
        extension.Fields[0].CName.Should().Be("chain");
        extension.Fields[0].CsType.Should().Be("ChainedStruct");
        extension.STypeValue.Should().Be("SampleExtension");
    }

    [Fact]
    public void Build_maps_member_types_exactly()
    {
        //Act
        string[] types = Struct("SampleDescriptor").Fields.Select(f => f.CName + ":" + f.CsType).ToArray();

        //Assert
        types.Should().Equal(
            "nextInChain:ChainedStruct*",
            "label:StringView",
            "size:ulong",
            "offset:nuint",
            "dimension:TextureDimension",
            "usage:BufferUsage",
            "parent:SampleDescriptor*",
            "entryCount:nuint",
            "entries:uint*",
            "flag:uint",
            "onError:UncapturedCallbackInfo");
    }

    [Fact]
    public void Build_resolves_typedefs_to_their_primitive()
        => Struct("ExtExtras").Fields[1].CsType.Should().Be("ulong");

    [Fact]
    public void Build_gives_callback_mode_callbacks_a_mode_field()
    {
        //Act
        ApiStruct info = Model.Callbacks.Single(c => c.CsName == "RequestThing").Info;

        //Assert
        info.Fields.Select(f => f.CName).Should().Equal("nextInChain", "mode", "callback", "userdata1", "userdata2");
        info.Fields[2].CsType.Should().Be("delegate* unmanaged[Cdecl]<Status, Thing, StringView, void*, void*, void>");
    }

    [Fact]
    public void Build_gives_immediate_callbacks_no_mode_field()
        => Model.Callbacks.Single(c => c.CsName == "Uncaptured").Info.Fields.Select(f => f.CName)
            .Should().Equal("nextInChain", "callback", "userdata1", "userdata2");

    [Fact]
    public void Build_treats_a_trailing_userdata_argument_as_a_legacy_callback()
    {
        //Act
        ApiCallback log = Model.Callbacks.Single(c => c.CsName == "Log");

        //Assert
        log.Info.Should().BeNull();
        log.FunctionPointerType.Should().Be("delegate* unmanaged[Cdecl]<LogLevel, void*, void>");
        Function("wgpuSetLogCallback").Parameters[0].CsType.Should().Be(log.FunctionPointerType);
    }

    [Fact]
    public void Build_expands_array_arguments_and_appends_callback_info()
    {
        //Act
        ApiFunction request = Function("wgpuThingRequest");

        //Assert
        request.Parameters.Select(p => p.CsType + " " + p.CsName).Should().Equal(
            "Thing thing", "nuint valueCount", "Thing* values", "RequestThingCallbackInfo callbackInfo");
        request.ReturnType.Should().Be("Future");
    }

    [Fact]
    public void Build_adds_add_ref_and_release_free_members_and_get_proc_address()
    {
        //Act
        string[] names = Model.Functions.Select(f => f.CName).ToArray();

        //Assert
        names.Should().Equal(
            "wgpuCreateThing",
            "wgpuGetProcAddress",
            "wgpuPlainInfoFreeMembers",
            "wgpuSetLogCallback",
            "wgpuThingAddRef",
            "wgpuThingPoll",
            "wgpuThingRelease",
            "wgpuThingRequest");
    }

    [Fact]
    public void Build_passes_the_free_members_struct_by_value()
        => Function("wgpuPlainInfoFreeMembers").Parameters.Single().CsType.Should().Be("PlainInfo");

    [Fact]
    public void Build_maps_optional_typedef_pointers_and_bool_returns()
    {
        //Act
        ApiFunction poll = Function("wgpuThingPoll");

        //Assert
        poll.ReturnType.Should().Be("uint");
        poll.Parameters[1].CsType.Should().Be("ulong*");
    }

    [Fact]
    public void Build_types_constants()
    {
        //Assert
        Model.Constants.Single(c => c.CsName == "Strlen").IsConst.Should().BeFalse();
        Model.Constants.Single(c => c.CsName == "WholeSize").CsValue.Should().Be("ulong.MaxValue");
        Model.Constants.Single(c => c.CsName == "Answer").CsValue.Should().Be("42u");
    }

    [Fact]
    public void Build_lists_callback_info_structs_for_the_layout_check()
        => Model.LayoutStructs.Select(s => s.CName).Should().Contain(new[] { "WGPURequestThingCallbackInfo", "WGPUStringView", "WGPUChainedStruct" });

    [Fact]
    public void Build_rejects_an_enum_redeclared_without_extended()
    {
        //Arrange
        string yaml = ExtraHeader + "structs: []\nenums:\n  - name: status\n    doc: d\n    entries:\n      - name: other\n        doc: d\n";

        //Act
        Action act = () => TestPaths.ModelWith(yaml);

        //Assert
        act.Should().Throw<SpecException>().Which.Message.Should().Contain("isn't marked as one");
    }

    [Fact]
    public void Build_rejects_a_duplicate_struct()
    {
        //Arrange
        string yaml = ExtraHeader + "enums: []\nstructs:\n  - name: plain_info\n    doc: d\n    type: standalone\n";

        //Act
        Action act = () => TestPaths.ModelWith(yaml);

        //Assert
        act.Should().Throw<SpecException>().Which.Message.Should().Contain("struct 'plain_info' already declared");
    }

    [Fact]
    public void Build_rejects_an_unknown_type_reference()
    {
        //Arrange
        string yaml = ExtraHeader + "enums: []\nstructs:\n  - name: broken\n    doc: d\n    type: standalone\n    members:\n      - name: m\n        doc: d\n        type: enum.no_such_enum\n";

        //Act
        Action act = () => TestPaths.ModelWith(yaml);

        //Assert
        act.Should().Throw<SpecException>().Which.Message.Should().Contain("unknown enum 'no_such_enum'");
    }

    [Fact]
    public void Build_rejects_an_extension_of_an_unknown_struct()
    {
        //Arrange
        string yaml = ExtraHeader + "enums: []\nstructs:\n  - name: lost\n    doc: d\n    type: extension\n    extends:\n      - nowhere\n";

        //Act
        Action act = () => TestPaths.ModelWith(yaml);

        //Assert
        act.Should().Throw<SpecException>().Which.Message.Should().Contain("extends 'nowhere'");
    }

    [Fact]
    public void Build_requires_the_core_specification_first()
    {
        //Act
        Action act = () => new ApiModelBuilder(new[] { new SpecFile { Name = "wgpu" } });

        //Assert
        act.Should().Throw<SpecException>();
    }
}
