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

    public IReadOnlyList<string> LoadedModels() => _pools.Keys.ToList();

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
            try { await GetInstanceAsync(DefaultModel, cancellationToken); }
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

    public async Task<string?> TranscribeAsync(string wavPath, string? model, string? prompt, CancellationToken ct = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!;
        if (!IsValidModelName(modelName))
        {
            _logger.LogError("Invalid model name '{Model}'; refusing to use it.", SafeForLog(modelName));
            return null;
        }
        if (!File.Exists(ModelPath(modelName)))
        {
            _logger.LogError("Model '{Model}' not installed at {Path}", SafeForLog(modelName), ModelPath(modelName));
            return null;
        }

        var instance = await GetInstanceAsync(modelName, ct);
        Interlocked.Increment(ref instance.InFlight);
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

            using var resp = await client.PostAsync($"http://127.0.0.1:{instance.Port}/inference", content, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Inference failed on {Model} (HTTP {Code}): {Body}", SafeForLog(modelName), (int)resp.StatusCode, body);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var text = json.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null;

            if (string.IsNullOrEmpty(text)) return null;
            // whisper emits [BLANK_AUDIO] / [ ... ] on silence.
            if (text.StartsWith('[') && text.EndsWith(']')) return null;
            return text;
        }
        finally
        {
            Interlocked.Decrement(ref instance.InFlight);
            instance.Owner.Touch();
        }
    }

    // ---- Instance management ---------------------------------------------

    private async Task<WhisperInstance> GetInstanceAsync(string model, CancellationToken ct)
    {
        var pool = _pools.GetOrAdd(model, m => new ModelPool(m));

        if (!pool.Ready)
        {
            await pool.InitLock.WaitAsync(ct);
            try
            {
                if (!pool.Ready)
                {
                    await StartPoolAsync(pool, ct);
                    pool.Ready = true;
                    await EnforceResidentLimitAsync(keep: model);
                }
            }
            finally
            {
                pool.InitLock.Release();
            }
        }

        pool.Touch();
        return pool.Next();
    }

    private async Task StartPoolAsync(ModelPool pool, CancellationToken ct)
    {
        var modelPath = ModelPath(pool.Model);
        _logger.LogInformation("Starting {Count} whisper-server instance(s) for model '{Model}' ({ModelPath})",
            InstancesPerModel, pool.Model, modelPath);

        var started = new List<WhisperInstance>();
        try
        {
            for (int i = 0; i < InstancesPerModel; i++)
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
                // Capture the child's stderr (bounded) so a startup failure is diagnosable;
                // drain stdout so the pipe can't fill and block the child.
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) instance.AppendStderr(e.Data); };
                proc.OutputDataReceived += (_, _) => { };
                proc.BeginErrorReadLine();
                proc.BeginOutputReadLine();

                started.Add(instance);
                pool.Instances.Add(instance);

                await WaitForHealthAsync(instance, ct);
                _logger.LogInformation("whisper-server '{Model}' ready on port {Port} (pid {Pid})", pool.Model, port, proc.Id);
            }
        }
        catch
        {
            // Roll back everything started in this call so we don't leak ports/processes
            // or leave dead instances in the pool for the round-robin to hand out.
            foreach (var inst in started)
            {
                pool.Instances.Remove(inst);
                try { if (!inst.Process.HasExited) inst.Process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
                FreePort(inst.Port);
                inst.Process.Dispose();
            }
            throw;
        }
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
            catch { /* connection refused while still booting — expected */ }

            await Task.Delay(500, ct);
        }
        throw new TimeoutException(
            $"whisper-server on port {port} did not become ready within {StartupTimeoutMs / 1000}s. " +
            $"Last output:\n{instance.StderrTail()}");
    }

    private async Task EnforceResidentLimitAsync(string keep)
    {
        if (_pools.Count <= MaxResidentModels) return;
        await _evictLock.WaitAsync();
        try
        {
            while (_pools.Count > MaxResidentModels)
            {
                // Evict the least-recently-used ready pool that isn't the one we just created.
                var victim = _pools.Values
                    .Where(p => p.Ready && !string.Equals(p.Model, keep, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.LastUsedTicks)
                    .FirstOrDefault();
                if (victim == null) break;
                if (_pools.TryRemove(victim.Model, out _))
                {
                    _logger.LogInformation("Evicting model '{Model}' to stay within {Max} resident models", victim.Model, MaxResidentModels);
                    KillPool(victim);
                }
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

    private void KillPool(ModelPool pool)
    {
        foreach (var inst in pool.Instances)
        {
            try { if (!inst.Process.HasExited) inst.Process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill whisper-server pid {Pid}", inst.Process.Id); }
            finally { FreePort(inst.Port); inst.Process.Dispose(); }
        }
        pool.Instances.Clear();
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
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
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
        foreach (var pool in _pools.Values) KillPool(pool);
        _pools.Clear();
    }

    // ---- Nested types -----------------------------------------------------

    private sealed class ModelPool
    {
        public string Model { get; }
        public SemaphoreSlim InitLock { get; } = new(1, 1);
        public List<WhisperInstance> Instances { get; } = new();
        public volatile bool Ready;
        private long _lastUsedTicks = DateTime.UtcNow.Ticks;
        private int _rr = -1;

        public ModelPool(string model) => Model = model;

        public long LastUsedTicks => Interlocked.Read(ref _lastUsedTicks);
        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

        // Prefer an idle instance; otherwise round-robin.
        public WhisperInstance Next()
        {
            var idle = Instances.FirstOrDefault(i => Volatile.Read(ref i.InFlight) == 0);
            if (idle != null) return idle;
            var idx = Interlocked.Increment(ref _rr);
            return Instances[(idx & int.MaxValue) % Instances.Count];
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

public class HardwareInfo
{
    public string Cpu { get; set; } = "Unknown";
    public string? Gpu { get; set; }
    public int? GpuMemoryMb { get; set; }
    public bool CudaAvailable { get; set; }
    public string AccelerationMode => CudaAvailable ? "GPU (CUDA)" : "CPU";
}
