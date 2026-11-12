using System.Security.Claims;
using Iyu.ML;
using Xunit;
using static Iyu.Tests.ML.MLTestData;

namespace Iyu.Tests.ML;

public class BaselineTests
{
    private static IReadOnlyDictionary<string, object?> Reading(object? value, object? label = null) =>
        Row(("value", value), ("label", label));

    // ---- ConstantBaseline -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_classification_baseline_answers_the_most_frequent_label_with_its_share_as_confidence()
    {
        var rows = new[] { Reading(1, "ok"), Reading(2, "ok"), Reading(3, "bad"), Reading(4, "ok"), Reading(5, null) };

        var baseline = ConstantBaseline.FitClassification("quality", "v1", Definition(), rows);
        var answer = await baseline.PredictAsync("quality", Row(("value", 99)));

        Assert.Equal("ok", answer.Value);
        Assert.Equal(0.75, answer.Confidence);
        Assert.Equal(4, baseline.Rows);
        Assert.Equal((MLTasks.Classification, "quality", "v1"), (answer.Stamp.Task, answer.Stamp.ModelName, answer.Stamp.Version));
    }

    [Fact]
    public void A_tie_goes_to_the_label_that_sorts_first_whatever_order_the_rows_came_in()
    {
        var forward = ConstantBaseline.FitClassification("m", "1", Definition(), [Reading(1, "b"), Reading(2, "a")]);
        var backward = ConstantBaseline.FitClassification("m", "1", Definition(), [Reading(2, "a"), Reading(1, "b")]);

        Assert.Equal("a", forward.Value);
        Assert.Equal("a", backward.Value);
        Assert.Equal(0.5, forward.Confidence);
    }

    [Fact]
    public void Labels_are_counted_by_their_text_so_one_number_written_two_ways_is_one_label()
    {
        var baseline = ConstantBaseline.FitClassification("m", "1", Definition(), [Reading(1, 2), Reading(1, 2L), Reading(1, 3)]);

        Assert.Equal(2, Convert.ToInt32(baseline.Value));
        Assert.Equal(2d / 3, baseline.Confidence!.Value, 12);
    }

    [Fact]
    public async Task A_regression_baseline_answers_the_median_and_states_no_confidence()
    {
        var baseline = ConstantBaseline.FitRegression("price", "v1", Definition(), [Reading(0, 10), Reading(0, 30), Reading(0, 20), Reading(0, 1000)]);

        var answer = await baseline.PredictAsync("price", Row());

        Assert.Equal(25d, answer.Value);
        Assert.Null(answer.Confidence);
        Assert.Equal(MLTasks.Regression, answer.Stamp.Task);
    }

    [Fact]
    public void A_regression_label_that_is_not_a_number_is_refused_naming_the_column()
    {
        var error = Assert.Throws<ArgumentException>(() => ConstantBaseline.FitRegression("m", "1", Definition(), [Reading(0, "text")]));

        Assert.Contains("'label'", error.Message);
    }

    [Fact]
    public void A_fit_needs_a_label_column_a_labelled_row_and_names()
    {
        var unlabeled = new MLDatasetDefinition("d", [new("value", MLColumnRole.Feature)]);

        Assert.Throws<ArgumentException>(() => ConstantBaseline.FitClassification("m", "1", unlabeled, [Reading(1, "x")]));
        Assert.Throws<InvalidOperationException>(() => ConstantBaseline.FitClassification("m", "1", Definition(), [Reading(1)]));
        Assert.Throws<InvalidOperationException>(() => ConstantBaseline.FitRegression("m", "1", Definition(), []));
        Assert.Throws<ArgumentException>(() => ConstantBaseline.FitClassification("", "1", Definition(), [Reading(1, "x")]));
        Assert.Throws<ArgumentException>(() => ConstantBaseline.FitRegression("m", " ", Definition(), [Reading(1, 1)]));
    }

    [Fact]
    public async Task A_baseline_serves_only_the_name_it_was_fitted_for_and_each_answer_has_its_own_id()
    {
        var baseline = ConstantBaseline.FitClassification("quality", "v1", Definition(), [Reading(1, "ok")]);

        await Assert.ThrowsAsync<MLModelNotFoundException>(async () => await baseline.PredictAsync("Quality", Row()));
        var first = await baseline.PredictAsync("quality", Row());
        var second = await baseline.PredictAsync("quality", Row());

        Assert.NotEqual(first.Stamp.PredictionId, second.Stamp.PredictionId);
    }

