using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace OrderFleet.Api.Configuration;

public static class OpenApiRegistration
{
    public static void AddOrderFleetOpenApi(this IServiceCollection services) => services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "OrderFleet API";
            document.Info.Version = "v1";
            document.Components ??= new();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            };
            document.Security = [new OpenApiSecurityRequirement
                { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            operation.Responses ??= new();
            var anonymous = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any();
            if (anonymous) operation.Security = [];
            else
            {
                operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentication required." });
                operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Access denied." });
            }
            if (context.Description.HttpMethod is "POST" or "PUT")
            {
                operation.Responses.TryAdd("400", new OpenApiResponse { Description = "Invalid request." });
                operation.Responses.TryAdd("409", new OpenApiResponse { Description = "Resource state or idempotency conflict." });
            }
            if (context.Description.RelativePath?.TrimEnd('/') == "api/v1/orders" && context.Description.HttpMethod == "POST")
            {
                operation.Responses.TryAdd("402", new OpenApiResponse { Description = "Payment authorization declined." });
                operation.Responses.TryAdd("503", new OpenApiResponse { Description = "Payment provider unavailable; retry safely with the same key." });
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Scoped to the authenticated customer and order creation. Reuse only for the same request.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 100 }
                });
            }
            if (context.Description.RelativePath?.EndsWith("/accept") == true)
                operation.Responses.TryAdd("503", new OpenApiResponse { Description = "Retryable failure or uncertain payment outcome; the stable capture operation is reconciled on retry." });
            if (context.Description.RelativePath?.StartsWith("api/v1/operations/", StringComparison.Ordinal) == true
                && context.Description.HttpMethod == "POST")
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Scoped to the administrator and operation. Reuse only for the same target.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 100 }
                });
            }
            return Task.CompletedTask;
        });
    });
}
