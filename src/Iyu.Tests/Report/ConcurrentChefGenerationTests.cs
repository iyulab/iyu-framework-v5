using DocuChef;
using Iyu.Report;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Report;

public class ConcurrentChefGenerationTests
{
    [Fact]
    public async Task Concurrent_Chef_generations_do_not_corrupt_each_others_output()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIyuReport();
        var provider = services.BuildServiceProvider();

        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            using var scope = provider.CreateScope();
            var chef = scope.ServiceProvider.GetRequiredService<Chef>();

            using var templateStream = ReportTemplateFixture.BuildShipmentSlipTemplate();
            using var recipe = chef.LoadExcelTemplate(templateStream);
            recipe.AddVariable("Title", $"Slip #{i}");
            recipe.AddVariable("Items", Array.Empty<object>());
            recipe.AddVariable("Total", 0m);
            recipe.AddVariable("Remarks", $"Remark {i}");
            recipe.AddVariable("Signature1", "");
            recipe.AddVariable("Signature2", "");
            recipe.AddVariable("Signature3", "");
            recipe.AddVariable("Signature4", "");

            using var dish = recipe.CookDish();
            using var outputStream = new MemoryStream();
            dish.SaveAs(outputStream);
            outputStream.Position = 0;

            using var result = new ClosedXML.Excel.XLWorkbook(outputStream);
            return result.Worksheet("Copy1").Cell("A1").GetString();
        });

        var titles = await Task.WhenAll(tasks);

        for (var i = 0; i < titles.Length; i++)
        {
            Assert.Equal($"Slip #{i}", titles[i]);
        }
    }
}
