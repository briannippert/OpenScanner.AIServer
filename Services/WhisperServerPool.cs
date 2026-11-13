using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenScanner.WhisperServer.Services;

/// <summary>
/// Manages a set of long-lived <c>whisper-server</c> child processes so models stay
/// resident in memory between requests (no per-clip reload) and multiple clips can be
/// transcribed concurrently.
///
/// whisper-server serializes all inference behind a single mutex, so one process == one
/// model, one-at-a-time. To get concurrency and multi-model support we run a small pool
/// of processes per model (each on its own loopback port) and route requests to them.
/// </summary>
public sealed class WhisperServerPool : IHostedService, IDisposable
{
    private readonly ILogger<WhisperServerPool> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    private readonly ConcurrentDictionary<string, ModelPool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _portLock = new();
    private readonly HashSet<int> _usedPorts = new();
    private readonly SemaphoreSlim _evictLock = new(1, 1);
    private readonly Lazy<HardwareInfo> _hardwareInfo;
    private readonly CancellationTokenSource _stopping = new();
    private volatile bool _disposed;

    public WhisperServerPool(ILogger<WhisperServerPool> logger, IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _hardwareInfo = new Lazy<HardwareInfo>(DetectHardware);
    }

    // ---- Config accessors -------------------------------------------------

    public string ModelsDir => _config["Whisper:ModelsDir"] ?? "/usr/local/share/whisper/models";
    public string WhisperServerBinary => _config["Whisper:WhisperServerBinary"] ?? "/usr/local/bin/whisper-server";
    public string DefaultModel => _config["Whisper:DefaultModel"] ?? "small.en";
    public string DefaultPrompt => _config["Whisper:DefaultPrompt"] ?? "";
    private int BasePort => _config.GetValue<int?>("Whisper:BasePort") ?? 8100;
    private int InstancesPerModel => Math.Max(1, _config.GetValue<int?>("Whisper:InstancesPerModel") ?? 1);
    private int MaxResidentModels => Math.Max(1, _config.GetValue<int?>("Whisper:MaxResidentModels") ?? 1);
    private int Threads
    {
        get
        {
            var t = _config.GetValue<int?>("Whisper:Threads") ?? 0;
            return t > 0 ? t : Math.Max(1, Environment.ProcessorCount); // 0 => all cores
        }
    }
    private bool UseGpu => _config.GetValue<bool?>("Whisper:UseGpu") ?? true;
    private int StartupTimeoutMs => (_config.GetValue<int?>("Whisper:StartupTimeoutSeconds") ?? 120) * 1000;
    private int InferenceTimeoutMs => (_config.GetValue<int?>("Whisper:TimeoutSeconds") ?? 120) * 1000;

    public HardwareInfo Hardware => _hardwareInfo.Value;

