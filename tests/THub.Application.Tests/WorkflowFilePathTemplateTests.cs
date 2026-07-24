using THub.Application.Execution;

namespace THub.Application.Tests;

public sealed class WorkflowFilePathTemplateTests
{
    [Fact]
    public void RenderExpandsRunDateAndWorkflowVariables()
    {
        var variables = new Dictionary<string, TabularValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["runStartedAtUtc"] = TabularValue.From(
                new DateTimeOffset(2026, 7, 21, 8, 1, 2, 81, TimeSpan.Zero)),
            ["region"] = TabularValue.From("taipei")
        };

        var result = WorkflowFilePathTemplate.Render(
            @"outbound\export_{region}_{runStartedAtUtc:yyyyMMdd_HHmmss_fff}.csv",
            variables);

        Assert.Equal(@"outbound\export_taipei_20260721_080102_081.csv", result);
    }

    [Theory]
    [InlineData("..\\escape")]
    [InlineData("..")]
    [InlineData("nested/file")]
    [InlineData("bad:name")]
    public void RenderRejectsUnsafeVariableSegments(string value)
    {
        var variables = new Dictionary<string, TabularValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = TabularValue.From(value)
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkflowFilePathTemplate.Render("export_{name}.csv", variables));

        Assert.Contains("not safe", exception.Message);
    }

    [Fact]
    public void GetVariableNamesRejectsMalformedPlaceholder()
    {
        Assert.Throws<ArgumentException>(() =>
            WorkflowFilePathTemplate.GetVariableNames("export_{runStartedAtUtc.csv"));
    }
}
