using OpenScanner.AIServer.Api;
using Microsoft.OpenApi;
using OpenScanner.AIServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "OpenScanner AI Server",
        Version = "v1",
        Description = "Local audio transcription, incident classification, and location lookup."
    });
    options.SupportNonNullableReferenceTypes();
    options.OperationFilter<TranscriptionOperationFilter>();
    options.OperationFilter<TransmissionOperationFilter>();
});
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("address", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<AddressExtractor>();
builder.Services.AddSingleton<TransmissionAnalyzer>();
builder.Services.AddSingleton<LocationEnricher>();
builder.Services.AddHttpClient("geocoding", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenScanner.AIServer/1.0");
}).RemoveAllLoggers();
// One shared per-host rate limit for Census and Photon across every geocoder.
builder.Services.AddSingleton<UpstreamThrottle>();
builder.Services.AddSingleton<AddressGeocoder>();
builder.Services.AddSingleton<GeocodeResolver>();
builder.Services.AddSingleton<PlaceGeocoder>();
builder.Services.AddSingleton<StreetGeocoder>();
// Non-logging client for internal loopback calls to whisper-server: the health
// poll during model startup would otherwise flood the log with expected
// connection-refused messages while the child boots.
builder.Services.AddHttpClient("whisper").RemoveAllLoggers();
builder.Services.AddSingleton<WhisperServerPool>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WhisperServerPool>());
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("v1/swagger.json", "OpenScanner API v1"));

app.MapGet("/health", (WhisperServerPool pool) =>
{
    var ready = pool.IsReady();
    var hw = pool.Hardware;
    return Results.Ok(new
    {
        status = ready ? "ok" : "error",
        defaultModel = pool.DefaultModel,
        binaryFound = pool.BinaryExists,
        defaultModelFound = pool.DefaultModelExists,
        loadedModels = pool.LoadedModels(),
        acceleration = hw.AccelerationMode,
        cpu = hw.Cpu,
        gpu = hw.Gpu,
        gpuMemoryMb = hw.GpuMemoryMb
    });
})
.WithName("GetHealth").WithTags("Server")
.WithSummary("Get Whisper readiness and hardware information")
.Produces<HealthResponse>();

app.MapGet("/models", (WhisperServerPool pool) =>
{
    var models = pool.GetAvailableModels()
        .Select(id => new { id, label = id })
        .ToList();
    return Results.Ok(new { models });
})
.WithName("GetModels").WithTags("Server")
.WithSummary("List installed Whisper models")
.Produces<ModelsResponse>();

