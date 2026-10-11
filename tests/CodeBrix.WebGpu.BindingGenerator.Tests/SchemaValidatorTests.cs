using System;
using CodeBrix.WebGpu.BindingGenerator.Schema;
using CodeBrix.WebGpu.BindingGenerator.Yaml;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

public class SchemaValidatorTests
{
    private static void Validate(string yaml) =>
        new SchemaValidator(TestPaths.SchemaJson).Validate(SpecLoader.Parse(yaml, "test.yml"), "test.yml");

    private static string Minimal(string extra = "", string prefix = "0x0003") =>
        "copyright: c\nname: test\nenum_prefix: " + prefix + "\ndoc: d\nconstants: []\ntypedefs: []\nenums: []\n"
        + "bitflags: []\nstructs: []\ncallbacks: []\nfunctions: []\nobjects: []\n" + extra;

    [Fact]
    public void Validate_accepts_the_fixtures()
    {
        //Arrange
        SchemaValidator validator = new SchemaValidator(TestPaths.SchemaJson);

        //Act
        Action act = () =>
        {
            validator.Validate(SpecLoader.Parse(TestPaths.Fixture("webgpu.yml"), "webgpu.yml"), "webgpu.yml");
            validator.Validate(SpecLoader.Parse(TestPaths.Fixture("ext.yml"), "ext.yml"), "ext.yml");
        };

        //Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_accepts_a_minimal_document()
    {
        //Act
        Action act = () => Validate(Minimal());

        //Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_rejects_a_quoted_enum_prefix_with_its_path()
    {
        //Act
        Action act = () => Validate(Minimal(prefix: "'0x0003'"));

        //Assert
        act.Should().Throw<SpecException>().Which.NodePath.Should().Be("test.yml $.enum_prefix");
    }

    [Fact]
    public void Validate_rejects_an_unknown_top_level_property()
    {
        //Act
        Action act = () => Validate(Minimal("extension-objects: []\n"));

        //Assert
        act.Should().Throw<SpecException>().Which.Message.Should().Contain("extension-objects");
    }

    [Fact]
    public void Validate_rejects_a_missing_required_property()
    {
        //Act
        Action act = () => Validate(Minimal().Replace("objects: []\n", string.Empty));

        //Assert
        act.Should().Throw<SpecException>().Which.Message.Should().Contain("'objects'");
    }

    [Fact]
    public void Validate_rejects_an_unknown_struct_type_with_the_member_path()
    {
        //Arrange
        string yaml = Minimal().Replace(
            "structs: []",
            "structs:\n  - name: s\n    doc: d\n    type: extension_in\n");

        //Act
        Action act = () => Validate(yaml);

        //Assert
        act.Should().Throw<SpecException>().Which.NodePath.Should().Be("test.yml $.structs[0].type");
    }

    [Fact]
    public void Validate_rejects_a_bad_member_type_string()
    {
        //Arrange
        string yaml = Minimal().Replace(
            "structs: []",
            "structs:\n  - name: s\n    doc: d\n    type: standalone\n    members:\n      - name: m\n        doc: d\n        type: string\n");

        //Act
        Action act = () => Validate(yaml);

        //Assert
        act.Should().Throw<SpecException>().Which.NodePath.Should().Be("test.yml $.structs[0].members[0].type");
    }

    [Fact]
    public void Validate_rejects_a_multi_paragraph_parameter_doc()
    {
        //Arrange
        string yaml = Minimal().Replace(
            "functions: []",
            "functions:\n  - name: f\n    doc: d\n    args:\n      - name: a\n        doc: |\n          one\n\n          two\n        type: uint32\n");

        //Act
        Action act = () => Validate(yaml);

        //Assert
        act.Should().Throw<SpecException>().Which.NodePath.Should().Contain("$.functions[0].args[0]");
    }

    [Fact]
    public void Validate_rejects_an_enum_value_above_16_bits()
    {
        //Arrange
        string yaml = Minimal().Replace(
            "enums: []",
            "enums:\n  - name: e\n    doc: d\n    entries:\n      - name: x\n        doc: d\n        value: 70000\n");

        //Act
        Action act = () => Validate(yaml);

        //Assert
        act.Should().Throw<SpecException>().Which.NodePath.Should().Contain("$.enums[0].entries[0]");
    }
}
