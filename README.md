# OpenScanner.WhisperServer

A lightweight .NET Web API server that provides remote Whisper AI transcription for [OpenScanner](https://github.com/briannippert/OpenScanner). Run this on a more powerful machine to offload speech-to-text processing from your Raspberry Pi.

Unlike a per-clip `whisper-cli` invocation, this server keeps models **resident in memory** by managing a pool of long-lived [`whisper-server`](https://github.com/ggerganov/whisper.cpp/tree/master/examples/server) processes. The first request for a model loads it; subsequent requests reuse the warm process, and multiple clips can be transcribed **concurrently**.

## Prerequisites

- A Linux machine (Ubuntu/Debian recommended) with a GPU or fast CPU
- The installer handles .NET, whisper.cpp, Ollama, and both model downloads

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
5. Install/start Ollama and select a metadata/address LLM (keep the configured model, default `qwen3:4b`)
6. Download missing models and verify Whisper loading plus LLM structured JSON output
7. Build the .NET server
8. Configure and start the application service, ordered after managed local Ollama

### Installer Options

- `--skip-update`: Build local code without pulling main (use this when installing a feature branch)
- `--deps-only`: Set up dependencies, Ollama, and both model downloads/checks, then exit without building/deploying the application. A previously running Whisper service is restored.
- `--port=NNNN`: Set the listening port (skips the interactive prompt)
- `--rebuild-whisper`: Force a clean rebuild of whisper.cpp (useful after installing CUDA)
- `--llm-model=NAME`: Select the Ollama model without its interactive prompt (`--ollama-model` is an alias)
- `--ollama-url=URL`: Override the Ollama origin; otherwise use `Address.OllamaUrl` (or `Address__OllamaUrl`)
- `--upgrade-ollama`: Force an upgrade of the managed local Ollama installation using the official installer
- `--skip-ollama`: Leave Ollama and `Address` settings unchanged; run only the existing Whisper/application setup
- `--help`: Show all installer options without sudo

For this checkout on port 8099:

```bash
bash scripts/install_service.sh --skip-update --port=8099 --llm-model=qwen3:4b
```

The LLM prompt offers `qwen3:4b`, `qwen3:1.7b`, or a custom model. Enter keeps
`Address.Model` (or `Address__Model`). The explicit CLI model takes precedence.
Whisper model selection remains separate. A model is downloaded only if missing;
Ollama verifies downloads and the installer then tests loading with the same
4,096-token context, `think=false`, and JSON Schema support used by the API. The
short probe unloads the LLM afterward so Whisper startup can use the GPU. Model
and URL settings are saved only after verification succeeds.

For a local HTTP loopback endpoint with an explicit port, the installer installs
Ollama if its CLI/service is missing, starts/enables `ollama.service`, and writes
`/etc/systemd/system/ollama.service.d/openscanner.conf`. This binds to loopback and
sets `OLLAMA_NUM_PARALLEL=1` and `OLLAMA_MAX_LOADED_MODELS=1`. Other drop-ins are
preserved. Current Ollama is retained unless `--upgrade-ollama` is supplied or a
model download reports that a newer version is required; in that case the official
[Linux installer](https://docs.ollama.com/linux) is run once and setup retries.
The required download tools (`curl`, CA certificates, `zstd`) are installed first.

For a configured remote URL or HTTPS reverse proxy, only model download and the
API verification run against that endpoint. Its local installation and service are
not changed. Remote upgrades must be performed on that host. URLs must be HTTP(S)
origins without credentials, paths, query strings, or fragments. Loopback ports
managed by this installer must be 1024–65535. `--skip-ollama` cannot be combined
with model, URL, or Ollama upgrade options.

If a setup step fails, the installer attempts to restart a Whisper service that
was running beforehand. This restores availability; it is not a transactional
rollback of dependencies or configuration already installed. Uninstalling the
Whisper service leaves Ollama and its model cache installed, since other apps may
use them.

Installer checks: `bash -n scripts/install_service.sh scripts/setup_ollama.sh` and
`python3 scripts/test_ollama_setup.py`. The provisioning suite uses local HTTP and
mock system commands; it never changes real services, `/etc`, packages, or models.


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

## User locality and optional metadata on audio uploads

OpenScanner can pass the user's location settings as `defaultCity` and
`defaultState`. Both are optional and apply only to the current lookup. Precedence:
explicitly extracted city/state, then request defaults, then `Geocoding:DefaultCity`
and `Geocoding:DefaultState`. Defaults do not become claimed spoken location fields.
Blank defaults use server settings. Nonblank values are limited to 100 characters,
without commas or control characters. These parameters are supported on `/address`
and `/transmission` JSON requests and `/transcribe` multipart uploads.

```bash
curl http://localhost:8099/transcribe \
  -F 'file=@dispatch.wav' \
  -F 'includeMetadata=true' \
  -F 'service=fire' \
  -F 'defaultCity=Salem' \
  -F 'defaultState=NH'
```

Without `includeMetadata=true`, `/transcribe` continues to return only `{ "text": "..." }`.
With it enabled, the response has `text`, `metadata`, and `metadataError`.
`metadata` contains the same object as `/transmission`, or `{}` for routine chatter
or silence. Successful metadata extraction has `metadataError: null`. If extraction
fails or the transcript exceeds 2,000 characters, the endpoint preserves the full
transcript, returns HTTP 200 with `metadata: null`, and explains the failure in
`metadataError`. Audio/transcription failures retain their existing error behavior.
`service` is optional police/fire context; it does not change the audio decoding prompt.
Metadata adds model and optional geocoding latency. Swagger shows all upload fields
and both response modes.

```json
{
  "text": "Engine 2 dispatched for a structure fire.",
  "metadata": {
    "callType": "fire",
    "service": "fire",
    "summary": "Engine 2 dispatched for a structure fire.",
    "incidentStatus": "dispatched",
    "location": {
      "address": null, "houseNumber": null, "street": null, "unit": null,
      "city": null, "state": null, "postalCode": null, "country": null,
      "crossStreet": null, "placeName": null, "geocode": null
    }
  },
  "metadataError": null
}
```

## Transmission metadata

`POST /transmission` parses one transmission into incident metadata and location.
It uses the same local Ollama model, serialized inference queue, and optional
geocoding as `/address`. The existing `/address` contract is unchanged.

```bash
curl http://localhost:8099/transmission \
  -H 'Content-Type: application/json' \
  -d '{"text":"207, Ill stop. 207. 42, Kelly Road, Mass, 3, Victor, Yankee Yankee, 6-2. Copy.","service":"police","defaultCity":"Salem","defaultState":"NH"}'
```

Example (coordinates omitted here):

```json
{
  "callType": "traffic_stop",
  "service": "police",
  "summary": "Traffic stop at 42 Kelly Road.",
  "incidentStatus": null,
  "location": {
    "address": "42 Kelly Road",
    "houseNumber": "42",
    "street": "Kelly Road",
    "unit": null,
    "city": null,
    "state": null,
    "postalCode": null,
    "country": null,
    "crossStreet": null,
    "placeName": null,
    "geocode": null
  }
}
```

Routine chatter such as “copy”, “I'm out”, and “roger” returns HTTP 200 with `{}`,
even with a supplied service. No geocoder is called for an empty result. Substantive
calls can have metadata without a location (for example, a medical complaint).
Missing fields are null. The input `service` is optional (`police` or `fire`,
case insensitive); when supplied it overrides inference. Otherwise service is
inferred only when clear, or null. The model classifies the first incident mentioned.

Stable call types for filters: `traffic_stop`, `traffic_collision`, `fire`, `medical`,
`rescue`, `alarm`, `assault`, `domestic_disturbance`, `theft`, `burglary`,
`suspicious_activity`, `welfare_check`, `disturbance`, `hazard`, `public_assistance`,
`other`. `other` represents substantive incident information with an unknown type.
For this application's agency policy, `welfare_check` is a police category.
Fire-service results never return it: patient complaints (including faintness) are
`medical`, practical nonmedical help is `public_assistance`, and an unspecified
incident type is `other`. The model schema excludes welfare checks when fire
context is supplied; a server check also enforces the rule for inferred fire context.

“Fire alarm” commonly identifies dispatch in phrases like “Fire alarm to all
companies” or “Engine 1 to Fire alarm”. Radio-address syntax is normalized to
“Dispatch” for classification. A location plus an MDT-message update, without a
stated incident type, is `other`. Actual activated alarms, alarm investigations,
and resets remain `alarm`; the dispatch identifier does not override a medical
complaint or reported structure fire.

`incidentStatus` is `dispatched`, `en_route`, `on_scene`, `cleared`, or null, based
on explicit statements. Classification and summaries are model interpretations.

Machine-readable JSON Schemas are in
[schemas/transmission-request.schema.json](schemas/transmission-request.schema.json)
and [schemas/transmission-response.schema.json](schemas/transmission-response.schema.json).
Swagger documents both the metadata object and empty response.
Text must be nonblank and at most 2,000 characters. Invalid input returns 400;
model unavailable, malformed output, and inference timeout return 503, 502, and 504.
Geocoding failures preserve metadata with `location.geocode: null`.

For a map, use `location.geocode.latitude` and `longitude`, then filter on `callType`
and `service`. Coordinates may use the configured default locality when the
transmission omits it. This endpoint does not create incident IDs, persist events,
or link successive transmissions. OpenScanner should attach feed ID and received
UTC timestamp and group related transmissions into incidents separately; an update
alone may not identify its original call type or location.

Verification: `dotnet build` then `python3 scripts/test_address_endpoint.py` exercises
mocked backend contracts and failure paths. With real Ollama and a running server,
run `python3 scripts/test_transmission_live.py http://127.0.0.1:8099` to check actual
chatter filtering, traffic-stop parsing, fire dispatch, and location-free medical calls.

For a downloaded OpenScanner support package, extract `openscanner_backup.db` to
an investigation directory and evaluate the manually labeled real-transmission sample:

```bash
python3 scripts/evaluate_transmissions.py /tmp/openscanner-transmissions.db \
  http://127.0.0.1:8099 /tmp/transmission-evaluation.json
```

The evaluator opens SQLite read-only. The label manifest in
`tests/transmission_support_cases.json` contains IDs and human expectations, with
22 development examples and 8 held-out examples. It requires the matching support
snapshot. Reports include original transcripts and should remain with the support
investigation. These small sample scores measure this snapshot, not production accuracy.
On the provided November 2026 support snapshot with `qwen3:4b`, the labeled sample
produced 29/30 expected classifications (21/22 development, 8/8 held out); all 30
responses matched the JSON Schema. One noninjury fall requesting assistance was
classified as `welfare_check` instead of the expected `public_assistance`. Keep this
case as a known mismatch, not an exact accuracy estimate for the full database.

To isolate classification from network availability, an evaluation-only server may
point its geocoding endpoints at an unavailable local port; location extraction is
still checked, while geocoding gracefully returns null. Never use receiver/channel
coordinates in the database as incident coordinates.

## Address extraction and automatic location lookup

`POST /address` accepts JSON text, extracts a location, and automatically resolves
its address and coordinates when possible. Extraction uses a separately
running [Ollama](https://ollama.com/) service. The installer now provisions local
Ollama and the configured LLM automatically. For an externally managed setup or
when using `--skip-ollama`, install/start Ollama separately and download the model:

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
  "crossStreet": null,
  "placeName": null,
  "geocode": null
}
```

The first address is extracted if several are mentioned. Intersections use `street`
and `crossStreet`; partial addresses are supported. Missing fields are null, and
no location produces an object with every field null, without making an external lookup.
`placeName` contains an extracted business or landmark name. `geocode` contains the
selected provider match (including `matchedAddress`, numeric `latitude` and
`longitude`) or null when no match can be selected. Geocoding outages, invalid
responses, timeouts, and ambiguous results do not discard the extracted address:
`/address` still returns HTTP 200 with `geocode: null`.

For street addresses, the server calls Census automatically. If Census has no
match, a full-address Photon search can resolve a unique OSM address/building.
It must return the exact house number, a normalized exact street name, and the
requested US city/state. Street-only search results and neighboring house numbers
are rejected; multiple distinct matching points remain unresolved. No business
name or Census coverage is required for a verified numbered OSM building.

For example, `3 Artisan Drive, Salem, NH` resolves through Photon to the OSM
apartment building at approximately 42.7715374, -71.2288257. The returned point
represents the building, not apartment 204 or a confirmed entrance. The original
extracted address and unit remain unchanged.

`geocode.precision` distinguishes `building` (OSM building with matching address),
`address` (an address point or Census address-range interpolation), `place`
(a business/landmark lookup), and `street` (an approximate mapped-street point). `geocode.provider` identifies the coordinate source.
These fields are additive and are documented in Swagger and the JSON Schema.
Building precision does not guarantee rooftop/entrance accuracy. Street-level points are never presented as numbered addresses.

For a transmission naming a street without a house number, lookup can return an
approximate street pin with `precision: "street"`, `houseNumber: null`, and Photon
attribution. It requires the exact normalized street name and requested US town/state;
similar names in other towns and invalid/missing points are rejected. Among available
segments of that named street, it selects a provider point near the center of their
combined mapped bounds. This is not a measured midpoint of the entire street or the
reported person's position. A partial cross street such as “Lake” is preserved as
text and is not treated as a verified intersection. The original location fields
remain unchanged. Numbered addresses continue to require an address/building match.
OpenScanner should label `precision: "street"` pins as approximate street locations.

If no full-address building candidates exist, street-spelling repair runs next.
When a house number and street are present, it searches Photon for similar street
names in the requested city/state. Street type and directional
prefixes must agree; name edits are bounded. If cross streets were extracted,
each must have a similar local street whose bounding box is near the candidate
street. This is proximity evidence, not proof that the roads intersect. The
unchanged house number is then re-geocoded by Census. A unique address matching
the number, candidate street, city and state is required. Multiple plausible
addresses, missing cross-street evidence, or failures leave `geocode: null`.

For example, `16 Shepherd Ave` with cross streets `Ivan Guile Road and Cole Street`
can resolve to `16 SHEPARD AVE, SALEM, NH, 03079`. The extracted `address`, `street`
and `crossStreet` stay as transcribed; the corrected provider address appears only
in `geocode`. Photon street centroids are never returned as house coordinates.
The fallback is used by automatic enrichment on `/address`, `/transmission`, and
`/transcribe` with metadata, rather than the standalone `/geocode` route.
`Geocoding:StreetEndpoint` defaults to `https://photon.komoot.io/api/`. Street
requests are serialized/rate limited, use the geocoding timeout, and cache results
for 24 hours by number, street, locality and cross streets. Additional requests
can add latency. Only extracted location information is sent to providers.

Location lookup rejects model-generated place names absent from the source text.
An explicit house number and street take precedence over a business lookup, and
selected coordinates must agree with that number and a compatible street name.
A conflicting provider/business result leaves coordinates unresolved and preserves
the extracted address. For example, `44 Millville Street` must never acquire the
local Dollar Tree directory entry at `294 N Broadway`.

For business names, lookup checks the configured local directory, then
Photon/OpenStreetMap. The original text is supplied only to the local LLM for
candidate selection. A successful business match fills address fields from the
provider/directory, never from the LLM's memory.

The configured default area is Salem, NH (`Geocoding:DefaultCity` and
`Geocoding:DefaultState`). Missing locality is added to lookup queries; explicit
extracted locality takes precedence. The extraction prompt distinguishes Route 28,
radio unit IDs and Massachusetts license plates from house numbers and location
states. Model behavior still needs evaluation against actual radio transcripts.
Automatic lookup can add the geocoding timeout and a second LLM call to latency.
Extracted queries over 500 characters are preserved but not sent for lookup.

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
when Ollama runs on another machine or address extraction is unused. The installer
sets `OLLAMA_NUM_PARALLEL=1` and `OLLAMA_MAX_LOADED_MODELS=1` for managed local
Ollama. For external setups, configure those values in the Ollama service environment
and restart Ollama. Test simultaneous transcription and address extraction with `nvidia-smi`
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
provider. `/address` now calls the same geocoding/selection service automatically.
Use `/geocode` for a standalone street-address lookup with optional context;
include city/state or ZIP explicitly on this standalone endpoint.

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


### Business names and local radio aliases

For example, submit the actual transcript to `/address`:

```json
{"text":"117, copy, stop. Go ahead. 28, Dollar Tree, Mass, 4, Paul, Henry, Edward, 93. Copy."}
```

A successful lookup returns `placeName: "Dollar Tree"`, the resolved street address,
and a `geocode` object. The repository includes an entry for 294 N Broadway,
Salem, NH 03079, verified against [Dollar Tree's store locator](https://locations.dollartree.com/nh/salem/294-n-broadway-suite-4).
The store is Suite 4; Census resolves the street address rather than a suite entrance.
Census returned latitude `42.7971735138`, longitude `-71.238059822759` during a
live verification. Coordinates are fetched at runtime rather than hard-coded.

Maintain `Geocoding:Places` for known local businesses and confirmed radio aliases:

```json
{
  "Name": "Example Business",
  "Aliases": ["confirmed radio nickname"],
  "Address": "123 Main Street, Salem, NH 03079",
  "HouseNumber": "123",
  "Street": "Main Street",
  "City": "Salem",
  "State": "NH",
  "PostalCode": "03079",
  "Source": "https://example.com/location"
}
```

This is a configuration example, not an actual verified business. Entries match
names/aliases exactly ignoring case and require matching city/state. Their street
addresses are geocoded with Census. Do not add speculative aliases for uncertain
transcripts such as “tailwind chasers” until the intended place is confirmed.

Names absent from the directory use [Photon](https://github.com/komoot/photon).
Results must match the requested town, state and US country code; even one fuzzy
candidate is checked by the LLM against the business name and transcript before
selection. Missing locality metadata is rejected rather than guessing. NH and
New Hampshire are treated as equivalent. Coverage is incomplete: the live Photon
query for Dollar Tree returned other NH towns, which is why the verified local
entry is useful. An unknown or uncertain name can remain unresolved.

`Geocoding:PlaceEndpoint` defaults to `https://photon.komoot.io/api/` and can point
to another Photon-compatible instance. The public demo permits reasonable use,
may throttle requests, and has no availability guarantee; use a private instance
for sustained/high-volume operation. The server caches up to 1,000 place queries
for 24 hours and spaces uncached calls at least one second apart per process.
Display the returned `geocode.attribution` with OpenStreetMap-derived results
([OpenStreetMap copyright](https://www.openstreetmap.org/copyright)). Local-directory
matches include their source URL in that field. No API key or additional GPU model
is required. The LLM is only used to extract names/select candidates, not to invent
coordinates or repair uncertain business names from memory.

Street fallback checks: `python3 scripts/test_street_fallback.py` uses local mock
providers to exercise locality, cross-street proximity, ambiguity, number
preservation, outages and caching. `python3 scripts/test_shepherd_live.py
http://127.0.0.1:8099` checks the actual cardiac-arrest transmission against Ollama,
Photon and Census; it requires those services to be available.

`python3 scripts/test_dispatch_identifier_live.py http://127.0.0.1:8099` checks
radio dispatch identifiers versus actual alarm/fire/medical calls, faintness,
fire-service category policy, and independent numbered street extraction using
real Ollama. Geocoding may be unavailable for these classification checks.

`python3 scripts/test_artisan_live.py http://127.0.0.1:8099` verifies the original
Artisan Drive medical dispatch with real Ollama, Census and Photon. The mocked
street fallback suite also covers exact building matches, ambiguity, duplicates,
wrong numbers/streets/localities, street-centroid rejection and provider outages.

`python3 scripts/test_street_center_live.py http://127.0.0.1:8099` verifies the
Bluff Street police transmission with a real approximate street pin. The mocked
fallback suite includes street-center locality isolation, multiple segments,
exact-name matching, missing/invalid points, and cache reuse; its center checks
can also run with `--centers-only`.