    [Fact]
    public async Task The_asynchronous_fits_give_the_same_baselines_as_the_synchronous_ones()
    {
        var rows = new[] { Reading(1, "a"), Reading(2, "b"), Reading(3, "b") };

        var sync = ConstantBaseline.FitClassification("m", "1", Definition(), rows);
        var async = await ConstantBaseline.FitClassificationAsync("m", "1", Definition(), AsAsync(rows));
        var median = await ConstantBaseline.FitRegressionAsync("m", "1", Definition(), AsAsync([Reading(0, 1), Reading(0, 5)]));

        Assert.Equal((sync.Value, sync.Confidence, sync.Rows), (async.Value, async.Confidence, async.Rows));
        Assert.Equal(3d, median.Value);
    }

    // ---- RobustZBaseline --------------------------------------------------------------------------------------

    private static RobustZBaseline FitValues(params double[] values) =>
        RobustZBaseline.Fit("anomaly", "v1", Definition(), values.Select(v => Reading(v)));

    [Fact]
    public async Task A_value_is_scored_in_robust_standard_deviations_from_the_median()
    {
        var baseline = FitValues(10, 11, 12, 13, 14); // median 12, median absolute deviation 1

        var typical = await baseline.PredictAsync("anomaly", Row(("value", 12)));
        var far = await baseline.PredictAsync("anomaly", Row(("value", 22)));

        Assert.Equal(12, baseline.Features.Single().Center);
        Assert.Equal(1.4826, baseline.Features.Single().Scale, 12);
        Assert.Equal(0d, (double)typical.Value!, 12);
        Assert.Equal(10 / 1.4826, (double)far.Value!, 12);
        Assert.Equal(MLTasks.Anomaly, far.Stamp.Task);
        Assert.Null(far.Confidence);
    }

    [Fact]
    public async Task Extreme_rows_in_the_fitted_data_do_not_move_what_counts_as_typical()
    {
        var clean = FitValues(10, 11, 12, 13, 14, 12.5, 11.5);
        var dirty = FitValues(10, 11, 12, 13, 14, 12.5, 11.5, 1000, -1000);

        var cleanScore = (double)(await clean.PredictAsync("anomaly", Row(("value", 40)))).Value!;
        var dirtyScore = (double)(await dirty.PredictAsync("anomaly", Row(("value", 40)))).Value!;

        Assert.InRange(dirtyScore / cleanScore, 0.5, 2.0);
        Assert.True(dirtyScore > 10, "a value far from the bulk still scores high with outliers in the fit.");
    }

    [Fact]
    public async Task When_over_half_the_values_are_equal_the_mean_absolute_deviation_supplies_the_spread()
    {
        var baseline = FitValues(5, 5, 5, 5, 9); // median 5, MAD 0, mean absolute deviation 0.8

        var score = (double)(await baseline.PredictAsync("anomaly", Row(("value", 9)))).Value!;

        Assert.Equal(1.2533 * 0.8, baseline.Features.Single().Scale, 12);
        Assert.Equal(4 / (1.2533 * 0.8), score, 12);
    }

    [Fact]
    public void A_feature_with_no_spread_carries_no_signal_and_a_fit_with_none_left_is_refused()
    {
        var definition = Definition("readings", "varies", "flat");
        var rows = new[] { 1, 2, 3, 4, 5 }.Select(v => Row(("varies", v), ("flat", 7)));

        var baseline = RobustZBaseline.Fit("m", "1", definition, rows);

        Assert.Equal(["varies"], baseline.Features.Select(f => f.Column));
        Assert.Throws<InvalidOperationException>(() => RobustZBaseline.Fit("m", "1", Definition(), [Reading(7), Reading(7)]));
    }

    [Fact]
    public async Task The_score_is_the_largest_over_the_features_and_a_missing_feature_contributes_nothing()
    {
        var definition = Definition("readings", "a", "b");
        var rows = new[] { 1, 2, 3, 4, 5 }.Select(v => Row(("a", v), ("b", v * 100)));
        var baseline = RobustZBaseline.Fit("m", "1", definition, rows);

        var onlyA = (double)(await baseline.PredictAsync("m", Row(("a", 3)))).Value!;
        var both = (double)(await baseline.PredictAsync("m", Row(("a", 3), ("b", 900)))).Value!;
        var nullB = (double)(await baseline.PredictAsync("m", Row(("a", 3), ("b", null)))).Value!;

        Assert.Equal(0d, onlyA, 12);
        Assert.Equal(600 / baseline.Features.Single(f => f.Column == "b").Scale, both, 12);
        Assert.Equal(0d, nullB, 12);
    }

