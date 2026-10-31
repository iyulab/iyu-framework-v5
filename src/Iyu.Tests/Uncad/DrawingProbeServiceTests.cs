using Iyu.Uncad;
using Xunit;

namespace Iyu.Tests.Uncad;

public class DrawingProbeServiceTests
{
    private sealed class FakeRunner(Func<IReadOnlyList<string>, UncadRunResult> reply) : IUncadRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<UncadRunResult> RunAsync(
            string verb,
            string drawingPath,
            IReadOnlyList<string> extraArgs,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(extraArgs);
            return Task.FromResult(reply(extraArgs));
        }
    }

    private const string Summary = """
        {"units": {"name": "mm"}, "entity_count": 3, "confidence": "HIGH",
         "layers": [{"name": "ANNOTATION", "color_index": 3, "entity_count": 1}],
         "dimensions": [{"id": 7, "kind": "ROTATED", "measurement": 9.0, "text": {"type": "MEASURED"}}]}
        """;

    private const string Detail = """
        {"selection": {"entities": [{"id": 7, "layer": {"data": "ANNOTATION"}, "record": {"common": {"color_index": 256}}}]}}
        """;

    private static UncadRunResult Ok(string json, params string[] warnings) => new(true, json, null, null, warnings, TimeSpan.Zero);

    private static UncadRunResult Fail(UncadFailure failure) => UncadRunResult.Fail(failure, failure.ToString(), TimeSpan.Zero);

    [Fact]
    public async Task Without_marks_one_run_reads_the_summary_and_keeps_the_program_warnings()
    {
        var runner = new FakeRunner(_ => Ok(Summary, "warning: partial"));

        var result = await new DrawingProbeService(runner).ProbeAsync("a.dwg");

        Assert.True(result.Ok);
        Assert.Single(runner.Calls);
        Assert.Equal("warning: partial", Assert.Single(result.Warnings));
        Assert.Null(Assert.Single(result.Probe!.Dimensions).Layer);
    }

    [Fact]
    public async Task With_marks_a_second_run_adds_the_layer_and_effective_color()
    {
        var runner = new FakeRunner(args => args.Count == 0 ? Ok(Summary) : Ok(Detail));

        var result = await new DrawingProbeService(runner).ProbeAsync("a.dwg", includeMarks: true);

        Assert.True(result.Ok);
        Assert.Equal(["--type", "DIMENSION", "--detail", "--limit", "100000"], runner.Calls[1]);
        var dimension = Assert.Single(result.Probe!.Dimensions);
        Assert.Equal("ANNOTATION", dimension.Layer);
        Assert.Equal(3, dimension.ColorIndex);
        Assert.False(result.MarksFailed);
    }

    [Fact]
    public async Task A_failed_marks_run_keeps_the_dimension_list_and_says_the_marks_failed()
    {
        var runner = new FakeRunner(args => args.Count == 0 ? Ok(Summary) : Fail(UncadFailure.Timeout));

        var result = await new DrawingProbeService(runner).ProbeAsync("a.dwg", includeMarks: true);

        Assert.True(result.Ok);
        Assert.True(result.MarksFailed);
        Assert.Single(result.Probe!.Dimensions);
    }

    [Fact]
    public async Task Broken_marks_output_keeps_the_dimension_list_and_says_the_marks_failed()
    {
        var runner = new FakeRunner(args => args.Count == 0 ? Ok(Summary) : Ok("not json"));

        var result = await new DrawingProbeService(runner).ProbeAsync("a.dwg", includeMarks: true);

        Assert.True(result.Ok);
        Assert.True(result.MarksFailed);
        var dimension = Assert.Single(result.Probe!.Dimensions);
        Assert.Null(dimension.Layer);
    }

    [Theory]
    [InlineData(UncadFailure.Timeout)]
    [InlineData(UncadFailure.NonZeroExit)]
    [InlineData(UncadFailure.NotConfigured)]
    public async Task A_runner_failure_is_reported_not_thrown(UncadFailure failure)
    {
        var result = await new DrawingProbeService(new FakeRunner(_ => Fail(failure))).ProbeAsync("a.dwg");

        Assert.False(result.Ok);
        Assert.Equal(failure, result.Failure);
        Assert.Null(result.Probe);
    }

    [Fact]
    public async Task Exit_zero_with_unreadable_output_is_an_invalid_output_failure()
    {
        var result = await new DrawingProbeService(new FakeRunner(_ => Ok("garbage"))).ProbeAsync("a.dwg");

        Assert.False(result.Ok);
        Assert.Equal(UncadFailure.InvalidOutput, result.Failure);
    }
}
