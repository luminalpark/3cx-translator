using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TranslationBridge.Configuration;

namespace TranslationBridge.Services;

/// <summary>
/// Diagnostic tool to test streaming translation and save all received audio.
/// This bypasses VAD and sends audio continuously to diagnose translation issues.
/// </summary>
public class StreamingDiagnostic : IDisposable
{
    private readonly ILogger<StreamingDiagnostic> _logger;
    private readonly BridgeConfig _config;
    private GeminiClient? _client;
    private WaveFileWriter? _outputWavWriter;
    private AudioPlaybackBuffer? _prerollBuffer;
    private MMDevice? _outputDevice;

    // Statistics
    private int _audioChunksSent;
    private int _audioChunksReceived;
    private int _totalBytesSent;
    private int _totalBytesReceived;
    private DateTime _startTime;
    private readonly List<string> _sourceTexts = new();
    private readonly List<string> _translatedTexts = new();
    private readonly List<int> _audioChunkSizes = new();

    // Events for UI updates
    public event Action<string>? OnLogMessage;
    public event Action<DiagnosticStats>? OnStatsUpdated;
    public event Action? OnComplete;

    public bool IsRunning { get; private set; }
    public string? OutputWavPath { get; private set; }
    public bool PrerollEnabled { get; private set; }

