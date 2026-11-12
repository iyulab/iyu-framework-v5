using Iyu.ML;
using Xunit;
using static Iyu.Tests.ML.MLTestData;

namespace Iyu.Tests.ML;

public class MLContractTests
{
    [Fact]
    public void A_definition_without_a_name_a_feature_or_with_two_labels_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MLDatasetDefinition(" ", [new("a", MLColumnRole.Feature)]));
        Assert.Throws<ArgumentException>(() => new MLDatasetDefinition("d", [new("y", MLColumnRole.Label)]));
        Assert.Throws<ArgumentException>(() => new MLDatasetDefinition("d", [new("a", MLColumnRole.Feature), new("y", MLColumnRole.Label), new("z", MLColumnRole.Label)]));
    }

    [Fact]
    public void A_column_named_twice_ignoring_case_or_not_named_at_all_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MLDatasetDefinition("d", [new("a", MLColumnRole.Feature), new("A", MLColumnRole.Feature)]));
        Assert.Throws<ArgumentException>(() => new MLDatasetDefinition("d", [new("", MLColumnRole.Feature)]));
    }

    [Fact]
    public void A_definition_reports_its_features_in_column_order_and_its_label()
    {
        var definition = new MLDatasetDefinition("d", [new("b", MLColumnRole.Feature), new("y", MLColumnRole.Label), new("a", MLColumnRole.Feature)]);

        Assert.Equal(["b", "a"], definition.Features);
        Assert.Equal("y", definition.Label);
        Assert.Null(new MLDatasetDefinition("d", [new("a", MLColumnRole.Feature)]).Label);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void A_confidence_outside_zero_to_one_is_refused(double confidence)
    {
        var stamp = new MLStamp("m", "1", MLTasks.Classification, "p");

        Assert.Throws<ArgumentOutOfRangeException>(() => new MLPrediction("x", stamp, confidence));
    }

    [Fact]
    public void A_prediction_may_carry_no_confidence_and_a_stamp_may_not_be_empty()
    {
        var stamp = new MLStamp("m", "1", MLTasks.Anomaly, "p", new Dictionary<string, string> { ["k"] = "v" });

        Assert.Null(new MLPrediction(0.5, stamp).Confidence);
        Assert.Equal("v", stamp.Notes!["k"]);
        Assert.Throws<ArgumentException>(() => new MLStamp("", "1", MLTasks.Anomaly, "p"));
        Assert.Throws<ArgumentException>(() => new MLStamp("m", "1", " ", "p"));
        Assert.Throws<ArgumentException>(() => new MLStamp("m", "1", MLTasks.Anomaly, ""));
    }

    [Fact]
    public async Task The_batch_default_answers_each_row_in_order_through_the_single_row_call()
    {
        var predictor = new EchoPredictor();

        var answers = await ((IMLPredictor)predictor).PredictBatchAsync("echo", [Row(("n", 1)), Row(("n", 2)), Row(("n", 3))]);

        Assert.Equal([1, 2, 3], answers.Select(a => a.Value));
        Assert.Equal(3, predictor.Calls);
    }

    [Fact]
    public async Task One_unreadable_row_fails_the_whole_batch()
    {
        IMLPredictor predictor = new EchoPredictor();

        await Assert.ThrowsAsync<MLModelNotFoundException>(async () => await predictor.PredictBatchAsync("other", [Row(("n", 1))]));
    }

    [Fact]
    public void The_assembly_depends_on_the_platform_and_its_extension_abstractions_alone()
    {
        // A host that keeps no records in a database and serves no web endpoints starts from this assembly by itself:
        // nothing here may pull in the web stack, a data-access library or another layer of this framework. The
        // dependency-injection, options and hosting abstractions are what any host-neutral module needs to register itself.
        var references = typeof(MLCsv).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.NotEmpty(references);
        Assert.All(references, name => Assert.True(
            name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            || name is "System" or "mscorlib" or "netstandard",
            $"Iyu.ML references '{name}', which is neither the platform nor one of its extension abstractions."));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || n.StartsWith("Iyu.", StringComparison.Ordinal));
    }

    private sealed class EchoPredictor : IMLPredictor
    {
        public int Calls { get; private set; }

        public ValueTask<MLPrediction> PredictAsync(string model, IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
        {
            if (model != "echo")
                throw new MLModelNotFoundException(model);
            Calls++;
            return ValueTask.FromResult(new MLPrediction(row["n"], new MLStamp(model, "1", MLTasks.Regression, Guid.NewGuid().ToString("N"))));
        }
    }
}
