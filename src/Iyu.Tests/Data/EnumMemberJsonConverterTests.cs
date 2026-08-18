using System.Runtime.Serialization;
using System.Text.Json;
using Iyu.Data;
using Xunit;

namespace Iyu.Tests.Data;

public class EnumMemberJsonConverterTests
{
    public enum InspectionItemInputType
    {
        [EnumMember(Value = "verdict")]
        Verdict,
        [EnumMember(Value = "numeric")]
        Numeric,
        Text // no attribute: falls back to the CLR name in both directions.
    }

    public enum PlainDirection
    {
        Up,
        Down
    }

    private static JsonSerializerOptions Options() =>
        new()
        {
            Converters = { new EnumMemberJsonConverterFactory(), new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

    [Fact]
    public void Serializes_using_the_EnumMember_wire_name_not_the_CLR_member_name()
    {
        var json = JsonSerializer.Serialize(InspectionItemInputType.Verdict, Options());

        Assert.Equal("\"verdict\"", json);
    }

    [Fact]
    public void Deserializes_the_EnumMember_wire_name()
    {
        var value = JsonSerializer.Deserialize<InspectionItemInputType>("\"verdict\"", Options());

        Assert.Equal(InspectionItemInputType.Verdict, value);
    }

    [Fact]
    public void Accepts_the_CLR_member_name_case_insensitively_but_always_serializes_the_wire_name()
    {
        // FromWire matches case-insensitively (inherited from EnumMemberConverter<T>'s EF
        // lookup via EnumWireNames<T>), so a caller still sending the old CLR-cased form
        // ("Verdict") keeps deserializing -- only the *output* changes to the wire name.
        // That is the actual root defect fixed here: before this converter, /$data (EDM)
        // always emitted "verdict" while /api (plain JsonStringEnumConverter) always
        // emitted "Verdict" -- the two surfaces disagreed on what they handed back for the
        // same stored value. They now agree on output; lenient input is a bonus, not the fix.
        var value = JsonSerializer.Deserialize<InspectionItemInputType>("\"Verdict\"", Options());
        Assert.Equal(InspectionItemInputType.Verdict, value);

        var json = JsonSerializer.Serialize(InspectionItemInputType.Verdict, Options());
        Assert.Equal("\"verdict\"", json);
    }

    [Fact]
    public void Rejects_a_value_not_present_on_the_enum_at_all()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<InspectionItemInputType>("\"not-a-real-value\"", Options()));
    }

    [Fact]
    public void Still_accepts_a_raw_numeric_value_like_the_plain_converter_does()
    {
        // The plain JsonStringEnumConverter this factory sits ahead of allows integer
        // input by default -- opting an enum into [EnumMember] must not silently drop
        // that for every existing numeric-sending caller.
        var value = JsonSerializer.Deserialize<InspectionItemInputType>("1", Options());
        Assert.Equal(InspectionItemInputType.Numeric, value);
    }

    [Fact]
    public void Member_without_EnumMember_attribute_falls_back_to_its_CLR_name()
    {
        var json = JsonSerializer.Serialize(InspectionItemInputType.Text, Options());
        Assert.Equal("\"Text\"", json);

        var value = JsonSerializer.Deserialize<InspectionItemInputType>("\"Text\"", Options());
        Assert.Equal(InspectionItemInputType.Text, value);
    }

    [Fact]
    public void Enum_with_no_EnumMember_attributes_at_all_falls_through_to_the_plain_converter()
    {
        // CanConvert must decline so the plain JsonStringEnumConverter registered after
        // it handles this type unchanged -- an enum nobody annotated is unaffected.
        var json = JsonSerializer.Serialize(PlainDirection.Up, Options());
        Assert.Equal("\"Up\"", json);
    }

    [Fact]
    public void Nullable_enum_round_trips_through_null()
    {
        var json = JsonSerializer.Serialize((InspectionItemInputType?)null, Options());
        Assert.Equal("null", json);

        var value = JsonSerializer.Deserialize<InspectionItemInputType?>("null", Options());
        Assert.Null(value);
    }

    [Fact]
    public void Nullable_enum_round_trips_a_value_through_the_wire_name()
    {
        var json = JsonSerializer.Serialize((InspectionItemInputType?)InspectionItemInputType.Numeric, Options());
        Assert.Equal("\"numeric\"", json);

        var value = JsonSerializer.Deserialize<InspectionItemInputType?>("\"numeric\"", Options());
        Assert.Equal(InspectionItemInputType.Numeric, value);
    }
}