    public StreamingDiagnostic(ILogger<StreamingDiagnostic> logger, BridgeConfig config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Enable preroll buffer for real-time playback during diagnostic
    /// </summary>
    public void EnablePrerollPlayback(int prerollMs = 400)
    {
        var enumerator = new MMDeviceEnumerator();
        _outputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        _prerollBuffer = new AudioPlaybackBuffer(
            _logger,
            new WaveFormat(16000, 16, 1),
            prerollMs: prerollMs,
            silenceTimeoutMs: 2000);
        _prerollBuffer.Initialize(_outputDevice);

        PrerollEnabled = true;
        _logger.LogInformation("Preroll playback enabled: {PrerollMs}ms buffer, device: {Device}",
            prerollMs, _outputDevice.FriendlyName);
    }

    /// <summary>
    /// Run diagnostic test with an audio file
    /// </summary>
    public async Task RunDiagnosticAsync(string inputWavPath, string outputFolder, CancellationToken ct = default)
    {
        if (IsRunning)
        {
            Log("ERROR: Diagnostic already running");
            return;
        }

        IsRunning = true;
        _startTime = DateTime.UtcNow;
        ResetStats();

        try
        {
            // Create output file path
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            OutputWavPath = Path.Combine(outputFolder, $"diagnostic_output_{timestamp}.wav");
            var logPath = Path.Combine(outputFolder, $"diagnostic_log_{timestamp}.txt");

            Log($"=== STREAMING DIAGNOSTIC TEST ===");
            Log($"Input file: {inputWavPath}");
            Log($"Output file: {OutputWavPath}");
            Log($"Log file: {logPath}");
            Log($"Server: {_config.ServerUrl}");
            Log("");

            // Create output WAV file (16kHz, 16-bit, mono)
            _outputWavWriter = new WaveFileWriter(OutputWavPath, new WaveFormat(16000, 16, 1));

            // Create and connect client
            Log("Connecting to server...");
            var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
            _client = new GeminiClient(
                loggerFactory.CreateLogger<GeminiClient>(),
                Options.Create(_config));

            // Subscribe to ALL events for diagnosis
            _client.OnConnectionChanged += connected => Log($"CONNECTION: {(connected ? "CONNECTED" : "DISCONNECTED")}");
            _client.OnStreamingModeChanged += enabled => Log($"STREAMING MODE: {(enabled ? "ENABLED" : "DISABLED")}");
            _client.OnStreamingSourceText += text =>
            {
                Log($"SOURCE TEXT: \"{text}\"");
                _sourceTexts.Add(text);
            };
            _client.OnStreamingTranslatedText += text =>
            {
                Log($"TRANSLATED TEXT: \"{text}\"");
                _translatedTexts.Add(text);
            };
            _client.OnTranslationReceived += result =>
            {
                Log($"TRANSLATION RESULT: \"{result.SourceText}\" -> \"{result.TranslatedText}\" ({result.ProcessingTimeMs:F0}ms)");
                return Task.CompletedTask;
            };
            _client.OnAudioReceived += audioData =>
            {
                _audioChunksReceived++;
                _totalBytesReceived += audioData.Length;
                _audioChunkSizes.Add(audioData.Length);

                Log($"AUDIO RECEIVED: {audioData.Length} bytes (chunk #{_audioChunksReceived}, total: {_totalBytesReceived} bytes)");

                // Write to output WAV (raw, without buffering)
                _outputWavWriter?.Write(audioData, 0, audioData.Length);

                // If preroll is enabled, play through buffer for smooth audio
                if (PrerollEnabled && _prerollBuffer != null)
                {
                    _prerollBuffer.AddAudio(audioData);
                }

                UpdateStats();
                return Task.CompletedTask;
            };
            _client.OnTranslationSkipped += result =>
            {
                Log($"TRANSLATION SKIPPED: {result.Reason} (detected: {result.DetectedLanguageName ?? result.DetectedLanguage})");
                return Task.CompletedTask;
            };
            _client.OnLanguageDetected += (code, name) =>
            {
                Log($"LANGUAGE DETECTED: {name} ({code})");
            };

            // Connect
            await _client.ConnectAsync(ct);
            Log("Connected!");

            // Configure for auto-detect -> Italian
            Log("Configuring languages: auto -> it");
            await _client.ConfigureLanguagesAsync("auto", "it", ct);

            // Enable streaming mode
            Log("Enabling streaming mode...");
            await _client.SetStreamingModeAsync(true, ct);
            await Task.Delay(500, ct); // Wait for streaming session to be ready

            // Start preroll buffer if enabled
            if (PrerollEnabled && _prerollBuffer != null)
            {
                _prerollBuffer.Start();
                Log($"Preroll playback started (1000ms buffer)");
            }

            // Load input audio
            Log($"Loading input audio: {inputWavPath}");
            var inputAudio = LoadAndConvertAudio(inputWavPath);
            Log($"Input audio loaded: {inputAudio.Length} bytes ({inputAudio.Length / 32.0:F1}ms at 16kHz)");

            // Send audio in chunks (100ms chunks = 3200 bytes at 16kHz/16bit/mono)
            const int chunkSize = 3200;
            const int chunkDelayMs = 100; // Real-time simulation

            Log("");
            Log($"=== SENDING AUDIO ===");
            Log($"Chunk size: {chunkSize} bytes ({chunkDelayMs}ms)");
            Log($"Total chunks: {(inputAudio.Length + chunkSize - 1) / chunkSize}");
            Log("");

            for (int offset = 0; offset < inputAudio.Length && !ct.IsCancellationRequested; offset += chunkSize)
            {
                var remaining = Math.Min(chunkSize, inputAudio.Length - offset);
                var chunk = new byte[remaining];
                Array.Copy(inputAudio, offset, chunk, 0, remaining);

                await _client.SendAudioChunkAsync(chunk, ct);
                _audioChunksSent++;
                _totalBytesSent += remaining;

                if (_audioChunksSent % 10 == 0)
                {
                    Log($"Sent chunk #{_audioChunksSent}: {_totalBytesSent} bytes total");
                }

                UpdateStats();

                // Wait real-time delay (simulates live audio)
                await Task.Delay(chunkDelayMs, ct);
            }

            Log("");
            Log($"=== AUDIO SENDING COMPLETE ===");
            Log($"Sent {_audioChunksSent} chunks, {_totalBytesSent} bytes total");
            Log("");

            // Signal end of turn to Gemini - this triggers translation response
            Log("Signaling end_of_turn to Gemini (triggering translation)...");
            await _client.SendEndOfTurnAsync(ct);

            // Wait for remaining translations to arrive
            Log("Waiting for translation response (15 seconds)...");
            await Task.Delay(15000, ct);

            // Final statistics
            PrintFinalStats(logPath);

        }
        catch (OperationCanceledException)
        {
            Log("Diagnostic cancelled");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            _logger.LogError(ex, "Diagnostic error");
        }
        finally
        {
            // Cleanup
            _outputWavWriter?.Dispose();
            _outputWavWriter = null;

            // Stop preroll buffer
            _prerollBuffer?.Stop();

            if (_client != null)
            {
                await _client.DisconnectAsync();
                await _client.DisposeAsync();
                _client = null;
            }

            IsRunning = false;
            OnComplete?.Invoke();
        }
    }

    private byte[] LoadAndConvertAudio(string filePath)
    {
        using var reader = new AudioFileReader(filePath);
        var targetFormat = new WaveFormat(16000, 16, 1);

        // Resample if needed
        using var resampler = new MediaFoundationResampler(reader, targetFormat);
        resampler.ResamplerQuality = 60;

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private void ResetStats()
    {
        _audioChunksSent = 0;
        _audioChunksReceived = 0;
        _totalBytesSent = 0;
        _totalBytesReceived = 0;
        _sourceTexts.Clear();
        _translatedTexts.Clear();
        _audioChunkSizes.Clear();
    }

    private void UpdateStats()
    {
        OnStatsUpdated?.Invoke(new DiagnosticStats
        {
            ChunksSent = _audioChunksSent,
            ChunksReceived = _audioChunksReceived,
            BytesSent = _totalBytesSent,
            BytesReceived = _totalBytesReceived,
            ElapsedTime = DateTime.UtcNow - _startTime
        });
    }

    private void PrintFinalStats(string logPath)
    {
        var elapsed = DateTime.UtcNow - _startTime;

        Log("");
        Log("╔══════════════════════════════════════════════════════════════╗");
        Log("║                    DIAGNOSTIC RESULTS                        ║");
        Log("╠══════════════════════════════════════════════════════════════╣");
        Log($"║  Duration: {elapsed.TotalSeconds:F1} seconds");
        Log($"║  Audio sent: {_audioChunksSent} chunks, {_totalBytesSent} bytes");
        Log($"║  Audio received: {_audioChunksReceived} chunks, {_totalBytesReceived} bytes");
        Log($"║  Ratio received/sent: {(_totalBytesSent > 0 ? (100.0 * _totalBytesReceived / _totalBytesSent) : 0):F1}%");
        Log("╠══════════════════════════════════════════════════════════════╣");

        if (_audioChunkSizes.Count > 0)
        {
            Log($"║  Audio chunk sizes: min={_audioChunkSizes.Min()}, max={_audioChunkSizes.Max()}, avg={_audioChunkSizes.Average():F0}");
        }

        Log($"║  Source texts received: {_sourceTexts.Count}");
        Log($"║  Translated texts received: {_translatedTexts.Count}");

        if (_sourceTexts.Count > 0)
        {
            Log("╠══════════════════════════════════════════════════════════════╣");
            Log("║  SOURCE TEXTS:");
            foreach (var text in _sourceTexts.Take(10))
            {
                Log($"║    - \"{text}\"");
            }
            if (_sourceTexts.Count > 10) Log($"║    ... and {_sourceTexts.Count - 10} more");
        }

        if (_translatedTexts.Count > 0)
        {
            Log("╠══════════════════════════════════════════════════════════════╣");
            Log("║  TRANSLATED TEXTS:");
            foreach (var text in _translatedTexts.Take(10))
            {
                Log($"║    - \"{text}\"");
            }
            if (_translatedTexts.Count > 10) Log($"║    ... and {_translatedTexts.Count - 10} more");
        }

        Log("╠══════════════════════════════════════════════════════════════╣");
        Log($"║  Output WAV: {OutputWavPath}");
        Log($"║  Output WAV size: {(File.Exists(OutputWavPath!) ? new FileInfo(OutputWavPath!).Length : 0)} bytes");
        Log("╚══════════════════════════════════════════════════════════════╝");

        // Also save log to file
        try
        {
            // Get all log messages and save to file
            _logger.LogInformation("Diagnostic complete. Results saved to: {Path}", logPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save log file");
        }
    }

    private void Log(string message)
    {
        var timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        _logger.LogInformation(message);
        OnLogMessage?.Invoke(timestamped);
    }

    public void Stop()
    {
        // Will be handled by cancellation token
        Log("Stop requested...");
    }

    public void Dispose()
    {
        _outputWavWriter?.Dispose();
        _prerollBuffer?.Dispose();
    }
}

/// <summary>
/// Diagnostic statistics
/// </summary>
public class DiagnosticStats
{
    public int ChunksSent { get; set; }
    public int ChunksReceived { get; set; }
    public int BytesSent { get; set; }
    public int BytesReceived { get; set; }
    public TimeSpan ElapsedTime { get; set; }
}
