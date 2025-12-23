using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranslationBridge.Configuration;

namespace TranslationBridge.Services;

/// <summary>
/// WebSocket client for Gemini translation server
/// Supports automatic language detection and real-time streaming mode
/// </summary>
public class GeminiClient : IAsyncDisposable
{
    private readonly ILogger<GeminiClient> _logger;
    private readonly BridgeConfig _config;
    private ClientWebSocket? _webSocket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _isConnected;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    /// <summary>
    /// Fired when translation result is received
    /// </summary>
    public event Func<TranslationResult, Task>? OnTranslationReceived;

    /// <summary>
    /// Fired when translated audio is received
    /// </summary>
    public event Func<byte[], Task>? OnAudioReceived;

    /// <summary>
    /// Fired when translation is skipped (same language)
    /// </summary>
    public event Func<SkippedResult, Task>? OnTranslationSkipped;

    /// <summary>
    /// Fired when language is detected
    /// </summary>
    public event Action<string, string>? OnLanguageDetected; // code, name

    /// <summary>
    /// Fired when connection state changes
    /// </summary>
    public event Action<bool>? OnConnectionChanged;

    /// <summary>
    /// Fired when streaming source text is received (incremental)
    /// </summary>
    public event Action<string>? OnStreamingSourceText;

    /// <summary>
    /// Fired when streaming translated text is received (incremental)
    /// </summary>
    public event Action<string>? OnStreamingTranslatedText;

    /// <summary>
    /// Fired when streaming mode is enabled/disabled
    /// </summary>
    public event Action<bool>? OnStreamingModeChanged;

    /// <summary>
    /// Fired when turn_complete is received from server (Manual VAD)
    /// Used for auto-restart logic in rolling turns
    /// </summary>
    public event Action? OnTurnComplete;

    public bool IsConnected => _isConnected && _webSocket?.State == WebSocketState.Open;

    /// <summary>
    /// Last detected language code
    /// </summary>
    public string? LastDetectedLanguage { get; private set; }

    /// <summary>
    /// Whether streaming mode is currently enabled
    /// </summary>
    public bool IsStreamingMode { get; private set; }

    public GeminiClient(
        ILogger<GeminiClient> logger,
        IOptions<BridgeConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;

        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        // Build URI with API key if configured
        var baseUri = new Uri(_config.ServerUrl);
        Uri uri;
        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            var separator = string.IsNullOrEmpty(baseUri.Query) ? "?" : "&";
            uri = new Uri($"{baseUri}{separator}key={Uri.EscapeDataString(_config.ApiKey)}");
            _logger.LogInformation("Connecting to Gemini server at {Url} (with API key)...", baseUri);
        }
        else
        {
            uri = baseUri;
            _logger.LogInformation("Connecting to Gemini server at {Url}...", uri);
        }

