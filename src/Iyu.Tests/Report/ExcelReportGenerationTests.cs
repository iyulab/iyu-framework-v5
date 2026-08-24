using ClosedXML.Excel;
using DocuChef;
using Iyu.Report;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Report;

public class ExcelReportGenerationTests
{
    private static Chef CreateChef()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIyuReport();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<Chef>();
    }

    private sealed record Item(string Name, int Qty, decimal Price);

    [Fact]
    public void Generate_binds_header_table_totals_remarks_signatures_on_both_sheets()
    {
        using var templateStream = ReportTemplateFixture.BuildShipmentSlipTemplate();
        using var chef = CreateChef();
        using var recipe = chef.LoadExcelTemplate(templateStream);

        recipe.AddVariable("Title", "Shipment Slip");
        recipe.AddVariable("Items", new[]
        {
            new Item("Widget", 3, 9.99m),
            new Item("Gadget", 1, 19.99m),
            new Item("Gizmo", 2, 4.99m),
        });
        recipe.AddVariable("Total", 49.97m);
        recipe.AddVariable("Remarks", "Handle with care.");
        recipe.AddVariable("Signature1", "Sender");
        recipe.AddVariable("Signature2", "Carrier");
        recipe.AddVariable("Signature3", "Receiver");
        recipe.AddVariable("Signature4", "Warehouse");

        using var dish = recipe.CookDish();
        using var outputStream = new MemoryStream();
        dish.SaveAs(outputStream);
        outputStream.Position = 0;

        using var result = new XLWorkbook(outputStream);
        foreach (var sheetName in new[] { "Copy1", "Copy2" })
        {
            var ws = result.Worksheet(sheetName);

            Assert.Equal("Shipment Slip", ws.Cell("A1").GetString());

            Assert.Equal("Widget", ws.Cell("B4").GetString());
            Assert.Equal(3, ws.Cell("C4").GetValue<int>());
            Assert.Equal("Gadget", ws.Cell("B5").GetString());
            Assert.Equal(1, ws.Cell("C5").GetValue<int>());
            Assert.Equal("Gizmo", ws.Cell("B6").GetString());
            Assert.Equal(2, ws.Cell("C6").GetValue<int>());

            Assert.Equal(49.97m, ws.Cell("D8").GetValue<decimal>());
            Assert.Equal("Handle with care.", ws.Cell("A10").GetString());

            Assert.Equal("Sender", ws.Cell("A12").GetString());
            Assert.Equal("Carrier", ws.Cell("C12").GetString());
            Assert.Equal("Receiver", ws.Cell("A13").GetString());
            Assert.Equal("Warehouse", ws.Cell("C13").GetString());
        }
    }
}