    [Fact]
    public async Task A_row_with_no_scored_feature_or_a_value_that_is_not_a_number_is_refused()
    {
        var baseline = FitValues(1, 2, 3, 4, 5);

        await Assert.ThrowsAsync<ArgumentException>(async () => await baseline.PredictAsync("anomaly", Row(("other", 1))));
        await Assert.ThrowsAsync<ArgumentException>(async () => await baseline.PredictAsync("anomaly", Row(("value", null))));
        var text = await Assert.ThrowsAsync<ArgumentException>(async () => await baseline.PredictAsync("anomaly", Row(("value", "12"))));
        Assert.Contains("'value'", text.Message);
        await Assert.ThrowsAsync<ArgumentException>(async () => await baseline.PredictAsync("anomaly", Row(("value", double.NaN))));
        await Assert.ThrowsAsync<MLModelNotFoundException>(async () => await baseline.PredictAsync("missing", Row(("value", 1))));
    }

    [Fact]
    public void A_fit_reads_numbers_of_any_numeric_type_and_refuses_a_column_with_no_values()
    {
        var mixed = RobustZBaseline.Fit("m", "1", Definition(), [Reading(1), Reading(2L), Reading(3.5f), Reading(4m), Reading((short)5), Reading(null)]);

        Assert.Equal(6, mixed.Rows); // every row the fit read, including the one with no value
        Assert.Equal(3.5, mixed.Features.Single().Center);
        Assert.Throws<InvalidOperationException>(() => RobustZBaseline.Fit("m", "1", Definition(), [Reading(null)]));
        Assert.Throws<ArgumentException>(() => RobustZBaseline.Fit("m", "1", Definition(), [Reading(true)]));
    }

    [Fact]
    public async Task A_baseline_answers_a_batch_in_order_when_called_on_its_own_type_and_one_bad_row_fails_the_batch()
    {
        var anomaly = FitValues(10, 11, 12, 13, 14);
        var constant = ConstantBaseline.FitClassification("quality", "v1", Definition(), [Reading(1, "ok")]);

        var scores = await anomaly.PredictBatchAsync("anomaly", [Row(("value", 12)), Row(("value", 22))]);
        var labels = await constant.PredictBatchAsync("quality", [Row(), Row()]);

        Assert.Equal(0d, (double)scores[0].Value!, 12);
        Assert.Equal(10 / 1.4826, (double)scores[1].Value!, 12);
        Assert.Equal(["ok", "ok"], labels.Select(a => a.Value));
        Assert.NotEqual(labels[0].Stamp.PredictionId, labels[1].Stamp.PredictionId);
        await Assert.ThrowsAsync<ArgumentException>(async () => await anomaly.PredictBatchAsync("anomaly", [Row(("value", 12)), Row(("value", "x"))]));
        await Assert.ThrowsAsync<MLModelNotFoundException>(async () => await constant.PredictBatchAsync("other", [Row()]));
    }

    // ---- both, through the ports ------------------------------------------------------------------------------

    [Fact]
    public async Task A_dataset_read_through_the_source_port_fits_a_baseline_and_exports_to_csv_for_the_same_caller()
    {
        var source = new FixedSource(
            Definition("readings"),
            [Reading(10, "ok"), Reading(11, "ok"), Reading(12, "bad"), Reading(13, "ok"), Reading(14, "ok")]);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "analyst")], "test"));

        var fitted = await RobustZBaseline.FitAsync("anomaly", "v1", source.Datasets[0], source.ReadAsync("readings", caller));
        var writer = new StringWriter();
        var rows = await MLCsv.WriteAsync(source.Datasets[0], source.ReadAsync("readings", caller), writer);

        Assert.Equal(12, fitted.Features.Single().Center);
        Assert.Equal(5, rows);
        Assert.StartsWith("value,label\r\n10,ok\r\n", writer.ToString());
        Assert.Equal([caller, caller], source.Callers);
    }

    private sealed class FixedSource(MLDatasetDefinition definition, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IMLDatasetSource
    {
        public List<ClaimsPrincipal> Callers { get; } = [];

        public IReadOnlyList<MLDatasetDefinition> Datasets { get; } = [definition];

        public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadAsync(
            string dataset, ClaimsPrincipal caller, CancellationToken cancellationToken = default)
        {
            Callers.Add(caller);
            return dataset == definition.Name ? AsAsync(rows) : throw new ArgumentException($"No dataset '{dataset}'.", nameof(dataset));
        }
    }
}