        try
        {
            await _webSocket.ConnectAsync(uri, cancellationToken);
            _isConnected = true;
            OnConnectionChanged?.Invoke(true);

            _logger.LogInformation("Connected to Gemini server");

            // Start receive loop
            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            // Wait for welcome message
            await Task.Delay(100, cancellationToken);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            OnConnectionChanged?.Invoke(false);
            _logger.LogError(ex, "Failed to connect to Gemini server");
            throw;
        }
    }

    public async Task ConfigureLanguagesAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        // Normalize language codes (es-ES -> es, de-DE -> de, etc.)
        var normalizedSource = NormalizeLanguageCode(sourceLanguage);
        var normalizedTarget = NormalizeLanguageCode(targetLanguage);

        var config = new
        {
            type = "configure",
            source_lang = normalizedSource,  // Can be "auto"
            target_lang = normalizedTarget
        };

        await SendJsonAsync(config, cancellationToken);

        var modeStr = normalizedSource == "auto" ? "auto-detect" : normalizedSource;
        _logger.LogInformation("Configured translation: {Source} -> {Target}", modeStr, normalizedTarget);
    }

    /// <summary>
    /// Normalize BCP-47 language codes to simple 2-letter codes.
    /// Gemini doesn't accept extended codes like 'es-ES', only 'es'.
    /// </summary>
    private static string NormalizeLanguageCode(string code)
    {
        if (string.IsNullOrEmpty(code) || code == "auto")
            return code;

        // Split on dash or underscore and take only the primary language subtag
        var parts = code.Split('-', '_');
        return parts[0].ToLowerInvariant();
    }

    /// <summary>
    /// Enable/disable real-time streaming mode
    /// In streaming mode, translations arrive incrementally as you speak
    /// </summary>
    public async Task SetStreamingModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var msg = new
        {
            type = "set_streaming",
            enabled = enabled
        };

        await SendJsonAsync(msg, cancellationToken);
        _logger.LogInformation("Streaming mode: {Enabled}", enabled ? "ENABLED" : "DISABLED");
    }

    public async Task SetLanguageAsync(string sourceLanguage, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeLanguageCode(sourceLanguage);
        var msg = new
        {
            type = "set_language",
            source_lang = normalized
        };

        await SendJsonAsync(msg, cancellationToken);
        _logger.LogInformation("Manual language override: {Lang}", normalized);
    }

    public async Task EnableAutoDetectAsync(CancellationToken cancellationToken = default)
    {
        var msg = new { type = "enable_auto_detect" };
        await SendJsonAsync(msg, cancellationToken);
        _logger.LogInformation("Auto-detect re-enabled");
    }

    /// <summary>
    /// Send voice reference file for voice cloning
    /// </summary>
    /// <param name="voiceFilePath">Path to WAV file (5-10 seconds of speech)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task SetVoiceReferenceAsync(string voiceFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(voiceFilePath) || !File.Exists(voiceFilePath))
        {
            _logger.LogWarning("Voice reference file not found: {Path}", voiceFilePath);
            return;
        }

        try
        {
            // Read the WAV file and convert to base64
            var audioBytes = await File.ReadAllBytesAsync(voiceFilePath, cancellationToken);
            var audioBase64 = Convert.ToBase64String(audioBytes);

            var msg = new
            {
                type = "set_voice_ref",
                audio_base64 = audioBase64
            };

            await SendJsonAsync(msg, cancellationToken);
            _logger.LogInformation("Voice reference sent to server: {Path} ({Size} bytes)",
                Path.GetFileName(voiceFilePath), audioBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send voice reference: {Path}", voiceFilePath);
        }
    }

    /// <summary>
    /// Clear the voice reference on the server
    /// </summary>
    public async Task ClearVoiceReferenceAsync(CancellationToken cancellationToken = default)
    {
        var msg = new
        {
            type = "set_voice_ref",
            audio_base64 = (string?)null
        };

        await SendJsonAsync(msg, cancellationToken);
        _logger.LogInformation("Voice reference cleared on server");
    }

    public async Task SendAudioChunkAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            _logger.LogWarning("Cannot send audio: not connected");
            return;
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket!.SendAsync(
                new ArraySegment<byte>(audioData),
                WebSocketMessageType.Binary,
                true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task RequestTranslationAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            _logger.LogWarning("Cannot request translation: not connected");
            return;
        }

        var request = new { type = "translate" };
        await SendJsonAsync(request, cancellationToken);
    }

    /// <summary>
    /// Signal end of turn to Gemini in streaming mode.
    /// NOTE: With Manual VAD enabled on server, use SendActivityEndAsync instead.
    /// This tells Gemini "I'm done speaking, now translate and respond".
    /// </summary>
    public async Task SendEndOfTurnAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            _logger.LogWarning("Cannot send end_of_turn: not connected");
            return;
        }

        if (!IsStreamingMode)
        {
            _logger.LogWarning("Cannot send end_of_turn: not in streaming mode");
            return;
        }

        var request = new { type = "end_of_turn" };
        await SendJsonAsync(request, cancellationToken);
        _logger.LogInformation("Sent end_of_turn signal to Gemini");
    }

    /// <summary>
    /// Signal start of user speech (Manual VAD).
    /// Call this before sending audio chunks when the server has Manual VAD enabled.
    /// </summary>
    public async Task SendActivityStartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            _logger.LogWarning("Cannot send activity_start: not connected");
            return;
        }

        if (!IsStreamingMode)
        {
            _logger.LogWarning("Cannot send activity_start: not in streaming mode");
            return;
        }

        var request = new { type = "activity_start" };
        await SendJsonAsync(request, cancellationToken);
        _logger.LogInformation("Sent activity_start signal to Gemini (Manual VAD)");
    }

    /// <summary>
    /// Signal end of user speech (Manual VAD).
    /// Call this after sending audio chunks to trigger translation response.
    /// This is the preferred method when server has Manual VAD enabled.
    /// </summary>
    public async Task SendActivityEndAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            _logger.LogWarning("Cannot send activity_end: not connected");
            return;
        }

        if (!IsStreamingMode)
        {
            _logger.LogWarning("Cannot send activity_end: not in streaming mode");
            return;
        }

        var request = new { type = "activity_end" };
        await SendJsonAsync(request, cancellationToken);
        _logger.LogInformation("Sent activity_end signal to Gemini (Manual VAD) - triggering translation");
    }

    public async Task ClearBufferAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return;

        var request = new { type = "clear" };
        await SendJsonAsync(request, cancellationToken);
    }

    private async Task SendJsonAsync(object obj, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _webSocket!.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var messageBuffer = new List<byte>();

        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                var result = await _webSocket!.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Server closed connection");
                    break;
                }

                messageBuffer.AddRange(buffer.Take(result.Count));

                if (result.EndOfMessage)
                {
                    var data = messageBuffer.ToArray();
                    messageBuffer.Clear();

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        await HandleTextMessageAsync(data);
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        await HandleBinaryMessageAsync(data);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket error");
        }
        finally
        {
            _isConnected = false;
            OnConnectionChanged?.Invoke(false);
        }
    }

    private async Task HandleTextMessageAsync(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var msgType = root.GetProperty("type").GetString();

            switch (msgType)
            {
                case "connected":
                    var server = root.TryGetProperty("server", out var srv) ? srv.GetString() : "unknown";
                    var model = root.TryGetProperty("model", out var mdl) ? mdl.GetString() : "unknown";
                    _logger.LogDebug("Server welcome: {Server}, Model: {Model}", server, model);
                    break;

                case "configured":
                    var srcLang = root.GetProperty("source_lang").GetString();
                    var tgtLang = root.GetProperty("target_lang").GetString();
                    _logger.LogDebug("Config acknowledged: {Src} -> {Tgt}", srcLang, tgtLang);
                    break;

                case "streaming_enabled":
                    var streamingEnabled = root.GetProperty("enabled").GetBoolean();
                    IsStreamingMode = streamingEnabled;
                    _logger.LogInformation("Streaming mode: {Enabled}", streamingEnabled ? "ACTIVE" : "INACTIVE");
                    OnStreamingModeChanged?.Invoke(streamingEnabled);
                    break;

                case "streaming_session_ready":
                    var streamSrc = root.TryGetProperty("source_lang", out var ss) ? ss.GetString() : "auto";
                    var streamTgt = root.TryGetProperty("target_lang", out var st) ? st.GetString() : "?";
                    _logger.LogInformation("Streaming session ready: {Src} -> {Tgt}", streamSrc, streamTgt);
                    break;

                case "source_text":
                    var sourceText = root.TryGetProperty("text", out var stxt) ? stxt.GetString() : "";
                    if (!string.IsNullOrEmpty(sourceText))
                    {
                        _logger.LogDebug("Streaming source: {Text}", sourceText);
                        OnStreamingSourceText?.Invoke(sourceText);
                    }
                    break;

                case "translated_text":
                    var translatedText = root.TryGetProperty("text", out var ttxt) ? ttxt.GetString() : "";
                    if (!string.IsNullOrEmpty(translatedText))
                    {
                        _logger.LogDebug("Streaming translation: {Text}", translatedText);
                        OnStreamingTranslatedText?.Invoke(translatedText);
                    }
                    break;

                case "model_turn_started":
                    _logger.LogDebug("Gemini speaking (buffering input)");
                    break;

                case "turn_complete":
                    _logger.LogInformation(">>> TURN_COMPLETE received from server");
                    OnTurnComplete?.Invoke();
                    break;

                case "end_of_turn_sent":
                    _logger.LogDebug("End of turn acknowledged by server");
                    break;

                case "activity_start_sent":
                    _logger.LogDebug("Activity start acknowledged by server (Manual VAD)");
                    break;

                case "activity_end_sent":
                    _logger.LogDebug("Activity end acknowledged by server (Manual VAD)");
                    break;

                case "result":
                case "translation":
                    var result = new TranslationResult
                    {
                        SourceText = root.TryGetProperty("source_text", out var st2) ? st2.GetString() ?? "" : "",
                        TranslatedText = root.TryGetProperty("translated_text", out var tt2) ? tt2.GetString() ?? "" : "",
                        ProcessingTimeMs = root.TryGetProperty("processing_time_ms", out var pt) ? pt.GetDouble() : 0,
                        AudioSampleRate = root.TryGetProperty("audio_sample_rate", out var sr)
                            ? sr.GetInt32() : 16000
                    };

                    // Handle detected language
                    if (root.TryGetProperty("detected_language", out var detLang) &&
                        detLang.ValueKind != JsonValueKind.Null)
                    {
                        result.DetectedLanguage = detLang.GetString();
                        LastDetectedLanguage = result.DetectedLanguage;

                        var langName = root.TryGetProperty("detected_language_name", out var ln)
                            ? ln.GetString() : result.DetectedLanguage;

                        OnLanguageDetected?.Invoke(result.DetectedLanguage!, langName ?? "");
                    }

                    _logger.LogInformation(
                        "Translation ({Time:F0}ms){Lang}: \"{Source}\" -> \"{Target}\"",
                        result.ProcessingTimeMs,
                        result.DetectedLanguage != null ? $" [{result.DetectedLanguage}]" : "",
                        result.SourceText.Length > 50 ? result.SourceText[..50] + "..." : result.SourceText,
                        result.TranslatedText.Length > 50 ? result.TranslatedText[..50] + "..." : result.TranslatedText);

                    if (OnTranslationReceived != null)
                    {
                        await OnTranslationReceived(result);
                    }
                    break;

                case "skipped":
                    var skipped = new SkippedResult
                    {
                        Reason = root.GetProperty("reason").GetString() ?? "unknown",
                        DetectedLanguage = root.TryGetProperty("detected_language", out var skipLang)
                            ? skipLang.GetString() : null,
                        DetectedLanguageName = root.TryGetProperty("detected_language_name", out var skipLangName)
                            ? skipLangName.GetString() : null
                    };

                    if (skipped.DetectedLanguage != null)
                    {
                        LastDetectedLanguage = skipped.DetectedLanguage;
                        OnLanguageDetected?.Invoke(skipped.DetectedLanguage, skipped.DetectedLanguageName ?? "");
                    }

                    _logger.LogInformation("Translation skipped: {Reason} (detected: {Lang})",
                        skipped.Reason, skipped.DetectedLanguageName ?? skipped.DetectedLanguage);

                    if (OnTranslationSkipped != null)
                    {
                        await OnTranslationSkipped(skipped);
                    }
                    break;

                case "language_set":
                    var setLang = root.GetProperty("source_lang").GetString();
                    _logger.LogInformation("Language manually set to: {Lang}", setLang);
                    break;

                case "auto_detect_enabled":
                    _logger.LogInformation("Auto-detect mode enabled");
                    break;

                case "voice_ref_set":
                    var voiceStatus = root.TryGetProperty("status", out var vs) ? vs.GetString() : "ok";
                    _logger.LogInformation("Voice reference acknowledged by server: {Status}", voiceStatus);
                    break;

                case "error":
                    var error = root.TryGetProperty("message", out var em) ? em.GetString() : "unknown";
                    _logger.LogWarning("Server error: {Error}", error);
                    break;

                case "pong":
                    if (root.TryGetProperty("last_detected_language", out var lastLang) &&
                        lastLang.ValueKind != JsonValueKind.Null)
                    {
                        LastDetectedLanguage = lastLang.GetString();
                    }
                    break;

                default:
                    _logger.LogDebug("Unhandled message type: {Type}", msgType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing server message: {Json}", json);
        }
    }

    private async Task HandleBinaryMessageAsync(byte[] data)
    {
        _logger.LogDebug("Received translated audio: {Bytes} bytes", data.Length);

        if (OnAudioReceived != null)
        {
            await OnAudioReceived(data);
        }
    }

    public async Task DisconnectAsync()
    {
        _receiveCts?.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                // Disable streaming before closing
                if (IsStreamingMode)
                {
                    await SetStreamingModeAsync(false);
                }

                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnecting",
                    CancellationToken.None);
            }
            catch { }
        }

        _isConnected = false;
        IsStreamingMode = false;
        OnConnectionChanged?.Invoke(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _webSocket?.Dispose();
        _sendLock.Dispose();
        _receiveCts?.Dispose();
    }
}

/// <summary>
/// Translation result from Gemini server
/// </summary>
public class TranslationResult
{
    public string SourceText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public double ProcessingTimeMs { get; set; }
    public int AudioSampleRate { get; set; } = 16000;
    public string? DetectedLanguage { get; set; }
}

/// <summary>
/// Result when translation is skipped
/// </summary>
public class SkippedResult
{
    public string Reason { get; set; } = "";
    public string? DetectedLanguage { get; set; }
    public string? DetectedLanguageName { get; set; }
}
