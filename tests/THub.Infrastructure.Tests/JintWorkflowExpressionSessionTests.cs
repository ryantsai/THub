using THub.Application.Execution;
using THub.Domain.Workflows;
using THub.Infrastructure.Execution;

namespace THub.Infrastructure.Tests;

public sealed class JintWorkflowExpressionSessionTests
{
    [Fact]
    public void EvaluatesReusableFunctionWithRowAndWorkflowVariables()
    {
        var factory = new JintWorkflowExpressionSessionFactory();
        var variables = new Dictionary<string, TabularValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["region"] = TabularValue.From("north")
        };
        using var session = factory.Create(
            [new("normalize", ["value"], "String(value).trim().toUpperCase()")],
            variables,
            CancellationToken.None);
        var schema = new TabularSchema(
            [new TabularColumn("Name", TabularDataType.String, false)]);
        var row = new TabularRow([TabularValue.From(" Alice ")]);

        var value = session.Evaluate(
            "normalize(row.Name) + '-' + vars.region",
            schema,
            row,
            TabularDataType.String,
            CancellationToken.None);

        Assert.Equal("ALICE-north", value.Value);
    }

    [Fact]
    public void RejectsDynamicStringCompilation()
    {
        var factory = new JintWorkflowExpressionSessionFactory();
        using var session = factory.Create(
            [],
            new Dictionary<string, TabularValue>(),
            CancellationToken.None);
        var schema = new TabularSchema(
            [new TabularColumn("Value", TabularDataType.Int64, false)]);
        var row = new TabularRow([TabularValue.From(1L)]);

        var exception = Assert.Throws<WorkflowNodeExecutionException>(() =>
            session.Evaluate(
                "eval('40 + 2')",
                schema,
                row,
                TabularDataType.Int64,
                CancellationToken.None));

        Assert.Equal("execution.javascript.failed", exception.Error.Code);
    }
}
