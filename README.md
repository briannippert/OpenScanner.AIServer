# OpenScanner.WhisperServer

A lightweight .NET Web API server that provides remote Whisper AI transcription for [OpenScanner](https://github.com/briannippert/OpenScanner). Run this on a more powerful machine to offload speech-to-text processing from your Raspberry Pi.

Unlike a per-clip `whisper-cli` invocation, this server keeps models **resident in memory** by managing a pool of long-lived [`whisper-server`](https://github.com/ggerganov/whisper.cpp/tree/master/examples/server) processes. The first request for a model loads it; subsequent requests reuse the warm process, and multiple clips can be transcribed **concurrently**.

## Prerequisites

- A Linux machine (Ubuntu/Debian recommended) with a GPU or fast CPU
- The installer handles everything else (.NET SDK, whisper.cpp, model download)

## Quick Start

```bash
git clone https://github.com/briannippert/OpenScanner.WhisperServer.git
cd OpenScanner.WhisperServer

chmod +x scripts/*.sh
./scripts/install_service.sh
```

The installer will:
1. Install system dependencies (.NET 10 SDK, cmake, ffmpeg)
2. Prompt you to select a default Whisper model (pre-warmed at startup)
3. Prompt you to choose a port (default: 8090)
4. Clone and build whisper.cpp — including `whisper-server` — (with CUDA if an NVIDIA GPU is detected)
5. Download your chosen model
6. Build the .NET server
7. Configure and start a systemd service

### Installer Options

- `--skip-update`: Build local code without pulling main (use this when installing a feature branch)
- `--deps-only`: Install dependencies and download the model, then exit without building or installing the service
- `--port=NNNN`: Set the listening port (skips the interactive prompt)
- `--rebuild-whisper`: Force a clean rebuild of whisper.cpp (useful after installing CUDA)

### GPU Acceleration

The installer automatically detects NVIDIA GPUs and enables CUDA acceleration when the CUDA toolkit is installed. If you install the CUDA toolkit after the initial setup, re-run the installer with `--rebuild-whisper` to recompile whisper.cpp with GPU support.

### Uninstall

```bash
./scripts/uninstall_service.sh
```

## Manual Setup

### Building whisper.cpp

The resident `whisper-server` binary is built by default as part of the examples:

```bash
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp

cmake -B build                  # CPU-only
# cmake -B build -DGGML_CUDA=ON # or with CUDA
cmake --build build --config Release   # produces build/bin/whisper-server

bash models/download-ggml-model.sh large-v3-turbo-q5_0
```

## Configuration

Edit `appsettings.json` (the installer does this automatically):

```json
{
  "Whisper": {
    "ModelsDir": "/path/to/whisper.cpp/models",
    "WhisperServerBinary": "/path/to/whisper.cpp/build/bin/whisper-server",
    "DefaultModel": "large-v3-turbo-q5_0",
    "BasePort": 8100,
    "InstancesPerModel": 1,
    "MaxResidentModels": 1,
    "Threads": 0,
    "UseGpu": true,
    "StartupTimeoutSeconds": 120,
    "TimeoutSeconds": 120,
    "DefaultPrompt": "..."
  }
}
```

| Key | Default | Purpose |
|---|---|---|
| `ModelsDir` | — | Directory of `ggml-*.bin` models (also drives `/models`) |
| `WhisperServerBinary` | — | Path to the built `whisper-server` binary |
| `DefaultModel` | `large-v3-turbo-q5_0` | Model pre-warmed at startup and used when a request omits `model` |
| `BasePort` | `8100` | First loopback port for spawned `whisper-server` processes |
| `InstancesPerModel` | `1` | Processes per model → concurrency for a single model |
| `MaxResidentModels` | `1` | Distinct models kept in memory (LRU eviction beyond this) |
| `Threads` | `0` | Threads per process (`0` = all cores) |
| `UseGpu` | `true` | Pass `-ng` to disable GPU when `false` |
| `StartupTimeoutSeconds` | `120` | How long to wait for a model to load |
| `TimeoutSeconds` | `120` | Per-inference request timeout |
| `DefaultPrompt` | radio prompt | Decoding bias when a request omits `prompt` |

Ports `BasePort` … `BasePort + InstancesPerModel × MaxResidentModels` are used internally and only bound to `127.0.0.1`. Only the .NET server's own port is exposed on the network.

## Running

If installed via the installer, the server runs as a systemd service:

```bash
systemctl status openscanner-whisper    # Check status
journalctl -u openscanner-whisper -f    # View logs
```

To run manually: `dotnet run --urls "http://0.0.0.0:8090"` (default dev port is 5000).

## Swagger / API documentation

Open `/swagger` on the running server (for example,
`http://localhost:8090/swagger`) to explore and try all five endpoints, including
WAV file uploads and JSON address requests. The OpenAPI document is available at
`/swagger/v1/swagger.json`. Both are enabled in development and production, so
Swagger is available when running the installed systemd service.

## API Endpoints

### GET /health

```json
{
  "status": "ok",
  "defaultModel": "large-v3-turbo-q5_0",
  "binaryFound": true,
  "defaultModelFound": true,
  "loadedModels": ["large-v3-turbo-q5_0"],
  "acceleration": "GPU (CUDA)",
  "cpu": "Intel Core i7-12700K",
  "gpu": "NVIDIA GeForce RTX 3080",
  "gpuMemoryMb": 10240
}
```

### GET /models

Lists the models installed in `ModelsDir`. OpenScanner queries this to populate its remote-model dropdown.

```json
{ "models": [ { "id": "large-v3-turbo-q5_0", "label": "large-v3-turbo-q5_0" }, { "id": "small.en", "label": "small.en" } ] }
```

### POST /transcribe

Transcribe an audio file (multipart form data). OpenScanner sends a pre-filtered 16 kHz mono WAV.

```bash
curl -X POST http://localhost:8090/transcribe \
  -F "file=@recording.wav" \
  -F "model=small.en" \
  -F "prompt=Dispatch, Unit 1, 10-4, copy, over."
```

Parameters:
- `file` (required): WAV audio file
- `model` (optional): model id from `/models`; defaults to `DefaultModel`
- `prompt` (optional): context prompt to guide accuracy; defaults to `DefaultPrompt`

Response:
```json
{ "text": "Unit 1, responding code 3 to Main and First." }
```

## Connecting to OpenScanner

1. Start this server on your network (`dotnet run --urls "http://0.0.0.0:8090"` or via the systemd service).
2. In the OpenScanner UI, go to **Settings → Transcription** and enable **AI Transcription**.
3. Set the **Backend** to **Remote**.
4. Enter the server URL (e.g. `http://192.168.1.100:8090`) and click **Test**.
5. Pick a **Model** from the list queried live from this server.

## Updating

Re-run the installer to pull the latest code, rebuild, and restart:

```bash
./scripts/install_service.sh
```

## License

MIT

## Address extraction (local LLM)

`POST /address` accepts JSON text and returns a fixed JSON object. It uses a separately
running local [Ollama](https://ollama.com/) service; the Whisper installer does not
install Ollama or download the LLM. After installing and starting Ollama on the server:

```bash
ollama pull qwen3:4b
```

Configure `Address:OllamaUrl` (default `http://127.0.0.1:11434/`), `Address:Model`
(default `qwen3:4b`) and `Address:TimeoutSeconds` (default `120`) in appsettings.json.
Environment overrides such as `Address__Model` also work. Keep Ollama bound to loopback.

```bash
curl http://localhost:8090/address \
  -H 'Content-Type: application/json' \
  -d '{"text":"Dispatch, respond to 123 Main Street, apartment 4, Springfield."}'
```

Example response (actual extraction depends on the model):

```json
{
  "address": "123 Main Street, apartment 4, Springfield",
  "houseNumber": "123",
  "street": "Main Street",
  "unit": "4",
  "city": "Springfield",
  "state": null,
  "postalCode": null,
  "country": null,
  "crossStreet": null
}
```

The first address is extracted if several are mentioned. Intersections use `street`
and `crossStreet`; partial addresses are supported. Missing fields are null, and
no address produces an object with every field null. This extracts text, rather
than validating a real location or geocoding it. Check accuracy against your own
radio transcripts before relying on the results.

Input must contain nonblank `text` of at most 2,000 characters (400 otherwise).
Backend failures return 503, invalid/incomplete model output returns 502, and a
queue/inference timeout returns 504. Requests are serialized, use a 4,096-token
context and a 512-token output limit, and disable Qwen3 thinking. JSON schema
constrains generation, and the server validates the returned shape. Address requests
work independently of Whisper readiness.

### Sharing an 8 GB GTX 1070 with Whisper

With Whisper using approximately 4.1 GB, a 4B model at Q4 is a practical starting
ceiling for fully GPU-resident inference. [Qwen3 4B Q4_K_M](https://ollama.com/library/qwen3:4b)
has a 2.5 GB model file; runtime buffers and the KV cache require additional memory.
This is an estimate, not a guaranteed fit. A 7–8B Q4 model generally exceeds the
remaining budget once overhead is included; CPU offloading allows larger models
at lower speed, depending on available system RAM.

For this GPU, set `Whisper:InstancesPerModel` and `Whisper:MaxResidentModels` to `1`
if you need to prevent additional Whisper copies consuming the remaining VRAM.
These settings trade transcription concurrency/model caching for memory and are
now the repository defaults. The installer subtracts `Address:ReservedVramMb`
(default 3500 MB) before sizing the Whisper pool, in addition to driver headroom.
This is only an estimate: it does not reserve GPU memory at runtime. Set it to 0
when Ollama runs on another machine or address extraction is unused. Set `OLLAMA_NUM_PARALLEL=1` and
`OLLAMA_MAX_LOADED_MODELS=1` in the **Ollama service environment**, then restart
Ollama. Test simultaneous transcription and address extraction with `nvidia-smi`
and check `ollama ps` for GPU placement. If memory is too tight, try `qwen3:1.7b`
(download it and change `Address:Model`). Models stay warm for five minutes after use.

[Ollama's hardware documentation](https://docs.ollama.com/gpu) lists the GTX 1070
as compute capability 6.1 and currently requires NVIDIA driver 570 or newer for
that generation. Verify GPU operation on the actual server; this project does
not install or update drivers.

### Development checks

Run `dotnet build` and `python3 scripts/test_address_endpoint.py`. The endpoint
checks use a local mock Ollama service and require Python 3 and loopback sockets;
they do not download a model or require a GPU. They also verify Swagger, Census
geocoding, caching, throttling, and LLM candidate selection using mock responses. To install this feature branch on
the server without pulling main, use `./scripts/install_service.sh --skip-update`.


## US address coordinates

`POST /geocode` uses the free [US Census geocoder](https://geocoding.geo.census.gov/geocoder/Geocoding_Services_API.html)
with no API key or GPU required. It sends only the supplied address to the external
provider. `/address` remains local: pass its extracted `address` to `/geocode`
when coordinates are needed, including known city/state or ZIP for disambiguation.

```bash
curl http://localhost:8090/geocode \
  -H 'Content-Type: application/json' \
  -d '{"address":"4600 Silver Hill Rd, Washington, DC 20233"}'
```

Response shape (illustrative coordinates):

```json
{
  "found": true,
  "matches": [
    {
      "matchedAddress": "4600 SILVER HILL RD, WASHINGTON, DC, 20233",
      "latitude": 38.85,
      "longitude": -76.94
    }
  ],
  "provider": "US Census Bureau",
  "selectedMatchIndex": 0,
  "selectionStatus": "single_match",
  "selectedMatch": {
    "matchedAddress": "4600 SILVER HILL RD, WASHINGTON, DC, 20233",
    "latitude": 38.85,
    "longitude": -76.94
  }
}
```

No match returns HTTP 200 with `found: false`, `matches: []`, and `selectedMatch: null`.
A single candidate is selected directly. With multiple candidates, the local LLM
compares the address and optional `context` against the candidate addresses. All
candidates remain in `matches`; `selectedMatchIndex` is zero-based and
`selectedMatch` contains the selected provider result, never LLM-generated coordinates.

```json
{
  "address": "123 Main St, VA",
  "context": "Dispatch is responding in South City."
}
```

Selection statuses are `no_match`, `single_match`, `llm_selected`, `ambiguous`
(insufficient evidence), or `llm_unavailable` (model failure, timeout, or invalid
output). In the latter two cases the selected index and match are null. A selection
is a model suggestion, not a verified location. Candidate lists over 20 entries or
selection prompts over 6,000 characters remain ambiguous to avoid truncation.
The original transcript can be supplied as context; context stays with the local
Ollama service and is not sent to Census. Ranking uses the same model and inference
queue as `/address`, with the configured address-model timeout. It does not load
a second LLM. Cached geocoding candidates are ranked afresh for each context.

 Census estimates points along street
address ranges; these are not guaranteed rooftop positions. Numbered street
addresses are required by the provider; bare intersections, landmarks, and incomplete
transcripts may not resolve. This endpoint does not supply map tiles.

The address must be nonblank and at most 500 characters; optional context is limited
to 2,000 characters (400 otherwise). Geocoding provider failures
return 503, malformed results return 502, and geocoding timeouts return 504. Ranking failures return the successful geocoding
response with `llm_unavailable`. Calls to Census are
serialized and spaced by at least one second per server process. Up to 1,000 query
results (including no matches) are cached in memory for 24 hours; restart clears
the cache. Errors are not cached or automatically retried.

`Geocoding:Endpoint`, `Geocoding:Benchmark` and `Geocoding:TimeoutSeconds` configure
the Census-compatible backend, dataset (default `Public_AR_Current`), and total
queue/request timeout (default 30 seconds). Environment overrides use double
underscores, for example `Geocoding__TimeoutSeconds=60`.
