using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranslationBridge.Configuration;

namespace TranslationBridge.Services;

/// <summary>
/// Bidirectional translation worker with automatic language detection
/// and manual language selection support.
/// 
/// INBOUND CALLS (client calls operator):
/// - Auto-detect mode active
/// - System detects client language automatically
/// 
/// OUTBOUND CALLS (operator calls client):
/// - Operator selects language from tray menu BEFORE calling
/// - Translation uses selected language
/// </summary>
public class TranslationWorker : BackgroundService
{
    private readonly ILogger<TranslationWorker> _logger;
    private readonly BridgeConfig _config;
    private readonly AudioBridge _audioBridge;
    
    // Two WebSocket clients for bidirectional translation
    private GeminiClient? _inboundClient;   // Remote lang -> Local lang
    private GeminiClient? _outboundClient;  // Local lang -> Remote lang
    
    // Language state
    private string _currentMode = "auto";  // "auto" or specific language code
    private string? _detectedRemoteLanguage;
    private string _currentOutboundTarget;
    private readonly object _languageLock = new();
    
    // Tray icon service for UI
    private TrayIconService? _trayService;

    // Translation active state (controlled by toggle)
    private bool _isTranslationActive = false;
    
    // VAD state for inbound (remote party speaking)
    private readonly List<byte> _inboundBuffer = new();
    private DateTime _inboundSpeechStart;
    private DateTime _inboundLastSpeech;
    private bool _inboundIsSpeaking;
    private readonly object _inboundLock = new();
    
    // VAD state for outbound (operator speaking)
    private readonly List<byte> _outboundBuffer = new();
    private DateTime _outboundSpeechStart;
    private DateTime _outboundLastSpeech;
    private bool _outboundIsSpeaking;
    private readonly object _outboundLock = new();

    // Call simulation
    private FileAudioInjector? _fileAudioInjector;

