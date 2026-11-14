# OpenScanner.AIServer

A .NET Web API server that provides the AI features for [OpenScanner](https://github.com/briannippert/OpenScanner). Run it on a more powerful machine to offload this work from your Raspberry Pi:

- **Transcription:** Whisper speech-to-text for radio transmissions
- **Incident metadata:** call type, service, status and a summary, extracted from each transmission by a local LLM (Ollama)
- **Location lookup:** addresses, intersections and businesses, geocoded through the US Census and OpenStreetMap

Unlike a per-clip `whisper-cli` invocation, this server keeps models **resident in memory** by managing a pool of long-lived [`whisper-server`](https://github.com/ggerganov/whisper.cpp/tree/master/examples/server) processes. The first request for a model loads it; later requests reuse the warm process, and multiple clips can be transcribed concurrently.

> Formerly **OpenScanner.WhisperServer**. Existing installs are migrated automatically: rerunning
> `./scripts/install_service.sh` replaces the old `openscanner-whisper` service with `openscanner-ai`.

## Contents

- [Quick start](#quick-start)
- [Connecting to OpenScanner](#connecting-to-openscanner)
- [Installer reference](#installer-reference)
- [Manual setup](#manual-setup)
- [Configuration](#configuration)
- [API](#api)
- [How location lookup works](#how-location-lookup-works)
- [GPU memory: sharing a card between Whisper and the LLM](#gpu-memory-sharing-a-card-between-whisper-and-the-llm)
- [Development and testing](#development-and-testing)
- [License](#license)

## Quick start

You need a Linux machine (Ubuntu/Debian recommended) with a GPU or a fast CPU. The installer handles everything else: .NET, whisper.cpp, Ollama, and both model downloads.

```bash
git clone https://github.com/briannippert/OpenScanner.AIServer.git
cd OpenScanner.AIServer

chmod +x scripts/*.sh
./scripts/install_service.sh
```

The installer will:

1. Install system dependencies (.NET 10 SDK, cmake, ffmpeg)
2. Prompt for a default Whisper model (pre-warmed at startup) and a port (default `8090`)
3. Clone and build whisper.cpp, including `whisper-server`, with CUDA if an NVIDIA GPU and the CUDA toolkit are detected
4. Install and start Ollama, and prompt for the metadata/address LLM (default `qwen3:4b`)
5. Download missing models, then verify that Whisper loads and the LLM produces structured JSON
6. Build the .NET server and start it as the `openscanner-ai` systemd service

Once it is running:

```bash
systemctl status openscanner-ai    # check status
journalctl -u openscanner-ai -f    # follow logs
```

To update, rerun `./scripts/install_service.sh`; it pulls the latest code, rebuilds, and restarts the service. To remove the service, run `./scripts/uninstall_service.sh`. Uninstalling leaves Ollama and its model cache in place, since other apps may use them.

## Connecting to OpenScanner

1. In the OpenScanner UI, go to **Settings → Transcription** and enable **AI Transcription**.
2. Set the **Backend** to **Remote**.
3. Enter the server URL (for example `http://192.168.1.100:8090`) and click **Test**.
4. Pick a **Model** from the list, which is queried live from this server.

## Installer reference

| Option | Effect |
|---|---|
| `--skip-update` | Build the local checkout without pulling `main`. Use this when installing a feature branch. |
| `--deps-only` | Set up dependencies, Ollama, and both models, then exit without building or deploying the application. A service that was running beforehand is restored. |
| `--port=NNNN` | Set the listening port and skip the prompt. |
| `--rebuild-whisper` | Force a clean rebuild of whisper.cpp, for example after installing the CUDA toolkit. |
| `--llm-model=NAME` | Select the Ollama model and skip the prompt. `--ollama-model` is an alias. |
| `--ollama-url=URL` | Override the Ollama origin. Otherwise `Address:OllamaUrl` is used. |
| `--upgrade-ollama` | Force an upgrade of the managed local Ollama using the official installer. |
| `--skip-ollama` | Leave Ollama and the `Address` settings untouched. Cannot be combined with the model, URL, or upgrade options. |
| `--help` | Show all options. Does not need sudo. |

For example, to install the current checkout on port 8099 without prompts:

```bash
bash scripts/install_service.sh --skip-update --port=8099 --llm-model=qwen3:4b
```

### LLM model selection

The prompt offers `qwen3:4b`, `qwen3:1.7b`, or a custom model; pressing Enter keeps the configured `Address:Model`. A model is downloaded only if it is missing. The installer then test-loads it with the same settings the API uses (4,096-token context, thinking disabled, JSON Schema output) and unloads it so Whisper startup can use the GPU. Model and URL settings are saved only after this verification succeeds.

### Local Ollama

When `Address:OllamaUrl` is a local HTTP loopback address with an explicit port (1024–65535), the installer manages Ollama for you:

- Installs Ollama if its CLI or service is missing, then starts and enables `ollama.service`.
- Writes `/etc/systemd/system/ollama.service.d/openscanner.conf`, which binds Ollama to loopback and sets `OLLAMA_NUM_PARALLEL=1` and `OLLAMA_MAX_LOADED_MODELS=1`. Other drop-ins are preserved.
- Keeps the installed Ollama version unless you pass `--upgrade-ollama` or a model download reports that a newer version is required. In that case the official [Linux installer](https://docs.ollama.com/linux) runs once and setup retries.

### Remote Ollama

For a remote URL or an HTTPS reverse proxy, the installer only downloads the model and runs the API verification against that endpoint. It does not change that host's installation or service, and upgrades must be done there. URLs must be HTTP(S) origins without credentials, paths, query strings, or fragments.

### If a step fails

The installer tries to restart a service that was running before it started. This restores availability; it is not a transactional rollback of dependencies or configuration that were already installed.

## Manual setup

To build whisper.cpp yourself instead of using the installer:

```bash
git clone https://github.com/ggerganov/whisper.cpp.git
cd whisper.cpp

cmake -B build                  # CPU-only
# cmake -B build -DGGML_CUDA=ON # or with CUDA
cmake --build build --config Release   # produces build/bin/whisper-server

bash models/download-ggml-model.sh large-v3-turbo-q5_0
```

For the LLM, install and start [Ollama](https://ollama.com/) and pull the model:

```bash
ollama pull qwen3:4b
```

Keep Ollama bound to loopback, and set `OLLAMA_NUM_PARALLEL=1` and `OLLAMA_MAX_LOADED_MODELS=1` in its service environment.

Then point `appsettings.json` at your paths (see [Configuration](#configuration)) and run the server:

```bash
dotnet run --urls "http://0.0.0.0:8090"
```

Without `--urls`, the development default is port 5000.

## Configuration

Settings live in `appsettings.json`; the installer writes the Whisper and LLM values for you. Any setting can also be overridden with an environment variable that uses double underscores, for example `Address__Model=qwen3:1.7b` or `Geocoding__TimeoutSeconds=60`.

### `Whisper`

| Key | Default | Purpose |
|---|---|---|
| `ModelsDir` | — | Directory of `ggml-*.bin` models. Also drives `/models`. |
| `WhisperServerBinary` | — | Path to the built `whisper-server` binary. |
| `DefaultModel` | `large-v3-turbo-q5_0` | Model pre-warmed at startup and used when a request omits `model`. |
| `BasePort` | `8100` | First loopback port for spawned `whisper-server` processes. |
| `InstancesPerModel` | `1` | Processes per model, which sets the concurrency for a single model. |
| `MaxResidentModels` | `1` | Distinct models kept in memory. Beyond this the least recently used model is evicted, after any in-flight requests finish. |
| `Threads` | `0` | Threads per process (`0` = all cores). |
| `UseGpu` | `true` | Set to `false` to disable the GPU. |
| `StartupTimeoutSeconds` | `120` | How long to wait for a model to load. |
| `TimeoutSeconds` | `120` | Per-inference request timeout. |
| `DefaultPrompt` | radio prompt | Decoding bias used when a request omits `prompt`. |

Ports `BasePort` … `BasePort + InstancesPerModel × MaxResidentModels` are used internally and bound only to `127.0.0.1`. Only the .NET server's own port is exposed on the network.

If a `whisper-server` process exits unexpectedly, the next request for that model replaces it automatically. A model load that is in progress continues even if the requesting client disconnects.

### `Address` (LLM)

| Key | Default | Purpose |
|---|---|---|
| `OllamaUrl` | `http://127.0.0.1:11434/` | Ollama origin. |
| `Model` | `qwen3:4b` | Model used for metadata, address extraction, and candidate selection. |
| `TimeoutSeconds` | `120` | Bounds each model call once it starts. |
| `QueueTimeoutSeconds` | `300` | Bounds the wait behind earlier calls, so a burst of transmissions does not time out while queued. |
| `ReservedVramMb` | `3500` | Installer-only estimate of LLM VRAM, subtracted before sizing the Whisper pool. See [GPU memory](#gpu-memory-sharing-a-card-between-whisper-and-the-llm). |

Model calls run one at a time, with a 4,096-token context, a 512-token output limit, and Qwen3 thinking disabled. Output is constrained by a JSON Schema and validated again by the server. The model stays warm for five minutes after use.

### `Geocoding`

| Key | Default | Purpose |
|---|---|---|
| `Endpoint` | US Census one-line address endpoint | Census-compatible geocoder. |
| `Benchmark` | `Public_AR_Current` | Census dataset. |
| `TimeoutSeconds` | `30` | Total queue and request timeout for a provider lookup. |
| `DefaultCity` / `DefaultState` | `Salem` / `NH` | Locality added to lookups when a transmission does not state one. |
| `PlaceEndpoint` | `https://photon.komoot.io/api/` | Photon instance for business and landmark lookups. |
| `StreetEndpoint` | `https://photon.komoot.io/api/` | Photon instance for street and building fallbacks. |
| `Places` | one example entry | Local directory of known businesses and radio aliases. See [Business names](#business-names). |

## API

| Endpoint | Purpose |
|---|---|
| [`GET /health`](#get-health) | Server status, loaded models, and hardware |
| [`GET /models`](#get-models) | Installed Whisper models |
| [`POST /transcribe`](#post-transcribe) | Transcribe a WAV file, optionally with incident metadata |
| [`POST /transmission`](#post-transmission) | Extract incident metadata and location from a transcript |
| [`POST /address`](#post-address) | Extract and geocode a location from a transcript |
| [`POST /geocode`](#post-geocode) | Standalone US street-address lookup |

Interactive documentation is at `/swagger` (for example `http://localhost:8090/swagger`), and the OpenAPI document is at `/swagger/v1/swagger.json`. Both are enabled in development and production.

Errors return an `{"error": ...}` body with these status codes:

| Status | Meaning |
|---|---|
| 400 | Invalid request, or an unknown or uninstalled `model` |
| 502 | `whisper-server` failed, or the model or provider returned malformed output |
| 503 | A backend (Whisper, Ollama, or a geocoding provider) is unavailable or not configured |
| 504 | Inference, queue, or geocoding timeout |

The LLM endpoints work independently of Whisper readiness.

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

Transcribes an audio file sent as multipart form data. OpenScanner sends a pre-filtered 16 kHz mono WAV.

```bash
curl -X POST http://localhost:8090/transcribe \
  -F "file=@recording.wav" \
  -F "model=small.en" \
  -F "prompt=Dispatch, Unit 1, 10-4, copy, over."
```

| Field | Required | Purpose |
|---|---|---|
| `file` | yes | WAV audio file |
| `model` | no | Model id from `/models`. Defaults to `DefaultModel`. |
| `prompt` | no | Context prompt to guide accuracy. Defaults to `DefaultPrompt`. |
| `includeMetadata` | no | `true` to also return incident metadata |
| `service` | no | `police` or `fire` context for metadata. Does not change the audio decoding prompt. |
| `defaultCity`, `defaultState` | no | Locality for this lookup. See [Default locality](#default-locality). |

By default the response is only the transcript. `{"text": ""}` means the audio was blank or silent.

```json
{ "text": "Unit 1, responding code 3 to Main and First." }
```

With `includeMetadata=true`, the response adds `metadata` and `metadataError`:

```bash
curl http://localhost:8090/transcribe \
  -F 'file=@dispatch.wav' \
  -F 'includeMetadata=true' \
  -F 'service=fire' \
  -F 'defaultCity=Salem' \
  -F 'defaultState=NH'
```

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

`metadata` is the same object that `/transmission` returns, or `{}` for routine chatter or silence. If extraction fails, or the transcript exceeds 2,000 characters, the endpoint still returns HTTP 200 with the full transcript, `metadata: null`, and the reason in `metadataError`. Audio and transcription failures return the usual error statuses. Metadata adds LLM and geocoding latency to the request.

### POST /transmission

Parses one transmission into incident metadata and a location.

```bash
curl http://localhost:8090/transmission \
  -H 'Content-Type: application/json' \
  -d '{"text":"207, Ill stop. 207. 42, Kelly Road, Mass, 3, Victor, Yankee Yankee, 6-2. Copy.","service":"police","defaultCity":"Salem","defaultState":"NH"}'
```

| Field | Required | Purpose |
|---|---|---|
| `text` | yes | Transcript. Nonblank, at most 2,000 characters. |
| `service` | no | `police` or `fire`, case insensitive. The source channel. |
| `defaultCity`, `defaultState` | no | Locality for this lookup. See [Default locality](#default-locality). |

Example response (coordinates omitted):

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

Response behavior:

- Routine chatter such as "copy", "I'm out", and "roger" returns HTTP 200 with `{}`, and no geocoder is called.
- Missing fields are null. A substantive call can have metadata without a location, such as a medical complaint.
- The model classifies the first incident mentioned. Classifications and summaries are model interpretations.
- `incidentStatus` is `dispatched`, `en_route`, `on_scene`, `cleared`, or null, based on explicit statements.
- `location` has the same shape as the [`/address`](#post-address) response. A geocoding failure keeps the metadata and returns `location.geocode: null`.

Machine-readable JSON Schemas are in [schemas/transmission-request.schema.json](schemas/transmission-request.schema.json) and [schemas/transmission-response.schema.json](schemas/transmission-response.schema.json).

#### Call types and channels

A supplied `service` is the source channel and is authoritative: it sets the returned `service` and limits which call types can be returned. Without it, the model infers the service only when it is clear, and the inferred service's allowlist still applies.

| Channel | Allowed call types |
|---|---|
| Police | `traffic_stop`, `traffic_collision`, `security_alarm`, `assault`, `domestic_disturbance`, `theft`, `burglary`, `suspicious_activity`, `welfare_check`, `disturbance`, `hazard`, `public_assistance`, `other` |
| Fire | `traffic_collision`, `fire`, `medical`, `rescue`, `alarm`, `hazard`, `public_assistance`, `other` |

- `other` is substantive incident information of an unknown type, or a type that is not available on its channel. The summary and location are preserved.
- `security_alarm` is a police security, burglar, or residential motion alarm, not a confirmed burglary. `alarm` is reserved for fire-service alarms.
- The server enforces these allowlists even if the model violates its output schema. A model response of `alarm` on police maps to `security_alarm`, `welfare_check` on fire maps to `public_assistance`, and any other invalid combination maps to `other`.
- These restrictions implement the application's channel taxonomy. They are not a claim about which agencies respond in reality.

Classification rules worth knowing:

- **"Fire alarm" as a dispatch identifier.** Phrases like "Fire alarm to all companies" or "Engine 1 to Fire alarm" name the dispatcher, so radio-address syntax is normalized to "Dispatch" before classification. Activated alarms, alarm investigations, and resets remain `alarm`, and the identifier never overrides a medical complaint or reported structure fire.
- **Police shorthand traffic stops.** A terse business or location followed by a registration state and plate is a traffic stop even without the word "stop", unless an explicit alarm, theft, or other incident is stated. Radio unit IDs and "in the area" do not establish a stop.
- **Notifications.** A crash or accident notification is a substantive `other` update. Its stated address is the notification destination, not automatically a crash scene. A location plus an MDT-message update with no stated incident type is also `other`.

#### Using the results in OpenScanner

For a map, use `location.geocode.latitude` and `longitude`, then filter on `callType` and `service`. Coordinates may rely on the default locality when the transmission omits one, and `precision: "street"` pins should be labeled as approximate.

This endpoint does not create incident IDs, persist events, or link successive transmissions. OpenScanner should attach the feed ID and received UTC timestamp and group related transmissions into incidents itself; an update alone may not identify its original call type or location.

### POST /address

Extracts a location from transcript text and resolves its address and coordinates when possible.

```bash
curl http://localhost:8090/address \
  -H 'Content-Type: application/json' \
  -d '{"text":"Dispatch, respond to 123 Main Street, apartment 4, Springfield."}'
```

| Field | Required | Purpose |
|---|---|---|
| `text` | yes | Transcript. Nonblank, at most 2,000 characters. |
| `defaultCity`, `defaultState` | no | Locality for this lookup. See [Default locality](#default-locality). |

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

- If several addresses are mentioned, the first is extracted. Partial addresses are supported.
- Intersections use `street` and `crossStreet`. `placeName` holds an extracted business or landmark name.
- Missing fields are null. When no location is found, every field is null and no external lookup is made.
- `geocode` holds the selected provider match, or null when none can be selected. Geocoding outages, invalid responses, timeouts, and ambiguous results never discard the extracted address: the endpoint still returns HTTP 200 with `geocode: null`.
- The extracted fields always stay as transcribed. Corrected or completed spellings appear only inside `geocode`.

The `geocode` object:

| Field | Meaning |
|---|---|
| `matchedAddress` | The provider's canonical address |
| `latitude`, `longitude` | Numeric coordinates from the provider, never from the LLM |
| `provider` | The coordinate source |
| `precision` | `building`, `address`, `place`, or `street` (see below) |
| `attribution` | Attribution to display with OpenStreetMap-derived results ([OpenStreetMap copyright](https://www.openstreetmap.org/copyright)), or the source URL for a local-directory match |

| Precision | Meaning |
|---|---|
| `building` | An OSM building with a matching address. Not a guaranteed rooftop or entrance position. |
| `address` | An address point, or a Census address-range interpolation |
| `place` | A business or landmark lookup |
| `street` | An approximate point on a named street. Never presented as a numbered address. |

[How location lookup works](#how-location-lookup-works) describes how a match is chosen.

### POST /geocode

A standalone street-address lookup against the free [US Census geocoder](https://geocoding.geo.census.gov/geocoder/Geocoding_Services_API.html). It needs no API key or GPU. Unlike the automatic lookup on the other endpoints, it applies no default locality and no fallbacks, so include the city and state or ZIP in the address.

```bash
curl http://localhost:8090/geocode \
  -H 'Content-Type: application/json' \
  -d '{"address":"4600 Silver Hill Rd, Washington, DC 20233"}'
```

| Field | Required | Purpose |
|---|---|---|
| `address` | yes | Nonblank, at most 500 characters. Sent to Census. |
| `context` | no | At most 2,000 characters, such as the original transcript. Used by the local LLM to choose between candidates; never sent to Census. |

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

All candidates remain in `matches`. `selectedMatchIndex` is zero-based, and `selectedMatch` is always a provider result.

| `selectionStatus` | Meaning |
|---|---|
| `no_match` | HTTP 200 with `found: false`, `matches: []`, and `selectedMatch: null` |
| `single_match` | The only candidate was selected directly |
| `llm_selected` | The local LLM chose among several candidates using the address and `context` |
| `ambiguous` | Insufficient evidence to choose. Selected index and match are null. |
| `llm_unavailable` | Model failure, timeout, or invalid output. Selected index and match are null. |

Notes:

- A selection is a model suggestion, not a verified location. Candidate lists over 20 entries, or selection prompts over 6,000 characters, stay `ambiguous` to avoid truncation.
- Ranking uses the same model and inference queue as the other LLM endpoints. A ranking failure still returns the successful geocoding response, with `llm_unavailable`.
- Census estimates points along street address ranges; these are not guaranteed rooftop positions. It requires numbered street addresses, so bare intersections, landmarks, and incomplete transcripts may not resolve.

## How location lookup works

`/address`, `/transmission`, and `/transcribe` with metadata all enrich an extracted location the same way. The rules below exist so that a pin is either backed by a provider or absent: the LLM extracts names and selects among candidates, but it never supplies coordinates or address details from memory.

### Default locality

When a transmission does not state a city or state, one is added to the lookup query. Precedence is:

1. The city and state extracted from the transmission
2. The request's `defaultCity` and `defaultState`
3. `Geocoding:DefaultCity` and `Geocoding:DefaultState`

Defaults apply only to the lookup. They are never reported as spoken location fields. Blank request values fall back to the server settings; nonblank values are limited to 100 characters with no commas or control characters.

The extraction prompt separates route numbers, radio unit IDs, and registration states from house numbers and incident locality. For example, "28 Taco Bell, California, 677-51-John-4" identifies Taco Bell and a California plate, not a California incident or an address of 28 Taco Bell.

### Numbered street addresses

A numbered address is tried against these sources in order, stopping at the first verified match:

1. **Census.** A provider match may differ from the transcript in two bounded ways, and the house number may never change:
   - *Completing a missing street type.* `16 Garabedian` can resolve to `16 GARABEDIAN DR, SALEM, NH, 03079`. A type that was spoken cannot be replaced, so Street never becomes Drive.
   - *Correcting a misheard name.* `8 Graymoor Woods` can resolve to `8 BRAEMOOR WOODS RD, SALEM, NH`. This allows at most two name edits (fewer for short names) with the same number, name-token count, and directional prefix, and it requires a unique candidate in the requested city and state.
2. **OSM building (Photon).** If Census has no match, a full-address search can resolve a unique OSM address or building. It must return the exact house number, the exact normalized street name, and the requested US city and state. Street-only results and neighboring house numbers are rejected, and multiple distinct matching points stay unresolved. For example, `3 Artisan Drive, Salem, NH` resolves to the apartment building; the point represents the building, not a specific apartment or entrance.
3. **Street-spelling repair.** If no building candidate exists, Photon is searched for similarly named streets in the requested city and state. Street type and directional prefix must agree and name edits are bounded. If cross streets were extracted, each must have a similar local street whose bounding box is near the candidate; this is proximity evidence, not proof of an intersection. The unchanged house number is then geocoded again by Census, and a unique match is required. For example, `16 Shepherd Ave` with cross streets `Ivan Guile Road and Cole Street` can resolve to `16 SHEPARD AVE, SALEM, NH, 03079`.

Multiple plausible addresses, missing cross-street evidence, or provider failures leave `geocode: null`. A Photon street centroid is never returned as a house coordinate.

### Streets without a house number

A transmission that names only a street can return an approximate pin with `precision: "street"` and `houseNumber: null`. It requires the exact normalized street name in the requested US town and state; similar names in other towns are rejected. The point is chosen near the center of the combined bounds of that street's mapped segments. This is not a measured midpoint of the street, nor the position of whoever was reported. A partial cross street such as "Lake" is kept as text and is not treated as a verified intersection.

### Business names

A place name is used only if it appears in the source text; model-generated names are rejected. An explicit house number and street always take precedence over a business lookup, and a business result that conflicts with them leaves the coordinates unresolved. Bare numbers before a business name do not establish a house number.

Lookups check the local directory first, then [Photon](https://github.com/komoot/photon):

- **Local directory (`Geocoding:Places`).** Entries match a name or alias exactly, ignoring case, and require a matching city and state. Their street addresses are geocoded with Census at runtime rather than hard-coded.
- **Photon.** Results must match the requested town, state, and US country code, and results with missing locality metadata are rejected (`NH` and `New Hampshire` are equivalent). Even a single fuzzy candidate is checked by the LLM against the business name and transcript before it is selected.

A successful match fills the address fields from the directory or provider. An unknown or uncertain name stays unresolved.

Photon's coverage is incomplete, which is why the directory is useful: a live query for the Salem, NH Dollar Tree returned only stores in other towns, so the repository ships a directory entry for it, verified against the [store locator](https://locations.dollartree.com/nh/salem/294-n-broadway-suite-4). Add your own known businesses and confirmed radio aliases in the same form:

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

Do not add speculative aliases for uncertain transcripts until the intended place is confirmed.

### Providers, limits, and privacy

- **What leaves the machine.** Only the extracted location is sent to Census and Photon. The transcript is supplied only to the local Ollama model.
- **Rate limiting.** Calls to each provider are serialized and spaced at least one second apart per server process.
- **Caching.** Up to 1,000 query results per provider, including no-match results, are cached in memory for 24 hours. A restart clears the cache. Errors are not cached or retried automatically, and cached candidates are ranked afresh for each context.
- **Latency.** Automatic lookup can add the geocoding timeout, a second LLM call, and extra fallback requests to a response.
- **Query size.** Extracted queries over 500 characters are preserved in the response but not sent for lookup.
- **Photon.** The public instance at `photon.komoot.io` permits reasonable use, may throttle, and has no availability guarantee. Point `Geocoding:PlaceEndpoint` and `Geocoding:StreetEndpoint` at a private instance for sustained or high-volume use.
- **No map tiles.** This server returns coordinates only.

## GPU memory: sharing a card between Whisper and the LLM

These notes use an 8 GB GTX 1070 as the reference card. The numbers are estimates, not guaranteed fits.

- Whisper uses about 4.1 GB, which makes a 4B model at Q4 a practical ceiling for fully GPU-resident inference. [Qwen3 4B Q4_K_M](https://ollama.com/library/qwen3:4b) is a 2.5 GB file, plus runtime buffers and the KV cache.
- A 7–8B Q4 model generally exceeds the remaining budget. CPU offloading allows larger models at lower speed, depending on system RAM.
- Keep `Whisper:InstancesPerModel` and `Whisper:MaxResidentModels` at `1` (the defaults) so extra Whisper copies do not consume the remaining VRAM. This trades transcription concurrency and model caching for memory.
- The installer subtracts `Address:ReservedVramMb` (default 3500 MB) and driver headroom before sizing the Whisper pool. This does not reserve memory at runtime. Set it to `0` when Ollama runs on another machine.
- If memory is still too tight, pull `qwen3:1.7b` and set `Address:Model` to it.

To check a setup, run transcription and address extraction at the same time while watching `nvidia-smi`, and use `ollama ps` to confirm GPU placement.

[Ollama's hardware documentation](https://docs.ollama.com/gpu) lists the GTX 1070 as compute capability 6.1 and currently requires NVIDIA driver 570 or newer for that generation. This project does not install or update drivers.

If you install the CUDA toolkit after the initial setup, rerun the installer with `--rebuild-whisper` to recompile whisper.cpp with GPU support.

## Development and testing

### Offline checks

These need only .NET, Python 3, and loopback sockets. They run against mock Whisper, Ollama, and geocoding services, so they do not download a model, need a GPU, or touch real services, `/etc`, or packages. GitHub Actions runs the same checks on every pull request and on pushes to `main` ([.github/workflows/ci.yml](.github/workflows/ci.yml)).

```bash
dotnet build
for f in scripts/*.sh; do bash -n "$f"; done
python3 scripts/test_address_endpoint.py    # endpoint contracts, failure paths, Swagger, geocoding, caching
python3 scripts/test_street_fallback.py     # building, street-repair and street-center fallbacks
python3 scripts/test_ollama_setup.py        # installer's Ollama provisioning
```

`test_street_fallback.py --centers-only` runs just the street-center checks.

### Live checks

These run real transmissions against a running server with the real Ollama model, and most also need Census and Photon to be reachable. Each takes the server URL as an optional argument, defaulting to `http://127.0.0.1:8099`.

| Script | Checks |
|---|---|
| `test_transmission_live.py` | Chatter filtering, traffic-stop parsing, fire dispatch, location-free medical calls |
| `test_address_dispatch_live.py` | Dispatch address extraction with automatic geocoding |
| `test_channel_types_live.py` | Police and fire call-type allowlists and source-channel precedence |
| `test_dispatch_identifier_live.py` | "Fire alarm" as a dispatch identifier versus actual alarm, fire, and medical calls |
| `test_traffic_stop_shorthand_live.py` | Police shorthand traffic stops (business, registration state, plate) |
| `test_partial_address_live.py` | Addresses with no spoken street type, and crash notifications |
| `test_graymoor_live.py` | Correction of a misheard street name |
| `test_shepherd_live.py` | Street-spelling repair using cross streets |
| `test_artisan_live.py` | OSM building match when Census has no address |
| `test_street_center_live.py` | Approximate pin for a street with no house number |

```bash
python3 scripts/test_transmission_live.py http://127.0.0.1:8090
```

### Evaluating against real transmissions

`scripts/evaluate_transmissions.py` scores classification against a manually labeled sample from an OpenScanner support package. Extract `openscanner_backup.db` from the package, then run:

```bash
python3 scripts/evaluate_transmissions.py /tmp/openscanner-transmissions.db \
  http://127.0.0.1:8090 /tmp/transmission-evaluation.json
```

- The labels in `tests/transmission_support_cases.json` hold transmission IDs and human expectations (22 development and 8 held-out examples), so the matching support snapshot is required.
- The database is opened read-only. Reports include original transcripts and should stay with the support investigation.
- On the November 2026 snapshot with `qwen3:4b`, 29 of 30 classifications matched (21/22 development, 8/8 held out) and all 30 responses matched the JSON Schema. The known mismatch is a non-injury fall requesting assistance, classified as `welfare_check` instead of `public_assistance`. This measures one small sample, not production accuracy.
- To isolate classification from network availability, point the geocoding endpoints of an evaluation-only server at an unused local port. Location extraction is still checked, and geocoding returns null.
- Receiver and channel coordinates in the database are not incident coordinates and must not be used as such.

## License

MIT
