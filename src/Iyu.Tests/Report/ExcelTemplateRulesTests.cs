using ClosedXML.Excel;
using DocuChef;
using DocuChef.Excel;
using Iyu.Report;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Report;

/// <summary>
/// The template rules a host meets when it fills a workbook with <c>Iyu.Report</c>, pinned by filling small templates and reading
/// the saved file back: how a named range repeats, which way a one-column range runs, what happens to the template's formulas, and
/// what of the template's view and page settings survives the fill. The README states these; if the library under it changes one,
/// this is where it shows.
/// </summary>
public class ExcelTemplateRulesTests
{
    private static Chef CreateChef()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIyuReport();
        return services.BuildServiceProvider().GetRequiredService<Chef>();
    }

    private static MemoryStream Save(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static IDish Fill(XLWorkbook template, params (string Name, object Value)[] variables)
    {
        using var templateStream = Save(template);
        using var chef = CreateChef();
        using var recipe = chef.LoadExcelTemplate(templateStream);
        foreach (var (name, value) in variables) recipe.AddVariable(name, value);
        return recipe.CookDish();
    }

    private static XLWorkbook Reopen(IDish dish)
    {
        var stream = new MemoryStream();
        dish.SaveAs(stream);
        stream.Position = 0;
        return new XLWorkbook(stream);
    }

    [Fact]
    public void A_range_repeats_every_row_but_its_last_per_record_and_keeps_the_last_as_a_tail()
    {
        using var template = new XLWorkbook();
        var ws = template.AddWorksheet("S");
        ws.Row(4).Height = 30;
        ws.Row(5).Height = 18;
        ws.Cell("A4").Value = "{{item.Name}}";
        ws.Cell("B4").Value = "{{item.Qty}}";
        ws.Range("A5:B5").Merge();
        ws.Cell("A5").Value = "detail";
        ws.Cell("A6").Value = "tail";
        ws.Range("A4:B6").AddToNamed("Blocks", XLScope.Worksheet);

        using var dish = Fill(template, ("Blocks", new[]
        {
            new { Name = "One", Qty = 1 }, new { Name = "Two", Qty = 2 }, new { Name = "Three", Qty = 3 },
        }));
        using var result = Reopen(dish);
        var sheet = result.Worksheet("S");

        Assert.Equal(new[] { "One", "detail", "Two", "detail", "Three", "detail", "tail" },
            Enumerable.Range(4, 7).Select(r => sheet.Cell(r, 1).GetString()).ToArray());
        Assert.Equal(new[] { 30d, 18d, 30d, 18d, 30d, 18d }, Enumerable.Range(4, 6).Select(r => sheet.Row(r).Height).ToArray());
        Assert.Equal(new[] { "A5:B5", "A7:B7", "A9:B9" }, sheet.MergedRanges.Select(m => m.RangeAddress.ToString()).Order().ToArray());
    }

    [Fact]
    public void A_one_column_range_runs_sideways_instead_of_downwards()
    {
        using var template = new XLWorkbook();
        var ws = template.AddWorksheet("S");
        ws.Cell("A4").Value = "{{item.Text}}";
        ws.Range("A4:A5").AddToNamed("Notes", XLScope.Worksheet);

        using var dish = Fill(template, ("Notes", new[] { new { Text = "first" }, new { Text = "second" }, new { Text = "third" } }));
        using var result = Reopen(dish);
        var sheet = result.Worksheet("S");

        Assert.Equal("first", sheet.Cell("A4").GetString());
        Assert.Equal("second", sheet.Cell("B4").GetString());
        Assert.Equal("third", sheet.Cell("C4").GetString());
        Assert.Equal("", sheet.Cell("A5").GetString());
    }

    [Theory]
    [InlineData(XLScope.Worksheet)]
    [InlineData(XLScope.Workbook)]
    public void A_range_one_row_high_runs_sideways_and_its_scope_changes_nothing(XLScope scope)
    {
        using var template = new XLWorkbook();
        var ws = template.AddWorksheet("S");
        ws.Cell("A4").Value = "{{item.Name}}";
        ws.Cell("B4").Value = "{{item.Qty}}";
        ws.Range("A4:D4").AddToNamed("Items", scope);

        using var dish = Fill(template, ("Items", new[]
        {
            new { Name = "a", Qty = 1 }, new { Name = "b", Qty = 2 }, new { Name = "c", Qty = 3 },
        }));
        using var result = Reopen(dish);
        var sheet = result.Worksheet("S");

        Assert.Equal("a", sheet.Cell("A4").GetString());
        Assert.True(sheet.Cell("A5").IsEmpty(), "a one-row range has no row to grow into");
        Assert.True(sheet.LastColumnUsed()!.ColumnNumber() > 4, "the other records went to the right of the range");
    }

    [Theory]
    [InlineData(XLScope.Worksheet)]
    [InlineData(XLScope.Workbook)]
    public void A_two_row_range_repeats_downwards_the_same_way_in_either_scope(XLScope scope)
    {
        using var template = new XLWorkbook();
        var ws = template.AddWorksheet("S");
        ws.Cell("A4").Value = "{{item.Name}}";
        ws.Cell("B4").Value = "{{item.Qty}}";
        ws.Range("A4:B5").AddToNamed("Items", scope);

        using var dish = Fill(template, ("Items", new[]
        {
            new { Name = "a", Qty = 1 }, new { Name = "b", Qty = 2 }, new { Name = "c", Qty = 3 },
        }));
        using var result = Reopen(dish);
        var sheet = result.Worksheet("S");

        Assert.Equal(new[] { "a", "b", "c" }, Enumerable.Range(4, 3).Select(r => sheet.Cell(r, 1).GetString()).ToArray());
        Assert.Equal(2, sheet.LastColumnUsed()!.ColumnNumber());
    }

    [Fact]
    public void Template_formulas_are_repeated_per_record_and_a_total_follows_the_rows()
    {
        using var template = new XLWorkbook();
        var ws = template.AddWorksheet("F");
        ws.Cell("A4").FormulaA1 = "ROW()-3";
        ws.Cell("B4").Value = "{{item.Qty}}";
        ws.Cell("C4").Value = "{{item.Price}}";
        ws.Cell("D4").FormulaA1 = "B4*C4";
        ws.Range("A4:D5").AddToNamed("Items", XLScope.Worksheet);
        ws.Cell("C7").Value = "Total";
        ws.Cell("D7").FormulaA1 = "SUM(D4:D5)";

        using var dish = Fill(template, ("Items", new[]
        {
            new { Qty = 2, Price = 10m }, new { Qty = 3, Price = 5m }, new { Qty = 1, Price = 7m },
        }));
        using var result = Reopen(dish);
        var sheet = result.Worksheet("F");

        Assert.Equal(new[] { "ROW()-3", "ROW()-3", "ROW()-3" }, Enumerable.Range(4, 3).Select(r => sheet.Cell(r, 1).FormulaA1).ToArray());
        Assert.Equal(new[] { "B4*C4", "B5*C5", "B6*C6" }, Enumerable.Range(4, 3).Select(r => sheet.Cell(r, 4).FormulaA1).ToArray());
        Assert.Equal("SUM(D4:D6)", sheet.Cell("D8").FormulaA1);
    }

    [Fact]
    public void Saving_through_the_workbook_with_evaluation_puts_the_computed_values_in_the_file()
    {
        using var template = new XLWorkbook();
        var ws = template.AddWorksheet("F");
        ws.Cell("A4").FormulaA1 = "ROW()-3";
        ws.Cell("B4").Value = "{{item.Qty}}";
        ws.Cell("C4").Value = "{{item.Price}}";
        ws.Cell("D4").FormulaA1 = "B4*C4";
        ws.Range("A4:D5").AddToNamed("Items", XLScope.Worksheet);
        ws.Cell("C7").Value = "Total";
        ws.Cell("D7").FormulaA1 = "SUM(D4:D5)";

        using var dish = Fill(template, ("Items", new[]
        {
            new { Qty = 2, Price = 10m }, new { Qty = 3, Price = 5m }, new { Qty = 1, Price = 7m },
        }));
        using var stream = new MemoryStream();
        ((ExcelDocument)dish).Workbook.SaveAs(stream, new SaveOptions { EvaluateFormulasBeforeSaving = true });
        stream.Position = 0;
        using var result = new XLWorkbook(stream);
        var sheet = result.Worksheet("F");

        Assert.Equal(new[] { 1d, 2d, 3d }, Enumerable.Range(4, 3).Select(r => sheet.Cell(r, 1).CachedValue.GetNumber()).ToArray());
        Assert.Equal(20d, sheet.Cell("D4").CachedValue.GetNumber());
        Assert.Equal(42d, sheet.Cell("D8").CachedValue.GetNumber());
    }

    [Fact]
    public void The_templates_view_settings_and_page_setup_survive_the_fill()
    {
        using var template = new XLWorkbook();
        var ws = template.AddWorksheet("V");
        ws.Cell("A4").Value = "{{item.Name}}";
        ws.Range("A4:A5").AddToNamed("Items", XLScope.Worksheet);
        ws.ShowGridLines = false;
        ws.SetShowRowColHeaders(false);
        ws.PageSetup.PagesWide = 1;
        ws.PageSetup.PagesTall = 0;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.Protect("pw");

        using var dish = Fill(template, ("Items", new[] { new { Name = "a" }, new { Name = "b" } }));
        using var result = Reopen(dish);
        var sheet = result.Worksheet("V");

        Assert.False(sheet.ShowGridLines);
        Assert.False(sheet.ShowRowColHeaders);
        Assert.True(sheet.IsProtected);
        Assert.Equal(1, sheet.PageSetup.PagesWide);
        Assert.Equal(0, sheet.PageSetup.PagesTall);
        Assert.Equal(XLPageOrientation.Landscape, sheet.PageSetup.PageOrientation);
    }

    [Fact]
    public void A_registered_function_is_called_once_per_record_and_can_put_a_picture_in_the_cell()
    {
        using var template = new XLWorkbook();
        var ws = template.AddWorksheet("Q");
        ws.Cell("A4").Value = "{{item.Code|mark}}";
        ws.Range("A4:B5").AddToNamed("Items", XLScope.Worksheet);
        using var templateStream = Save(template);
        using var chef = CreateChef();
        using var recipe = chef.LoadExcelTemplate(templateStream);
        var seen = new List<object?>();
        recipe.RegisterFunction("mark", (cell, value, parameters) =>
        {
            seen.Add(value);
            using var bitmap = new SkiaSharp.SKBitmap(8, 8);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            cell.Worksheet.AddPicture(data.AsStream()).MoveTo(cell);
            cell.Value = "";
        });
        recipe.AddVariable("Items", new[] { new { Code = "C1" }, new { Code = "C2" } });

        using var dish = recipe.CookDish();
        using var result = Reopen(dish);

        Assert.Equal(new object?[] { "C1", "C2" }, seen);
        Assert.Equal(2, result.Worksheet("Q").Pictures.Count);
    }
}
