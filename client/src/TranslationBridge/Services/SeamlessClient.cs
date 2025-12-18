using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranslationBridge.Configuration;

namespace TranslationBridge.Services;

/// <summary>
/// WebSocket client for SeamlessM4T translation server
/// Supports automatic language detection
/// </summary>
public class SeamlessClient : IAsyncDisposable
{
    private readonly ILogger<SeamlessClient> _logger;
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

    public bool IsConnected => _isConnected && _webSocket?.State == WebSocketState.Open;
    
    /// <summary>
    /// Last detected language code
    /// </summary>
    public string? LastDetectedLanguage { get; private set; }

    public SeamlessClient(
        ILogger<SeamlessClient> logger,
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
        
        var uri = new Uri(_config.ServerUrl);
        _logger.LogInformation("Connecting to SeamlessM4T server at {Url}...", uri);

        try
        {
            await _webSocket.ConnectAsync(uri, cancellationToken);
            _isConnected = true;
            OnConnectionChanged?.Invoke(true);
            
            _logger.LogInformation("Connected to SeamlessM4T server");

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
            _logger.LogError(ex, "Failed to connect to SeamlessM4T server");
            throw;
        }
    }

    public async Task ConfigureLanguagesAsync(
        string sourceLanguage, 
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var config = new
        {
            type = "config",
            source_lang = sourceLanguage,  // Can be "auto"
            target_lang = targetLanguage
        };

        await SendJsonAsync(config, cancellationToken);
        
        var modeStr = sourceLanguage == "auto" ? "auto-detect" : sourceLanguage;
        _logger.LogInformation("Configured translation: {Source} → {Target}", modeStr, targetLanguage);
    }

    public async Task SetLanguageAsync(string sourceLanguage, CancellationToken cancellationToken = default)
    {
        var msg = new
        {
            type = "set_language",
            source_lang = sourceLanguage
        };
        
        await SendJsonAsync(msg, cancellationToken);
        _logger.LogInformation("Manual language override: {Lang}", sourceLanguage);
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
                    var autoDetect = root.TryGetProperty("auto_detect", out var ad) && ad.GetBoolean();
                    _logger.LogDebug("Server welcome received (auto_detect: {AutoDetect})", autoDetect);
                    break;

                case "config_ack":
                    var srcLang = root.GetProperty("source_lang").GetString();
                    var tgtLang = root.GetProperty("target_lang").GetString();
                    var isAuto = root.TryGetProperty("auto_detect", out var autoEl) && autoEl.GetBoolean();
                    _logger.LogDebug("Config acknowledged: {Src} → {Tgt} (auto: {Auto})", 
                        srcLang, tgtLang, isAuto);
                    break;

                case "result":
                    var result = new TranslationResult
                    {
                        SourceText = root.GetProperty("source_text").GetString() ?? "",
                        TranslatedText = root.GetProperty("translated_text").GetString() ?? "",
                        ProcessingTimeMs = root.GetProperty("processing_time_ms").GetDouble(),
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
                        "Translation ({Time:F0}ms){Lang}: \"{Source}\" → \"{Target}\"",
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
                    var error = root.GetProperty("message").GetString();
                    _logger.LogWarning("Server error: {Error}", error);
                    break;

                case "pong":
                    if (root.TryGetProperty("last_detected_language", out var lastLang) &&
                        lastLang.ValueKind != JsonValueKind.Null)
                    {
                        LastDetectedLanguage = lastLang.GetString();
                    }
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
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnecting",
                    CancellationToken.None);
            }
            catch { }
        }

        _isConnected = false;
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
/// Translation result from SeamlessM4T server
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
