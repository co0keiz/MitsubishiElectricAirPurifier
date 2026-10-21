using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AirPurifier.Api;

internal sealed class ApiDocumentTransformer(IConfiguration configuration) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Mitsubishi Electric Air Purifier API",
            Version = "v1",
            Description = "Read status, switch power, select Normal/Night/Turbo auto mode, or " +
                          "Silent/Low/Mid/High/Turbo manual speed, switch Smart Search ON/OFF, or start pre-filter auto cleaning. " +
                          "Writes wait for separate device readback. Mode/speed/Smart Search changes and cleaning require power ON. " +
                          "Cleaning success confirms the cycle has started, not that it has finished."
        };

        if (string.IsNullOrEmpty(configuration["Api:ApiKey"]))
            return Task.CompletedTask;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Api-Key",
            Description = "Enter the configured API key."
        };
        foreach (var path in document.Paths.Where(path => path.Key.StartsWith("/api/", StringComparison.Ordinal)))
        {
            if (path.Value.Operations is null) continue;
            foreach (var operation in path.Value.Operations.Values)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("ApiKey", document)] = []
                });
            }
        }
        return Task.CompletedTask;
    }
}
