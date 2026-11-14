using Microsoft.OpenApi;
using OpenScanner.AIServer.Services;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OpenScanner.AIServer.Api;

public sealed record ApiError(string Error);
public sealed record TranscriptionResponse(string Text);
public sealed record TranscriptionWithMetadataResponse(string Text, object? Metadata, string? MetadataError);
public sealed record ModelInfo(string Id, string Label);
public sealed record ModelsResponse(IReadOnlyList<ModelInfo> Models);
public sealed record HealthResponse(string Status, string Version, string DefaultModel, bool BinaryFound,
    bool DefaultModelFound, IReadOnlyList<string> LoadedModels, string Acceleration,
    string Cpu, string? Gpu, int? GpuMemoryMb, IReadOnlyList<GazetteerCity> Gazetteer);
public sealed record AddressResponse(string? Address, string? HouseNumber, string? Street,
    string? Unit, string? City, string? State, string? PostalCode, string? Country, string? CrossStreet,
    GeocodeMatch? Geocode, string? PlaceName);

public sealed record TransmissionRequest(string? Text, string? Service = null,
    string? DefaultCity = null, string? DefaultState = null);
public sealed record TransmissionResponse(string CallType, string? Service, string Summary,
    string? IncidentStatus, AddressResponse Location);
public sealed record EmptyTransmissionResponse;

public sealed class TransmissionOperationFilter : IOperationFilter
{
    internal static OpenApiSchema ResponseSchema(OperationFilterContext context, bool allowNull = false)
    {
        var choices = new List<IOpenApiSchema>
        {
            context.SchemaGenerator.GenerateSchema(typeof(TransmissionResponse), context.SchemaRepository),
            new OpenApiSchema { Type = JsonSchemaType.Object, MaxProperties = 0, AdditionalPropertiesAllowed = false }
        };
        if (allowNull) choices.Add(new OpenApiSchema { Type = JsonSchemaType.Null });
        return new OpenApiSchema { OneOf = choices };
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath != "transmission") return;
        operation.Responses!["200"].Content!["application/json"].Schema = ResponseSchema(context);
    }
}

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
                            ["prompt"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Optional decoding context; defaults to the configured prompt." },
                            ["includeMetadata"] = new OpenApiSchema { Type = JsonSchemaType.Boolean, Description = "Set true to extract incident metadata and location from the transcript; defaults to false." },
                            ["service"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Optional police or fire context for metadata extraction." },
                            ["defaultCity"] = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 100, Description = "User's default city for metadata lookup; spoken city takes precedence. Falls back to server configuration." },
                            ["defaultState"] = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 100, Description = "User's default state for metadata lookup; spoken state takes precedence. Falls back to server configuration." }
                        }
                    }
                }
            }
        };
        operation.Responses!["200"].Content!["application/json"].Schema = new OpenApiSchema
        {
            OneOf = [
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false,
                    Required = new HashSet<string> { "text" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["text"] = new OpenApiSchema { Type = JsonSchemaType.String }
                    }
                },
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = false,
                    Required = new HashSet<string> { "text", "metadata", "metadataError" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["text"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["metadata"] = TransmissionOperationFilter.ResponseSchema(context, true),
                        ["metadataError"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null }
                    }
                }
            ]
        };
    }
}
