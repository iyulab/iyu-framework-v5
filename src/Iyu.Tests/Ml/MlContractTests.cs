using Iyu.Ml;
using Xunit;
using static Iyu.Tests.Ml.MlTestData;

namespace Iyu.Tests.Ml;

public class MlContractTests
{
    [Fact]
    public void A_definition_without_a_name_a_feature_or_with_two_labels_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MlDatasetDefinition(" ", [new("a", MlColumnRole.Feature)]));
        Assert.Throws<ArgumentException>(() => new MlDatasetDefinition("d", [new("y", MlColumnRole.Label)]));
        Assert.Throws<ArgumentException>(() => new MlDatasetDefinition("d", [new("a", MlColumnRole.Feature), new("y", MlColumnRole.Label), new("z", MlColumnRole.Label)]));
    }

    [Fact]
    public void A_column_named_twice_ignoring_case_or_not_named_at_all_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MlDatasetDefinition("d", [new("a", MlColumnRole.Feature), new("A", MlColumnRole.Feature)]));
        Assert.Throws<ArgumentException>(() => new MlDatasetDefinition("d", [new("", MlColumnRole.Feature)]));
    }

    [Fact]
    public void A_definition_reports_its_features_in_column_order_and_its_label()
    {
        var definition = new MlDatasetDefinition("d", [new("b", MlColumnRole.Feature), new("y", MlColumnRole.Label), new("a", MlColumnRole.Feature)]);

        Assert.Equal(["b", "a"], definition.Features);
        Assert.Equal("y", definition.Label);
        Assert.Null(new MlDatasetDefinition("d", [new("a", MlColumnRole.Feature)]).Label);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void A_confidence_outside_zero_to_one_is_refused(double confidence)
    {
        var stamp = new MlStamp("m", "1", MlTasks.Classification, "p");

        Assert.Throws<ArgumentOutOfRangeException>(() => new MlPrediction("x", stamp, confidence));
    }

    [Fact]
    public void A_prediction_may_carry_no_confidence_and_a_stamp_may_not_be_empty()
    {
        var stamp = new MlStamp("m", "1", MlTasks.Anomaly, "p", new Dictionary<string, string> { ["k"] = "v" });

        Assert.Null(new MlPrediction(0.5, stamp).Confidence);
        Assert.Equal("v", stamp.Notes!["k"]);
        Assert.Throws<ArgumentException>(() => new MlStamp("", "1", MlTasks.Anomaly, "p"));
        Assert.Throws<ArgumentException>(() => new MlStamp("m", "1", " ", "p"));
        Assert.Throws<ArgumentException>(() => new MlStamp("m", "1", MlTasks.Anomaly, ""));
    }

    [Fact]
    public async Task The_batch_default_answers_each_row_in_order_through_the_single_row_call()
    {
        var predictor = new EchoPredictor();

        var answers = await ((IMlPredictor)predictor).PredictBatchAsync("echo", [Row(("n", 1)), Row(("n", 2)), Row(("n", 3))]);

        Assert.Equal([1, 2, 3], answers.Select(a => a.Value));
        Assert.Equal(3, predictor.Calls);
    }

    [Fact]
    public async Task One_unreadable_row_fails_the_whole_batch()
    {
        IMlPredictor predictor = new EchoPredictor();

        await Assert.ThrowsAsync<MlModelNotFoundException>(async () => await predictor.PredictBatchAsync("other", [Row(("n", 1))]));
    }

    [Fact]
    public void The_assembly_depends_on_the_platform_and_its_extension_abstractions_alone()
    {
        // A host that keeps no records in a database and serves no web endpoints starts from this assembly by itself:
        // nothing here may pull in the web stack, a data-access library or another layer of this framework. The
        // dependency-injection, options and hosting abstractions are what any host-neutral module needs to register itself.
        var references = typeof(MlCsv).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.NotEmpty(references);
        Assert.All(references, name => Assert.True(
            name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            || name is "System" or "mscorlib" or "netstandard",
            $"Iyu.Ml references '{name}', which is neither the platform nor one of its extension abstractions."));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || n.StartsWith("Iyu.", StringComparison.Ordinal));
    }

    private sealed class EchoPredictor : IMlPredictor
    {
        public int Calls { get; private set; }

        public ValueTask<MlPrediction> PredictAsync(string model, IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
        {
            if (model != "echo")
                throw new MlModelNotFoundException(model);
            Calls++;
            return ValueTask.FromResult(new MlPrediction(row["n"], new MlStamp(model, "1", MlTasks.Regression, Guid.NewGuid().ToString("N"))));
        }
    }
}