    // ggml model names are simple tokens (e.g. "large-v3-turbo-q5_0", "small.en").
    public static bool IsValidModelName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, "^[A-Za-z0-9._-]+$");

    public string ModelPath(string model) => Path.Combine(ModelsDir, $"ggml-{model}.bin");

    public bool BinaryExists => File.Exists(WhisperServerBinary);
    public bool DefaultModelExists => File.Exists(ModelPath(DefaultModel));

    /// <summary>Ready once the binary and the default model file are present.</summary>
    public bool IsReady() => BinaryExists && DefaultModelExists;

    /// <summary>Enumerate installed ggml models (filenames like <c>ggml-small.en.bin</c>).</summary>
    public IReadOnlyList<string> GetAvailableModels()
    {
        try
        {
            if (!Directory.Exists(ModelsDir)) return Array.Empty<string>();
            return Directory.EnumerateFiles(ModelsDir, "ggml-*.bin")
                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                .Select(n => n.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ? n.Substring(5) : n)
                // whisper-server can't serve the tiny "for-tests-*" fixtures.
                .Where(id => !id.StartsWith("for-tests", StringComparison.OrdinalIgnoreCase))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate models in {Dir}", ModelsDir);
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> LoadedModels() => _pools.Values.Where(p => p.Ready).Select(p => p.Model).ToList();

    // ---- Lifecycle --------------------------------------------------------

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!BinaryExists)
        {
            _logger.LogError("whisper-server binary not found at {Path}; transcription will fail until configured.", WhisperServerBinary);
            return;
        }
        // Pre-warm the default model so the first transmission isn't blocked on a cold load.
        if (DefaultModelExists)
        {
            try
            {
                var instance = await AcquireAsync(DefaultModel, cancellationToken);
                await ReleaseAsync(instance);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to pre-warm default model '{Model}'", DefaultModel); }
        }
        else
        {
            _logger.LogWarning("Default model file not found at {Path}", ModelPath(DefaultModel));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    // ---- Transcription ----------------------------------------------------

    /// <summary>
    /// Transcribes a WAV file. Returns null only for genuinely empty/blank audio.
    /// Throws <see cref="WhisperModelException"/> for an invalid or uninstalled model and
    /// <see cref="WhisperInferenceException"/> when whisper-server fails, so callers can
    /// distinguish a failure from silence.
    /// </summary>
    public async Task<string?> TranscribeAsync(string wavPath, string? model, string? prompt, CancellationToken ct = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!;
        if (!IsValidModelName(modelName))
            throw new WhisperModelException($"Invalid model name '{SafeForLog(modelName)}'.");
        if (!File.Exists(ModelPath(modelName)))
            throw new WhisperModelException($"Model '{modelName}' is not installed.");

        var instance = await AcquireAsync(modelName, ct);
        try
        {
            using var content = new MultipartFormDataContent();
            await using var fs = File.OpenRead(wavPath);
            var fileContent = new StreamContent(fs);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", Path.GetFileName(wavPath));
            content.Add(new StringContent("json"), "response_format");
            content.Add(new StringContent("en"), "language");
            content.Add(new StringContent("true"), "no_timestamps");
            content.Add(new StringContent("0.0"), "temperature");
            var effectivePrompt = string.IsNullOrEmpty(prompt) ? DefaultPrompt : prompt;
            if (!string.IsNullOrEmpty(effectivePrompt))
                content.Add(new StringContent(effectivePrompt), "prompt");

            var client = _httpClientFactory.CreateClient("whisper");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(InferenceTimeoutMs);

            HttpResponseMessage resp;
            try
            {
                resp = await client.PostAsync($"http://127.0.0.1:{instance.Port}/inference", content, timeoutCts.Token);
            }
            catch (HttpRequestException ex)
            {
                // Usually the child died mid-request; the next acquire will replace it.
                throw new WhisperInferenceException($"whisper-server for '{modelName}' is unreachable: {ex.Message}", ex);
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                    _logger.LogError("Inference failed on {Model} (HTTP {Code}): {Body}", SafeForLog(modelName), (int)resp.StatusCode, body);
                    throw new WhisperInferenceException($"whisper-server returned HTTP {(int)resp.StatusCode}.");
                }

                JsonElement json;
                try { json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeoutCts.Token); }
                catch (JsonException ex) { throw new WhisperInferenceException("whisper-server returned invalid JSON.", ex); }
                var text = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("text", out var t) &&
                    t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() : null;

                if (string.IsNullOrEmpty(text)) return null;
                // whisper emits [BLANK_AUDIO] / [ ... ] on silence.
                if (text.StartsWith('[') && text.EndsWith(']')) return null;
                return text;
            }
        }
        finally
        {
            await ReleaseAsync(instance);
        }
    }

    // ---- Instance management ---------------------------------------------

    /// <summary>
    /// Returns a live instance with its in-flight count already incremented. Retries when
    /// the pool was evicted or an instance died between lookup and acquisition.
    /// </summary>
    private async Task<WhisperInstance> AcquireAsync(string model, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pool = _pools.GetOrAdd(model, m => new ModelPool(m));
            // The load runs on the service lifetime token: a client that gives up during a
            // cold model load stops waiting, but the load completes for the next request.
            await EnsureStartedAsync(pool).WaitAsync(ct);
            if (pool.TryAcquire(out var instance)) return instance;
            if (attempt >= 3)
                throw new WhisperInferenceException($"whisper-server for '{model}' keeps exiting; see service logs.");
            await Task.Delay(100 * (attempt + 1), ct);
        }
    }

    private async Task ReleaseAsync(WhisperInstance instance)
    {
        Interlocked.Decrement(ref instance.InFlight);
        // An eviction may have been deferred while this request was running. Recency is
        // by request start (TryAcquire), so finishing a slow request on an old model
        // doesn't evict a model that was requested more recently.
        try { await EnforceResidentLimitAsync(keep: null); }
        catch (Exception ex) { _logger.LogWarning(ex, "Deferred model eviction failed"); }
    }

    /// <summary>
    /// Returns the pool's start task, (re)starting it when it has never started, failed,
    /// or lost instances to a crash. Dead instances are removed so they are never routed to.
    /// </summary>
    private Task EnsureStartedAsync(ModelPool pool)
    {
        lock (pool.Sync)
        {
            if (pool.Evicted) return Task.CompletedTask; // TryAcquire fails; the caller retries on a fresh pool
            var task = pool.StartTask;
            if (task is { IsCompleted: false }) return task;
            if (task is { IsCompletedSuccessfully: true })
            {
                var dead = pool.Instances.Where(i => i.Process.HasExited).ToArray();
                if (dead.Length == 0) return task;
                foreach (var inst in dead)
                {
                    _logger.LogError("whisper-server '{Model}' on port {Port} exited unexpectedly (exit code {Code}); restarting. Last output:\n{Tail}",
                        pool.Model, inst.Port, SafeExitCode(inst.Process), inst.StderrTail());
                    DisposeInstance(inst);
                }
                pool.Instances = pool.Instances.Except(dead).ToArray();
            }
            pool.StartTask = StartPoolAsync(pool, _stopping.Token);
            return pool.StartTask;
        }
    }

    private async Task StartPoolAsync(ModelPool pool, CancellationToken ct)
    {
        await Task.Yield(); // never run the start inline under pool.Sync
        var modelPath = ModelPath(pool.Model);
        var missing = InstancesPerModel - pool.Instances.Length;
        _logger.LogInformation("Starting {Count} whisper-server instance(s) for model '{Model}' ({ModelPath})",
            missing, pool.Model, modelPath);

        var started = new List<WhisperInstance>();
        try
        {
            for (int i = 0; i < missing; i++)
            {
                var port = AllocatePort();
                var psi = new ProcessStartInfo(WhisperServerBinary)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(modelPath);
                psi.ArgumentList.Add("--host"); psi.ArgumentList.Add("127.0.0.1");
                psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
                psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(Threads.ToString());
                if (!UseGpu) psi.ArgumentList.Add("-ng");

                Process proc;
                try
                {
                    proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                }
                catch (Exception ex)
                {
                    FreePort(port);
                    throw new InvalidOperationException(
                        $"Failed to launch whisper-server binary '{WhisperServerBinary}': {ex.Message}", ex);
                }

                var instance = new WhisperInstance(proc, port, pool);
                // Capture the child's stderr (bounded) so a failure is diagnosable;
                // drain stdout so the pipe can't fill and block the child.
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) instance.AppendStderr(e.Data); };
                proc.OutputDataReceived += (_, _) => { };
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();
                started.Add(instance);

                await WaitForHealthAsync(instance, ct);
                _logger.LogInformation("whisper-server '{Model}' ready on port {Port} (pid {Pid})", pool.Model, port, proc.Id);
            }
        }
        catch
        {
            // Roll back everything started in this call so we don't leak ports/processes.
            foreach (var inst in started) DisposeInstance(inst);
            throw;
        }

        lock (pool.Sync)
        {
            if (pool.Evicted || _disposed)
            {
                foreach (var inst in started) DisposeInstance(inst);
                return;
            }
            pool.Instances = [.. pool.Instances, .. started];
            pool.Ready = true;
        }
        await EnforceResidentLimitAsync(keep: pool.Model);
    }

    private async Task WaitForHealthAsync(WhisperInstance instance, CancellationToken ct)
    {
        // Dedicated non-logging client so the expected connection-refused churn while the
        // child boots doesn't flood the logs.
        var client = _httpClientFactory.CreateClient("whisper");
        var port = instance.Port;
        var deadline = Environment.TickCount64 + StartupTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // Fail fast if the child already died (bad/oversized model, OOM, missing CUDA
            // libs, port in use, ...) instead of polling a dead port for the full timeout.
            if (instance.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"whisper-server exited during startup on port {port} (exit code {instance.Process.ExitCode}). " +
                    $"Last output:\n{instance.StderrTail()}");
            }

            try
            {
                using var resp = await client.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { /* connection refused while still booting — expected */ }

            await Task.Delay(500, ct);
        }
        throw new TimeoutException(
            $"whisper-server on port {port} did not become ready within {StartupTimeoutMs / 1000}s. " +
            $"Last output:\n{instance.StderrTail()}");
    }

    private async Task EnforceResidentLimitAsync(string? keep)
    {
        if (_pools.Values.Count(p => p.Ready) <= MaxResidentModels) return;
        await _evictLock.WaitAsync();
        try
        {
            while (_pools.Values.Count(p => p.Ready) > MaxResidentModels)
            {
                // Evict the least-recently-used idle pool that isn't the one being kept.
                // A pool with requests in flight is skipped; ReleaseAsync retries later.
                ModelPool? victim = null;
                foreach (var candidate in _pools.Values
                    .Where(p => p.Ready && !string.Equals(p.Model, keep, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.LastUsedTicks))
                {
                    if (candidate.TryMarkEvicted()) { victim = candidate; break; }
                }
                if (victim == null) break;
                _pools.TryRemove(new KeyValuePair<string, ModelPool>(victim.Model, victim));
                _logger.LogInformation("Evicting model '{Model}' to stay within {Max} resident models", victim.Model, MaxResidentModels);
                KillPool(victim);
            }
        }
        finally
        {
            _evictLock.Release();
        }
    }

    private int AllocatePort()
    {
        lock (_portLock)
        {
            for (int p = BasePort; p < BasePort + 1000; p++)
            {
                if (_usedPorts.Add(p)) return p;
            }
            throw new InvalidOperationException("No free port available for whisper-server");
        }
    }

    private void FreePort(int port)
    {
        lock (_portLock) { _usedPorts.Remove(port); }
    }

    private void DisposeInstance(WhisperInstance inst)
    {
        try { if (!inst.Process.HasExited) inst.Process.Kill(entireProcessTree: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill whisper-server on port {Port}", inst.Port); }
        finally { FreePort(inst.Port); inst.Process.Dispose(); }
    }

    private void KillPool(ModelPool pool)
    {
        WhisperInstance[] instances;
        lock (pool.Sync)
        {
            instances = pool.Instances;
            pool.Instances = [];
            pool.Ready = false;
        }
        foreach (var inst in instances) DisposeInstance(inst);
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); } catch { return "unknown"; }
    }

    // ---- Hardware detection (reused by /health) ---------------------------

    private HardwareInfo DetectHardware()
    {
        var info = new HardwareInfo { Cpu = GetCpuName() };
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                // Bound the whole probe: a hung driver must not hang /health.
                var stdout = proc.StandardOutput.ReadToEndAsync();
                _ = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(3000) || !stdout.Wait(1000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    _logger.LogWarning("nvidia-smi did not respond within 3s; reporting CPU only");
                    return info;
                }
                var output = stdout.Result.Trim();
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var parts = output.Split(',', 2);
                    info.Gpu = parts[0].Trim();
                    if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var mb)) info.GpuMemoryMb = mb;
                    info.CudaAvailable = true;
                }
            }
        }
        catch { /* nvidia-smi not available */ }
        return info;
    }

    private static string GetCpuName()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var modelLine = File.ReadLines("/proc/cpuinfo")
                    .FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                if (modelLine != null)
                {
                    var value = modelLine.Split(':', 2);
                    if (value.Length == 2) return value[1].Trim();
                }
            }
        }
        catch { }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static string SafeForLog(string? value) =>
        (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping.Cancel();
        foreach (var pool in _pools.Values) KillPool(pool);
        _pools.Clear();
        // _stopping is intentionally not disposed: an in-progress load may still observe its token.
    }

    // ---- Nested types -----------------------------------------------------

    private sealed class ModelPool
    {
        public string Model { get; }
        /// <summary>Guards Instances, StartTask, Evicted and in-flight acquisition.</summary>
        public object Sync { get; } = new();
        // Replaced (never mutated) under Sync so readers always see a consistent array.
        public volatile WhisperInstance[] Instances = [];
        public Task? StartTask;
        public volatile bool Ready;
        public bool Evicted;
        private long _lastUsedTicks = DateTime.UtcNow.Ticks;
        private int _rr = -1;

        public ModelPool(string model) => Model = model;

        public long LastUsedTicks => Interlocked.Read(ref _lastUsedTicks);
        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

        /// <summary>Picks a live instance (idle first, else round-robin) and marks it in flight.</summary>
        public bool TryAcquire(out WhisperInstance instance)
        {
            lock (Sync)
            {
                instance = null!;
                if (Evicted) return false;
                var live = Instances.Where(i => !i.Process.HasExited).ToArray();
                if (live.Length == 0 || live.Length < Instances.Length) return false; // let EnsureStarted replace the dead
                instance = live.FirstOrDefault(i => Volatile.Read(ref i.InFlight) == 0)
                    ?? live[(Interlocked.Increment(ref _rr) & int.MaxValue) % live.Length];
                Interlocked.Increment(ref instance.InFlight);
                Touch();
                return true;
            }
        }

        /// <summary>Marks the pool evicted only if no request is using it.</summary>
        public bool TryMarkEvicted()
        {
            lock (Sync)
            {
                if (Evicted || Instances.Any(i => Volatile.Read(ref i.InFlight) > 0)) return false;
                Evicted = true;
                return true;
            }
        }
    }

    private sealed class WhisperInstance
    {
        public Process Process { get; }
        public int Port { get; }
        public ModelPool Owner { get; }
        public int InFlight;

        private readonly object _errLock = new();
        private readonly Queue<string> _stderr = new();

        public WhisperInstance(Process process, int port, ModelPool owner)
        {
            Process = process;
            Port = port;
            Owner = owner;
        }

        // Keep a bounded tail of the child's stderr for diagnostics.
        public void AppendStderr(string line)
        {
            lock (_errLock)
            {
                _stderr.Enqueue(line);
                while (_stderr.Count > 40) _stderr.Dequeue();
            }
        }

        public string StderrTail()
        {
            lock (_errLock) return _stderr.Count == 0 ? "(no output captured)" : string.Join("\n", _stderr);
        }
    }
}

/// <summary>The requested model name is invalid or not installed (client error).</summary>
public sealed class WhisperModelException(string message) : Exception(message);

/// <summary>whisper-server failed or returned an unusable response (upstream error).</summary>
public sealed class WhisperInferenceException(string message, Exception? inner = null) : Exception(message, inner);

public class HardwareInfo
{
    public string Cpu { get; set; } = "Unknown";
    public string? Gpu { get; set; }
    public int? GpuMemoryMb { get; set; }
    public bool CudaAvailable { get; set; }
    public string AccelerationMode => CudaAvailable ? "GPU (CUDA)" : "CPU";
}