    public TranslationWorker(
        ILogger<TranslationWorker> logger,
        IOptions<BridgeConfig> config,
        AudioBridge audioBridge)
    {
        _logger = logger;
        _config = config.Value;
        _audioBridge = audioBridge;
        _currentOutboundTarget = _config.Languages.ExpectedLanguages.FirstOrDefault() ?? "en";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║  3CX Translation Bridge - Multi-Language Support          ║");
        _logger.LogInformation("╠════════════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  CHIAMATE IN ENTRATA: Auto-detect attivo                  ║");
        _logger.LogInformation("║  CHIAMATE IN USCITA:  Seleziona lingua dal menu tray     ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        _logger.LogInformation("");
        _logger.LogInformation("Lingua operatore: {Local}", _config.Languages.LocalLanguage.ToUpper());
        _logger.LogInformation("Lingue supportate: {Langs}", 
            string.Join(", ", _config.Languages.ExpectedLanguages));
        _logger.LogInformation("");
        
        try
        {
            // Initialize tray icon service
            InitializeTrayService();
            
            // Initialize audio bridge
            _audioBridge.Initialize();
            _audioBridge.OnInboundAudioCaptured += OnInboundAudioCaptured;
            _audioBridge.OnOutboundAudioCaptured += OnOutboundAudioCaptured;

            // Create and connect both WebSocket clients
            var loggerFactory = LoggerFactory.Create(builder => 
                builder.AddConsole().SetMinimumLevel(LogLevel.Information));
            
            _inboundClient = new GeminiClient(
                loggerFactory.CreateLogger<GeminiClient>(),
                Options.Create(_config));

            _outboundClient = new GeminiClient(
                loggerFactory.CreateLogger<GeminiClient>(),
                Options.Create(_config));

            // Connect inbound client (Remote → Local)
            _logger.LogInformation("Connecting INBOUND translator...");
            await ConnectWithRetryAsync(_inboundClient, stoppingToken);
            await _inboundClient.ConfigureLanguagesAsync(
                _currentMode == "auto" ? "auto" : _currentMode,
                _config.Languages.LocalLanguage,
                stoppingToken);

            // Enable streaming mode for real-time translation
            await _inboundClient.SetStreamingModeAsync(true, stoppingToken);

            _inboundClient.OnAudioReceived += OnInboundTranslatedAudio;
            _inboundClient.OnTranslationSkipped += OnInboundTranslationSkipped;
            _inboundClient.OnLanguageDetected += OnRemoteLanguageDetected;
            _inboundClient.OnStreamingTranslatedText += text =>
                _logger.LogDebug("[INBOUND] Streaming: {Text}", text);

            // Connect outbound client (Local → Remote)
            _logger.LogInformation("Connecting OUTBOUND translator...");
            await ConnectWithRetryAsync(_outboundClient, stoppingToken);
            await _outboundClient.ConfigureLanguagesAsync(
                _config.Languages.LocalLanguage,
                _currentOutboundTarget,
                stoppingToken);

            // Enable streaming mode for real-time translation
            await _outboundClient.SetStreamingModeAsync(true, stoppingToken);

            _outboundClient.OnAudioReceived += OnOutboundTranslatedAudio;
            _outboundClient.OnTranslationSkipped += OnOutboundTranslationSkipped;
            _outboundClient.OnStreamingTranslatedText += text =>
                _logger.LogDebug("[OUTBOUND] Streaming: {Text}", text);

            // Send voice reference if configured
            await SendVoiceReferenceAsync(stoppingToken);

            // DO NOT start audio automatically - wait for toggle
            // _audioBridge.Start();

            _logger.LogInformation("");
            _logger.LogInformation("════════════════════════════════════════════════════════════");
            _logger.LogInformation("  ⏸ Traduzione IN PAUSA");
            _logger.LogInformation("  → Double-click o menu tray per AVVIARE");
            _logger.LogInformation("════════════════════════════════════════════════════════════");
            _logger.LogInformation("");

            // Main loop
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckInboundSilenceAsync(stoppingToken);
                await CheckOutboundSilenceAsync(stoppingToken);
                await Task.Delay(50, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in translation worker");
            throw;
        }
        finally
        {
            _audioBridge.Stop();
            _trayService?.Dispose();
            if (_inboundClient != null) await _inboundClient.DisconnectAsync();
            if (_outboundClient != null) await _outboundClient.DisconnectAsync();
        }
    }

    private void InitializeTrayService()
    {
        try
        {
            _trayService = new TrayIconService(
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<TrayIconService>(),
                Options.Create(_config),
                this);
            
            _trayService.OnLanguageSelected += OnManualLanguageSelected;
            _trayService.OnAutoDetectEnabled += OnAutoDetectEnabled;
            _trayService.OnTranslationToggled += OnTranslationToggled;
            _trayService.OnVoiceChanged += OnVoiceChanged;
            _trayService.OnTestTranslationRequested += OnTestTranslationRequested;
            _trayService.OnSimulateCallRequested += OnSimulateCallRequested;
            _trayService.OnStopSimulationRequested += OnStopSimulationRequested;
            _trayService.Initialize();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize tray icon (running as service?)");
        }
    }

    private void OnManualLanguageSelected(string langCode)
    {
        lock (_languageLock)
        {
            var wasAutoDetect = _currentMode == "auto";
            _currentMode = langCode;
            _detectedRemoteLanguage = langCode;
            _currentOutboundTarget = langCode;
            
            // Mark as overridden if we were in auto-detect with a detected language
            var isOverride = wasAutoDetect && _trayService?.IsOverridden == true;
            
            _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
            if (isOverride)
            {
                _logger.LogInformation("║  ⚠️  OVERRIDE MANUALE: {Lang,-35}  ║", langCode.ToUpper());
                _logger.LogInformation("║  La lingua rilevata è stata corretta dall'operatore     ║");
            }
            else
            {
                _logger.LogInformation("║  LINGUA MANUALE: {Lang,-38}  ║", langCode.ToUpper());
                _logger.LogInformation("║  Pronto per chiamata in uscita                          ║");
            }
            _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");
        }
        
        // Reconfigure both clients with the selected language
        Task.Run(async () =>
        {
            try
            {
                // For inbound: set explicit source language (no more auto-detect)
                if (_inboundClient != null)
                {
                    await _inboundClient.SetLanguageAsync(langCode);
                    _logger.LogDebug("Inbound client updated: {Lang} → {Local}", 
                        langCode, _config.Languages.LocalLanguage);
                }
                
                // For outbound: set target language
                if (_outboundClient != null)
                {
                    await _outboundClient.ConfigureLanguagesAsync(
                        _config.Languages.LocalLanguage,
                        langCode,
                        CancellationToken.None);
                    _logger.LogDebug("Outbound client updated: {Local} → {Lang}", 
                        _config.Languages.LocalLanguage, langCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconfiguring clients after language selection");
            }
        });
    }

    private void OnTranslationToggled(bool isActive)
    {
        _isTranslationActive = isActive;

        if (isActive)
        {
            _logger.LogInformation("═══════════════════════════════════════════════════════════");
            _logger.LogInformation("  ▶ TRADUZIONE ATTIVATA");
            _logger.LogInformation("═══════════════════════════════════════════════════════════");
            _audioBridge.Start();
        }
        else
        {
            _logger.LogInformation("═══════════════════════════════════════════════════════════");
            _logger.LogInformation("  ⏸ TRADUZIONE IN PAUSA");
            _logger.LogInformation("═══════════════════════════════════════════════════════════");
            _audioBridge.Stop();

            // Clear any pending buffers
            lock (_inboundLock)
            {
                _inboundBuffer.Clear();
                _inboundIsSpeaking = false;
            }
            lock (_outboundLock)
            {
                _outboundBuffer.Clear();
                _outboundIsSpeaking = false;
            }
        }
    }

    private void OnVoiceChanged(string? voicePath)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        if (string.IsNullOrEmpty(voicePath))
        {
            _logger.LogInformation("  🔇 Voice cloning DISATTIVATO");
            _logger.LogInformation("  → Verrà usata la voce default del server");
        }
        else
        {
            _logger.LogInformation("  🎤 Voice cloning ATTIVATO");
            _logger.LogInformation("  → File: {Path}", Path.GetFileName(voicePath));
        }
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        // Send the voice reference to both clients
        Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrEmpty(voicePath))
                {
                    if (_inboundClient != null)
                        await _inboundClient.ClearVoiceReferenceAsync();
                    if (_outboundClient != null)
                        await _outboundClient.ClearVoiceReferenceAsync();
                }
                else
                {
                    if (_inboundClient != null)
                        await _inboundClient.SetVoiceReferenceAsync(voicePath);
                    if (_outboundClient != null)
                        await _outboundClient.SetVoiceReferenceAsync(voicePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating voice reference on server");
            }
        });
    }

    private async Task SendVoiceReferenceAsync(CancellationToken cancellationToken)
    {
        // Check if voice cloning is configured
        var voicePath = _trayService?.GetVoiceReferencePath();
        if (string.IsNullOrEmpty(voicePath))
        {
            // Also check config
            if (_config.Voice.Enabled && !string.IsNullOrEmpty(_config.Voice.ReferencePath))
            {
                voicePath = _config.Voice.ReferencePath;
            }
        }

        if (!string.IsNullOrEmpty(voicePath) && File.Exists(voicePath))
        {
            _logger.LogInformation("Sending voice reference to servers...");
            _logger.LogInformation("  File: {Path}", Path.GetFileName(voicePath));

            if (_inboundClient != null)
                await _inboundClient.SetVoiceReferenceAsync(voicePath, cancellationToken);
            if (_outboundClient != null)
                await _outboundClient.SetVoiceReferenceAsync(voicePath, cancellationToken);
        }
    }

    private void OnTestTranslationRequested()
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("  🧪 Test Traduzione richiesto");
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        // Use inbound client for testing (it has auto-detect configured)
        var client = _inboundClient;
        if (client == null || !client.IsConnected)
        {
            _logger.LogWarning("Client non connesso. Impossibile eseguire il test.");
            return;
        }

        // Open dialog on UI thread
        Task.Run(() =>
        {
            try
            {
                var thread = new Thread(() =>
                {
                    Application.EnableVisualStyles();
                    using var dialog = new TestTranslationDialog(client);
                    dialog.ShowDialog();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error opening test translation dialog");
            }
        });
    }

    private void OnSimulateCallRequested(string filePath)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("  📞 Simulazione Chiamata avviata");
        _logger.LogInformation("  File: {Path}", filePath);
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        try
        {
            // Stop existing simulation if running
            _fileAudioInjector?.Stop();
            _fileAudioInjector?.Dispose();

            // Create new injector
            _fileAudioInjector = new FileAudioInjector(
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<FileAudioInjector>(),
                _audioBridge);

            // Subscribe to events
            _fileAudioInjector.OnPlaybackComplete += () =>
            {
                _logger.LogInformation("═══════════════════════════════════════════════════════════");
                _logger.LogInformation("  📞 Simulazione completata");
                _logger.LogInformation("═══════════════════════════════════════════════════════════");
                _trayService?.UpdateSimulationState(false, null);
            };

            _fileAudioInjector.OnProgressChanged += (position, duration) =>
            {
                _logger.LogDebug("Simulation progress: {Position:mm\\:ss} / {Duration:mm\\:ss}",
                    position, duration);
            };

            // Load and start
            if (_fileAudioInjector.LoadFile(filePath))
            {
                _fileAudioInjector.Start();
                _logger.LogInformation("Audio injection started. Duration: {Duration:mm\\:ss}",
                    _fileAudioInjector.Duration);
            }
            else
            {
                _logger.LogError("Failed to load audio file: {Path}", filePath);
                _trayService?.UpdateSimulationState(false, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting call simulation");
            _trayService?.UpdateSimulationState(false, null);
        }
    }

    private void OnStopSimulationRequested()
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("  ⏹ Simulazione Chiamata fermata");
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        try
        {
            _fileAudioInjector?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping call simulation");
        }
    }

    private void OnAutoDetectEnabled()
    {
        lock (_languageLock)
        {
            _currentMode = "auto";
            _detectedRemoteLanguage = null;
        }

        _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║  🔍 AUTO-DETECT ATTIVO                                    ║");
        _logger.LogInformation("║  Il sistema rileverà automaticamente la lingua           ║");
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");
        
        // Reconfigure inbound client for auto-detect
        Task.Run(async () =>
        {
            try
            {
                if (_inboundClient != null)
                {
                    await _inboundClient.EnableAutoDetectAsync();
                    _logger.LogDebug("Inbound client: auto-detect enabled");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enabling auto-detect");
            }
        });
    }

    private void OnRemoteLanguageDetected(string langCode, string langName)
    {
        // Check if override is active
        if (_trayService?.IsOverridden == true)
        {
            _logger.LogDebug("Ignoring detected language {Lang} - manual override active", langCode);
            return;
        }
        
        bool shouldUpdate;
        lock (_languageLock)
        {
            shouldUpdate = _currentMode == "auto" && _detectedRemoteLanguage != langCode;
            if (shouldUpdate)
            {
                _detectedRemoteLanguage = langCode;
                _currentOutboundTarget = langCode;
            }
        }
        
        if (shouldUpdate)
        {
            _logger.LogInformation("╔═══════════════════════════════════════════════════════════╗");
            _logger.LogInformation("║  🌐 LINGUA RILEVATA: {Lang,-34}  ║", langName.ToUpper());
            _logger.LogInformation("║  💡 Click destro sull'icona per correggere se errata     ║");
            _logger.LogInformation("╚═══════════════════════════════════════════════════════════╝");
            
            // Update tray icon
            _trayService?.UpdateDetectedLanguage(langCode, langName);
            
            // Update outbound client
            if (langCode != _config.Languages.LocalLanguage)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        if (_outboundClient != null)
                        {
                            await _outboundClient.ConfigureLanguagesAsync(
                                _config.Languages.LocalLanguage,
                                langCode,
                                CancellationToken.None);
                            _logger.LogDebug("Outbound updated: {Local} → {Remote}",
                                _config.Languages.LocalLanguage, langCode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error updating outbound client");
                    }
                });
            }
        }
    }

    private async Task ConnectWithRetryAsync(GeminiClient client, CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var maxRetries = 10;
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                attempt++;
                await client.ConnectAsync(cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= maxRetries)
                {
                    _logger.LogError(ex, "Failed to connect after {Attempts} attempts", attempt);
                    throw;
                }

                _logger.LogWarning(
                    "Connection attempt {Attempt}/{Max} failed: {Error}. Retrying...",
                    attempt, maxRetries, ex.Message);
                
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 1.5, 30));
            }
        }
    }

    // ============================================================
    // INBOUND: Remote Party → Operator
    // ============================================================

    private void OnInboundAudioCaptured(byte[] audioData)
    {
        // Skip if translation is paused
        if (!_isTranslationActive) return;

        var energy = CalculateEnergy(audioData);
        var isSpeech = energy > _config.Vad.Threshold;

        lock (_inboundLock)
        {
            if (isSpeech)
            {
                if (!_inboundIsSpeaking)
                {
                    _inboundIsSpeaking = true;
                    _inboundSpeechStart = DateTime.UtcNow;
                    _logger.LogDebug("[INBOUND] Remote party started speaking");
                }
                
                _inboundLastSpeech = DateTime.UtcNow;
                _inboundBuffer.AddRange(audioData);
                _ = _inboundClient?.SendAudioChunkAsync(audioData);
            }
            else if (_inboundIsSpeaking)
            {
                _inboundBuffer.AddRange(audioData);
                _ = _inboundClient?.SendAudioChunkAsync(audioData);
            }
        }
    }

    private async Task CheckInboundSilenceAsync(CancellationToken cancellationToken)
    {
        bool shouldTranslate = false;

        lock (_inboundLock)
        {
            if (!_inboundIsSpeaking || _inboundBuffer.Count == 0) return;

            var silenceDuration = (DateTime.UtcNow - _inboundLastSpeech).TotalMilliseconds;
            var speechDuration = (DateTime.UtcNow - _inboundSpeechStart).TotalMilliseconds;

            if (silenceDuration >= _config.Vad.SilenceDurationMs ||
                speechDuration >= _config.Vad.MaxSpeechDurationMs)
            {
                if (speechDuration >= _config.Vad.MinSpeechDurationMs)
                {
                    shouldTranslate = true;
                }
                _inboundBuffer.Clear();
                _inboundIsSpeaking = false;
            }
        }

        if (shouldTranslate && _inboundClient != null)
        {
            _logger.LogDebug("[INBOUND] Requesting translation");
            await _inboundClient.RequestTranslationAsync(cancellationToken);
        }
    }

    private Task OnInboundTranslatedAudio(byte[] audioData)
    {
        _logger.LogDebug("[INBOUND] Playing translated audio: {Bytes} bytes", audioData.Length);
        _audioBridge.PlayInboundAudio(audioData);
        return Task.CompletedTask;
    }

    private Task OnInboundTranslationSkipped(SkippedResult result)
    {
        _logger.LogInformation("[INBOUND] Skipped - {Lang}",
            result.DetectedLanguageName ?? result.DetectedLanguage ?? "same language");
        return Task.CompletedTask;
    }

    // ============================================================
    // OUTBOUND: Operator → Remote Party
    // ============================================================

    private void OnOutboundAudioCaptured(byte[] audioData)
    {
        // Skip if translation is paused
        if (!_isTranslationActive) return;

        var energy = CalculateEnergy(audioData);
        var isSpeech = energy > _config.Vad.Threshold;

        lock (_outboundLock)
        {
            if (isSpeech)
            {
                if (!_outboundIsSpeaking)
                {
                    _outboundIsSpeaking = true;
                    _outboundSpeechStart = DateTime.UtcNow;
                    _logger.LogDebug("[OUTBOUND] Operator started speaking");
                }
                
                _outboundLastSpeech = DateTime.UtcNow;
                _outboundBuffer.AddRange(audioData);
                _ = _outboundClient?.SendAudioChunkAsync(audioData);
            }
            else if (_outboundIsSpeaking)
            {
                _outboundBuffer.AddRange(audioData);
                _ = _outboundClient?.SendAudioChunkAsync(audioData);
            }
        }
    }

    private async Task CheckOutboundSilenceAsync(CancellationToken cancellationToken)
    {
        bool shouldTranslate = false;
        byte[]? rawAudio = null;
        string? targetLang;

        lock (_outboundLock)
        {
            if (!_outboundIsSpeaking || _outboundBuffer.Count == 0) return;

            var silenceDuration = (DateTime.UtcNow - _outboundLastSpeech).TotalMilliseconds;
            var speechDuration = (DateTime.UtcNow - _outboundSpeechStart).TotalMilliseconds;

            if (silenceDuration >= _config.Vad.SilenceDurationMs ||
                speechDuration >= _config.Vad.MaxSpeechDurationMs)
            {
                if (speechDuration >= _config.Vad.MinSpeechDurationMs)
                {
                    shouldTranslate = true;
                    rawAudio = _outboundBuffer.ToArray();
                }
                _outboundBuffer.Clear();
                _outboundIsSpeaking = false;
            }
        }

        lock (_languageLock)
        {
            targetLang = _detectedRemoteLanguage ?? _currentOutboundTarget;
        }

        if (shouldTranslate && _outboundClient != null)
        {
            // Skip if target is same as local language
            if (targetLang == _config.Languages.LocalLanguage && _config.Languages.SkipSameLanguage)
            {
                _logger.LogDebug("[OUTBOUND] Skipping - same language");
                if (rawAudio != null)
                {
                    _audioBridge.PlayOutboundAudio(rawAudio);
                }
                return;
            }
            
            _logger.LogDebug("[OUTBOUND] Requesting translation → {Lang}", targetLang);
            await _outboundClient.RequestTranslationAsync(cancellationToken);
        }
    }

    private Task OnOutboundTranslatedAudio(byte[] audioData)
    {
        _logger.LogDebug("[OUTBOUND] Sending translated audio: {Bytes} bytes", audioData.Length);
        _audioBridge.PlayOutboundAudio(audioData);
        return Task.CompletedTask;
    }

    private Task OnOutboundTranslationSkipped(SkippedResult result)
    {
        _logger.LogInformation("[OUTBOUND] Skipped - {Reason}", result.Reason);
        return Task.CompletedTask;
    }

    // ============================================================
    // Utilities
    // ============================================================

    private static float CalculateEnergy(byte[] audioData)
    {
        if (audioData.Length < 2) return 0;

        double sum = 0;
        var sampleCount = audioData.Length / 2;

        for (int i = 0; i < audioData.Length - 1; i += 2)
        {
            var sample = (short)(audioData[i] | (audioData[i + 1] << 8));
            var normalized = sample / 32768.0;
            sum += normalized * normalized;
        }

        return (float)Math.Sqrt(sum / sampleCount);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping translation worker...");
        
        _audioBridge.OnInboundAudioCaptured -= OnInboundAudioCaptured;
        _audioBridge.OnOutboundAudioCaptured -= OnOutboundAudioCaptured;
        
        if (_trayService != null)
        {
            _trayService.OnLanguageSelected -= OnManualLanguageSelected;
            _trayService.OnAutoDetectEnabled -= OnAutoDetectEnabled;
            _trayService.OnTranslationToggled -= OnTranslationToggled;
            _trayService.OnVoiceChanged -= OnVoiceChanged;
            _trayService.OnTestTranslationRequested -= OnTestTranslationRequested;
        }
        
        if (_inboundClient != null)
        {
            _inboundClient.OnAudioReceived -= OnInboundTranslatedAudio;
            _inboundClient.OnTranslationSkipped -= OnInboundTranslationSkipped;
            _inboundClient.OnLanguageDetected -= OnRemoteLanguageDetected;
        }
        if (_outboundClient != null)
        {
            _outboundClient.OnAudioReceived -= OnOutboundTranslatedAudio;
            _outboundClient.OnTranslationSkipped -= OnOutboundTranslationSkipped;
        }
        
        await base.StopAsync(cancellationToken);
    }
}
