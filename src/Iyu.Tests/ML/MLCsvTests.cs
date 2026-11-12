using System.Globalization;
using Iyu.ML;
using Xunit;
using static Iyu.Tests.ML.MLTestData;

namespace Iyu.Tests.ML;

public class MLCsvTests
{
    private static async Task<(string Text, long Rows)> Write(MLDatasetDefinition definition, params IReadOnlyDictionary<string, object?>[] rows)
    {
        var writer = new StringWriter();
        var count = await MLCsv.WriteAsync(definition, AsAsync(rows), writer);
        return (writer.ToString(), count);
    }

    [Fact]
    public async Task The_header_is_the_definition_in_column_order_and_each_row_follows_it()
    {
        var definition = new MLDatasetDefinition("d", [new("b", MLColumnRole.Feature), new("a", MLColumnRole.Feature), new("y", MLColumnRole.Label)]);

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
        var definition = new MLDatasetDefinition("d", [new("unit, price", MLColumnRole.Feature)]);

        var (text, _) = await Write(definition, Row(("unit, price", 1.5)));

        Assert.Equal("\"unit, price\"\r\n1.5\r\n", text);
    }

    [Fact]
    public async Task A_record_of_one_empty_field_is_quoted_so_it_does_not_read_as_a_blank_line()
    {
        var definition = new MLDatasetDefinition("d", [new("v", MLColumnRole.Feature)]);

        var (text, rows) = await Write(definition, Row(("v", 1)), Row(("v", null)), Row(("v", "")), Row());

        Assert.Equal("v\r\n1\r\n\"\"\r\n\"\"\r\n\"\"\r\n", text);
        Assert.Equal(4, rows);
    }

    [Fact]
    public void Bytes_are_written_as_base_64_and_a_value_with_no_text_of_its_own_is_refused()
    {
        Assert.Equal("AQID", MLCsv.Format(new byte[] { 1, 2, 3 }));
        Assert.Equal("x", MLCsv.Format('x'));
        Assert.Throws<ArgumentException>(() => MLCsv.Format(new object()));
        Assert.Throws<ArgumentException>(() => MLCsv.Format(new List<int>()));
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

            Assert.Equal("1234.5", MLCsv.Format(1234.5));
            Assert.Equal("1234.5", MLCsv.Format(1234.5m));
            Assert.Equal("0.1", MLCsv.Format(0.1f));
            Assert.Equal("2026-11-08T09:30:00.0000000", MLCsv.Format(new DateTime(2026, 11, 8, 9, 30, 0, DateTimeKind.Unspecified)));
            Assert.Equal("2026-11-08T09:30:00.0000000+09:00", MLCsv.Format(new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.FromHours(9))));
            Assert.Equal("2026-11-08", MLCsv.Format(new DateOnly(2026, 11, 8)));
            Assert.Equal("09:30:00.0000000", MLCsv.Format(new TimeOnly(9, 30)));
            Assert.Equal("-12", MLCsv.Format(-12));
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
        Assert.Equal(expected, MLCsv.Format(value));

    [Fact]
    public void A_guid_and_a_time_span_have_one_fixed_shape()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", MLCsv.Format(id));
        Assert.Equal("1.02:03:04", MLCsv.Format(new TimeSpan(1, 2, 3, 4)));
    }

    [Fact]
    public async Task Cancelling_stops_the_write()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MLCsv.WriteAsync(Definition(), AsAsync([Row(("label", "x"))]), new StringWriter(), source.Token));
    }
}