app.MapPost("/transcribe", async (HttpRequest request, WhisperServerPool pool, TransmissionAnalyzer analyzer, AddressExtractor extractor, ILogger<Program> logger) =>
{
    if (!pool.IsReady())
    {
        return Results.Json(new ApiError("Whisper is not configured. Check binary and model paths."),
            statusCode: 503);
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new ApiError("Request must be multipart/form-data."));
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new ApiError("No audio file provided. Send as 'file' in multipart form data."));
    }

    var prompt = form.TryGetValue("prompt", out var promptValues) ? promptValues.ToString() : null;
    var model = form.TryGetValue("model", out var modelValues) ? modelValues.ToString() : null;

    var includeMetadata = false;
    if (form.TryGetValue("includeMetadata", out var metadataValue) &&
        !bool.TryParse(metadataValue.ToString(), out includeMetadata))
        return Results.BadRequest(new ApiError("includeMetadata must be true or false."));
    var service = LocationDefaults.NormalizeService(form["service"].ToString());
    if (!LocationDefaults.ValidService(service))
        return Results.BadRequest(new ApiError(LocationDefaults.ServiceError));
    var defaultCity = LocationDefaults.Normalize(form["defaultCity"].ToString());
    var defaultState = LocationDefaults.Normalize(form["defaultState"].ToString());
    if (!LocationDefaults.Valid(defaultCity, defaultState))
        return Results.BadRequest(new ApiError(LocationDefaults.Error));

    // Save uploaded file to temp location (expected: 16 kHz mono WAV from OpenScanner).
    var tempDir = Path.Combine(Path.GetTempPath(), "whisper-server");
    Directory.CreateDirectory(tempDir);
    var ext = Path.GetExtension(file.FileName);
    if (string.IsNullOrEmpty(ext)) ext = ".wav";
    var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}{ext}");

    try
    {
        await using (var stream = new FileStream(tempPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        logger.LogInformation("Transcribing {FileName} ({Size} bytes, model={Model})",
            file.FileName, file.Length, string.IsNullOrWhiteSpace(model) ? pool.DefaultModel : model);

        var text = await pool.TranscribeAsync(tempPath, model, prompt, request.HttpContext.RequestAborted);
        text ??= "";
        if (!includeMetadata) return Results.Ok(new TranscriptionResponse(text));
        if (text.Length > extractor.MaxTextLength)
            return Results.Ok(new TranscriptionWithMetadataResponse(text, null,
                $"Transcript exceeds the {extractor.MaxTextLength}-character metadata limit."));
        if (string.IsNullOrWhiteSpace(text))
            return Results.Ok(new TranscriptionWithMetadataResponse(text, new EmptyTransmissionResponse(), null));
        try
        {
            object metadata = (object?)await analyzer.AnalyzeAsync(text, service, defaultCity, defaultState,
                request.HttpContext.RequestAborted) ?? new EmptyTransmissionResponse();
            return Results.Ok(new TranscriptionWithMetadataResponse(text, metadata, null));
        }
        // Any metadata failure (other than the client going away) must preserve the transcript.
        catch (Exception ex) when (!request.HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Optional transcription metadata failed");
            var error = ex switch
            {
                OperationCanceledException => "Metadata extraction timed out.",
                HttpRequestException => "Metadata model unavailable.",
                System.Text.Json.JsonException => "Metadata model returned invalid or incomplete JSON.",
                _ => "Metadata extraction failed."
            };
            return Results.Ok(new TranscriptionWithMetadataResponse(text, null, error));
        }
    }
    catch (WhisperModelException ex)
    {
        return Results.BadRequest(new ApiError(ex.Message));
    }
    catch (WhisperInferenceException ex)
    {
        logger.LogError(ex, "Transcription failed for {FileName}", file.FileName);
        return Results.Json(new ApiError("Transcription failed: " + ex.Message), statusCode: 502);
    }
    catch (OperationCanceledException) when (!request.HttpContext.RequestAborted.IsCancellationRequested)
    {
        logger.LogWarning("Transcription timed out for {FileName}", file.FileName);
        return Results.Json(new ApiError("Transcription timed out."), statusCode: 504);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected; nothing to send.
        return Results.Empty;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Transcription failed for {FileName}", file.FileName);
        return Results.Json(new ApiError("Transcription failed: " + ex.Message), statusCode: 500);
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
})
.WithName("Transcribe").WithTags("Transcription")
.WithSummary("Transcribe a WAV audio upload with optional incident metadata")
.WithDescription("Set includeMetadata=true for metadata and location lookup. Optional service is police/fire. defaultCity/defaultState provide request-scoped locality hints; spoken locality takes precedence. The default response remains {text}. Metadata failures preserve the text and return metadata:null with metadataError.")
.Produces<TranscriptionResponse>()
.Produces<ApiError>(400).Produces<ApiError>(500).Produces<ApiError>(502).Produces<ApiError>(503).Produces<ApiError>(504);

app.MapPost("/address", async (AddressRequest request, AddressExtractor extractor, LocationEnricher locations,
    HttpContext context, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > extractor.MaxTextLength)
        return Results.BadRequest(new ApiError($"Text is required and must be at most {extractor.MaxTextLength} characters."));
    if (!LocationDefaults.Valid(request.DefaultCity, request.DefaultState))
        return Results.BadRequest(new ApiError(LocationDefaults.Error));
    try
    {
        var fields = await extractor.ExtractAsync(request.Text, context.RequestAborted);
        return Results.Ok(await locations.EnrichAsync(fields, request.Text, context.RequestAborted, request.DefaultCity, request.DefaultState));
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        return Results.Json(new ApiError("Address extraction timed out."), statusCode: 504);
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning(ex, "Address model unavailable");
        return Results.Json(new ApiError("Address model unavailable. Check Ollama and the configured model."), statusCode: 503);
    }
    catch (System.Text.Json.JsonException ex)
    {
        logger.LogWarning(ex, "Invalid address model response");
        return Results.Json(new ApiError("Address model returned invalid or incomplete JSON."), statusCode: 502);
    }
})
.WithName("ExtractAddress").WithTags("Address")
.WithSummary("Extract and locate an address, business, or landmark from text")
.WithDescription("Send nonblank text of at most 2,000 characters. Missing address fields are null; if no address is found, all fields are null. Extracts business names into placeName. Automatically geocodes street addresses through Census and businesses through a local directory or Photon, using request defaultCity/defaultState or the configured default area and uses the original text locally to resolve ambiguous candidates. The geocode field contains the selected coordinates, or null if unresolved or unavailable.")
.Produces<AddressResponse>()
.Produces<ApiError>(400).Produces<ApiError>(502).Produces<ApiError>(503).Produces<ApiError>(504);

