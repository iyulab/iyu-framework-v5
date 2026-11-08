using System.Globalization;
using Iyu.Ml;
using Xunit;
using static Iyu.Tests.Ml.MlTestData;

namespace Iyu.Tests.Ml;

public class MlCsvTests
{
    private static async Task<(string Text, long Rows)> Write(MlDatasetDefinition definition, params IReadOnlyDictionary<string, object?>[] rows)
    {
        var writer = new StringWriter();
        var count = await MlCsv.WriteAsync(definition, AsAsync(rows), writer);
        return (writer.ToString(), count);
    }

    [Fact]
    public async Task The_header_is_the_definition_in_column_order_and_each_row_follows_it()
    {
        var definition = new MlDatasetDefinition("d", [new("b", MlColumnRole.Feature), new("a", MlColumnRole.Feature), new("y", MlColumnRole.Label)]);

        var (text, rows) = await Write(definition, Row(("a", 1), ("b", 2), ("y", "x")), Row(("y", "z"), ("b", 4), ("a", 3)));

        Assert.Equal("b,a,y\r\n2,1,x\r\n4,3,z\r\n", text);
        Assert.Equal(2, rows);
    }

    [Fact]
    public async Task A_value_with_a_comma_a_quote_or_a_line_break_is_quoted_and_inner_quotes_are_doubled()
    {
        var (text, _) = await Write(Definition(), Row(("value", "a,b"), ("label", "say \"hi\"")), Row(("value", "line\nbreak"), ("label", "cr\rlf")));

        Assert.Equal("value,label\r\n\"a,b\",\"say \"\"hi\"\"\"\r\n\"line\nbreak\",\"cr\rlf\"\r\n", text);
    }

    [Fact]
    public async Task A_missing_value_and_a_null_are_both_written_as_an_empty_field()
    {
        var (text, _) = await Write(Definition(), Row(("label", "x")), Row(("value", null), ("label", "y")));

        Assert.Equal("value,label\r\n,x\r\n,y\r\n", text);
    }

    [Fact]
    public async Task A_column_name_that_needs_quoting_is_quoted_in_the_header()
    {
        var definition = new MlDatasetDefinition("d", [new("unit, price", MlColumnRole.Feature)]);

        var (text, _) = await Write(definition, Row(("unit, price", 1.5)));

        Assert.Equal("\"unit, price\"\r\n1.5\r\n", text);
    }

    [Fact]
    public async Task An_empty_dataset_is_the_header_alone()
    {
        var (text, rows) = await Write(Definition());

        Assert.Equal("value,label\r\n", text);
        Assert.Equal(0, rows);
    }

    [Fact]
    public void Numbers_and_dates_read_the_same_whatever_the_machines_regional_setting_is()
    {
        var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            Assert.Equal("1234.5", MlCsv.Format(1234.5));
            Assert.Equal("1234.5", MlCsv.Format(1234.5m));
            Assert.Equal("0.1", MlCsv.Format(0.1f));
            Assert.Equal("2026-11-08T09:30:00.0000000", MlCsv.Format(new DateTime(2026, 11, 8, 9, 30, 0, DateTimeKind.Unspecified)));
            Assert.Equal("2026-11-08T09:30:00.0000000+09:00", MlCsv.Format(new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.FromHours(9))));
            Assert.Equal("2026-11-08", MlCsv.Format(new DateOnly(2026, 11, 8)));
            Assert.Equal("09:30:00.0000000", MlCsv.Format(new TimeOnly(9, 30)));
            Assert.Equal("-12", MlCsv.Format(-12));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = before;
        }
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(DayOfWeek.Sunday, "Sunday")]
    public void A_boolean_and_an_enumeration_are_written_by_name(object value, string expected) =>
        Assert.Equal(expected, MlCsv.Format(value));

    [Fact]
    public void A_guid_and_a_time_span_have_one_fixed_shape()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", MlCsv.Format(id));
        Assert.Equal("1.02:03:04", MlCsv.Format(new TimeSpan(1, 2, 3, 4)));
    }

    [Fact]
    public async Task Cancelling_stops_the_write()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MlCsv.WriteAsync(Definition(), AsAsync([Row(("label", "x"))]), new StringWriter(), source.Token));
    }
}
