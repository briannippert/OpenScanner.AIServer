using OpenScanner.WhisperServer.Api;
using Microsoft.OpenApi;
using OpenScanner.WhisperServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "OpenScanner Whisper Server",
        Version = "v1",
        Description = "Local audio transcription and address extraction."
    });
    options.SupportNonNullableReferenceTypes();
    options.OperationFilter<TranscriptionOperationFilter>();
});
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("address", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<AddressExtractor>();
builder.Services.AddHttpClient("geocoding", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenScanner.WhisperServer/1.0");
}).RemoveAllLoggers();
builder.Services.AddSingleton<AddressGeocoder>();
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

app.MapPost("/transcribe", async (HttpRequest request, WhisperServerPool pool, ILogger<Program> logger) =>
{
    if (!pool.IsReady())
    {
        return Results.Json(new { error = "Whisper is not configured. Check binary and model paths." },
            statusCode: 503);
    }

    if (!request.HasFormContentType)
    {
        return Results.Json(new { error = "Request must be multipart/form-data." }, statusCode: 400);
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        return Results.Json(new { error = "No audio file provided. Send as 'file' in multipart form data." },
            statusCode: 400);
    }

    var prompt = form.TryGetValue("prompt", out var promptValues) ? promptValues.ToString() : null;
    var model = form.TryGetValue("model", out var modelValues) ? modelValues.ToString() : null;

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
        return Results.Ok(new { text = text ?? "" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Transcription failed for {FileName}", file.FileName);
        return Results.Json(new { error = "Transcription failed: " + ex.Message }, statusCode: 500);
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
})
.WithName("Transcribe").WithTags("Transcription")
.WithSummary("Transcribe a WAV audio upload")
.Produces<TranscriptionResponse>()
.Produces<ApiError>(400).Produces<ApiError>(503).Produces<ApiError>(500);

app.MapPost("/address", async (AddressRequest request, AddressExtractor extractor,
    HttpContext context, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > extractor.MaxTextLength)
        return Results.Json(new { error = $"Text is required and must be at most {extractor.MaxTextLength} characters." }, statusCode: 400);
    try
    {
        return Results.Ok(await extractor.ExtractAsync(request.Text, context.RequestAborted));
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        return Results.Json(new { error = "Address extraction timed out." }, statusCode: 504);
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning(ex, "Address model unavailable");
        return Results.Json(new { error = "Address model unavailable. Check Ollama and the configured model." }, statusCode: 503);
    }
    catch (System.Text.Json.JsonException ex)
    {
        logger.LogWarning(ex, "Invalid address model response");
        return Results.Json(new { error = "Address model returned invalid or incomplete JSON." }, statusCode: 502);
    }
})
.WithName("ExtractAddress").WithTags("Address")
.WithSummary("Extract the first address or intersection from text")
.WithDescription("Send nonblank text of at most 2,000 characters. Missing address fields are null; if no address is found, all fields are null.")
.Produces<AddressResponse>()
.Produces<ApiError>(400).Produces<ApiError>(502).Produces<ApiError>(503).Produces<ApiError>(504);

app.MapPost("/geocode", async (GeocodeRequest request, AddressGeocoder geocoder,
    AddressExtractor extractor, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length > 500)
        return Results.BadRequest(new ApiError("Address is required and must be at most 500 characters."));
    if (request.Context?.Length > 2000)
        return Results.BadRequest(new ApiError("Context must be at most 2,000 characters."));
    try
    {
        var result = await geocoder.GeocodeAsync(request.Address, context.RequestAborted);
        if (result.Matches.Count > 1)
        {
            try
            {
                var selected = await extractor.SelectMatchAsync(request.Address, request.Context,
                    result.Matches, context.RequestAborted);
                result = result with { SelectedMatchIndex = selected,
                    SelectionStatus = selected.HasValue ? "llm_selected" : "ambiguous" };
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException ||
                (ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested))
            {
                // Geocoding succeeded; retain candidates even if local ranking failed.
                result = result with { SelectionStatus = "llm_unavailable" };
            }
        }
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