app.MapPost("/transmission", async (TransmissionRequest request, AddressExtractor extractor,
    TransmissionAnalyzer analyzer, HttpContext context, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > extractor.MaxTextLength)
        return Results.BadRequest(new ApiError($"Text is required and must be at most {extractor.MaxTextLength} characters."));
    var service = LocationDefaults.NormalizeService(request.Service);
    if (!LocationDefaults.ValidService(service))
        return Results.BadRequest(new ApiError(LocationDefaults.ServiceError));
    if (!LocationDefaults.Valid(request.DefaultCity, request.DefaultState))
        return Results.BadRequest(new ApiError(LocationDefaults.Error));
    try
    {
        var result = await analyzer.AnalyzeAsync(request.Text, service, request.DefaultCity,
            request.DefaultState, context.RequestAborted);
        return result is null ? Results.Ok(new EmptyTransmissionResponse()) : Results.Ok(result);
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        return Results.Json(new ApiError("Transmission extraction timed out."), statusCode: 504);
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning(ex, "Transmission model unavailable");
        return Results.Json(new ApiError("Transmission model unavailable. Check Ollama and the configured model."), statusCode: 503);
    }
    catch (System.Text.Json.JsonException ex)
    {
        logger.LogWarning(ex, "Invalid transmission model response");
        return Results.Json(new ApiError("Transmission model returned invalid or incomplete JSON."), statusCode: 502);
    }
})
.WithName("ExtractTransmission").WithTags("Transmission")
.WithSummary("Extract incident metadata and location from a radio transmission")
.WithDescription("Returns {} for routine chatter. Substantive transmissions return callType, service, summary, incidentStatus and location (the existing address and geocode fields). Optional service input is police or fire and overrides inference. The supplied channel restricts call types: police uses security_alarm (not alarm/fire/medical/rescue); fire excludes police-only types. Out-of-channel categories become other while summary/location are preserved. Radio addresses using Fire alarm identify dispatch, not an alarm incident. defaultCity/defaultState override configured lookup defaults; spoken locality takes precedence. Unmatched street addresses first try a unique full-address Photon building match, then locality-filtered street-name repair, cross-street proximity checks, and Census revalidation. Street-only locations can return an approximate mapped-street point. geocode includes precision (building/address/place/street) and provider; extracted text is preserved. No incident tracking or grouping is performed. Call types: " + string.Join(", ", AddressExtractor.CallTypes) + ". Police: " + string.Join(", ", AddressExtractor.AllowedCallTypes("police")) + ". Fire: " + string.Join(", ", AddressExtractor.AllowedCallTypes("fire")))
.Produces<TransmissionResponse>()
.Produces<ApiError>(400).Produces<ApiError>(502).Produces<ApiError>(503).Produces<ApiError>(504);

app.MapPost("/geocode", async (GeocodeRequest request, GeocodeResolver resolver,
    HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length > 500)
        return Results.BadRequest(new ApiError("Address is required and must be at most 500 characters."));
    if (request.Context?.Length > 2000)
        return Results.BadRequest(new ApiError("Context must be at most 2,000 characters."));
    try
    {
        var result = await resolver.ResolveAsync(request.Address, request.Context, context.RequestAborted);
        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        return Results.Json(new ApiError("Geocoding timed out."), statusCode: 504);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new ApiError("Geocoding provider unavailable. Try again later."), statusCode: 503);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.Json(new ApiError("Geocoding provider returned invalid data."), statusCode: 502);
    }
})
.WithName("GeocodeAddress").WithTags("Address")
.WithSummary("Find map coordinates for a US street address")
.WithDescription("Sends the address to the US Census geocoder. Include a house number, street, and city/state or ZIP. Returns all candidate matches and uses the local LLM to select among multiple matches using optional context (up to 2,000 characters). Selection may remain null. Coordinates are estimates, not verified building locations. Intersections are not supported by this provider.")
.Produces<GeocodeResponse>()
.Produces<ApiError>(400).Produces<ApiError>(502).Produces<ApiError>(503).Produces<ApiError>(504);

app.Run();
