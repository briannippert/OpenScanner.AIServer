using Microsoft.OpenApi;
using OpenScanner.WhisperServer.Services;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OpenScanner.WhisperServer.Api;

public sealed record ApiError(string Error);
public sealed record TranscriptionResponse(string Text);
public sealed record ModelInfo(string Id, string Label);
public sealed record ModelsResponse(IReadOnlyList<ModelInfo> Models);
public sealed record HealthResponse(string Status, string DefaultModel, bool BinaryFound,
    bool DefaultModelFound, IReadOnlyList<string> LoadedModels, string Acceleration,
    string Cpu, string? Gpu, int? GpuMemoryMb);
public sealed record AddressResponse(string? Address, string? HouseNumber, string? Street,
    string? Unit, string? City, string? State, string? PostalCode, string? Country, string? CrossStreet,
    GeocodeMatch? Geocode, string? PlaceName);

// The transcription handler reads the multipart form manually, so ApiExplorer
// cannot infer its upload fields from the handler parameters.
public sealed class TranscriptionOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath != "transcribe") return;
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "file" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary", Description = "WAV audio (16 kHz mono recommended)." },
                            ["model"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Optional model ID from /models; defaults to the configured model." },
                            ["prompt"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Optional decoding context; defaults to the configured prompt." }
                        }
                    }
                }
            }
        };
    }
}
