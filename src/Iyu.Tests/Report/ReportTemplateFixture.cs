using ClosedXML.Excel;

namespace Iyu.Tests.Report;

public static class ReportTemplateFixture
{
    public static MemoryStream BuildShipmentSlipTemplate()
    {
        using var workbook = new XLWorkbook();

        AddSheet(workbook, "Copy1");
        AddSheet(workbook, "Copy2");

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static void AddSheet(XLWorkbook workbook, string sheetName)
    {
        var ws = workbook.AddWorksheet(sheetName);

        ws.Range("A1:D1").Merge();
        ws.Cell("A1").Value = "{{Title}}";

        ws.Cell("A3").Value = "Name";
        ws.Cell("B3").Value = "Qty";
        ws.Cell("C3").Value = "Price";

        ws.Cell("B4").Value = "{{item.Name}}";
        ws.Cell("C4").Value = "{{item.Qty}}";
        ws.Cell("D4").Value = "{{item.Price}}";
        ws.Range("A4:D5").AddToNamed("Items", XLScope.Worksheet);

        ws.Cell("C7").Value = "Total";
        ws.Cell("D7").Value = "{{Total}}";

        ws.Cell("A9").Value = "{{Remarks}}";

        ws.Range("A11:B11").Merge();
        ws.Cell("A11").Value = "{{Signature1}}";
        ws.Range("C11:D11").Merge();
        ws.Cell("C11").Value = "{{Signature2}}";
        ws.Range("A12:B12").Merge();
        ws.Cell("A12").Value = "{{Signature3}}";
        ws.Range("C12:D12").Merge();
        ws.Cell("C12").Value = "{{Signature4}}";
    }
}
