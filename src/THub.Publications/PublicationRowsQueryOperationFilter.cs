using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace THub.Publications;

public sealed class PublicationRowsQueryOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!string.Equals(
                context.ApiDescription.ActionDescriptor.EndpointMetadata
                    .OfType<IEndpointNameMetadata>()
                    .SingleOrDefault()
                    ?.EndpointName,
                "GetPublishedRows",
                StringComparison.Ordinal))
        {
            return;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "pageSize",
            In = ParameterLocation.Query,
            Required = false,
            Description = "Number of rows to return. Defaults to the active publication setting.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int32",
                Minimum = "1"
            }
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "cursor",
            In = ParameterLocation.Query,
            Required = false,
            Description = "Opaque nextCursor returned by the preceding response.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String
            }
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "filter",
            In = ParameterLocation.Query,
            Required = false,
            Description =
                "Repeat up to 16 times. Use alias:operator:value; isnull and isnotnull omit value.",
            Style = ParameterStyle.Form,
            Explode = true,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = new OpenApiSchema
                {
                    Type = JsonSchemaType.String
                }
            }
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "sort",
            In = ParameterLocation.Query,
            Required = false,
            Description =
                "Repeat up to 8 times. Use an approved alias for ascending order or prefix it with - for descending.",
            Style = ParameterStyle.Form,
            Explode = true,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = new OpenApiSchema
                {
                    Type = JsonSchemaType.String
                }
            }
        });
    }
}
