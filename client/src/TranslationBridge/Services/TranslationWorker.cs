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
    
    // ============================================================
    // Turn State Machine (3 stati come HTML client)
    // ============================================================
    private enum TurnState { IDLE, ACTIVE, WAIT_COMPLETE }

    // VAD state for inbound (remote party speaking)
    private readonly List<byte> _inboundBuffer = new();
    private DateTime _inboundSpeechStart;
    private DateTime _inboundLastSpeech;
    private bool _inboundIsSpeaking;
    private readonly object _inboundLock = new();
    private TurnState _inboundTurnState = TurnState.IDLE;
    private byte[]? _inboundOverlapTail = null;           // Last 250ms for overlap
    private readonly List<byte[]> _inboundPendingChunks = new();  // Buffer during WAIT_COMPLETE
    private int _inboundPendingBytes = 0;
    private float _inboundLastRms = 0;                    // For auto-restart check
    private float _inboundPeakRmsDuringWait = 0;          // Peak RMS during WAIT_COMPLETE

    // VAD state for outbound (operator speaking)
    private readonly List<byte> _outboundBuffer = new();
    private DateTime _outboundSpeechStart;
    private DateTime _outboundLastSpeech;
    private bool _outboundIsSpeaking;
    private readonly object _outboundLock = new();
    private TurnState _outboundTurnState = TurnState.IDLE;
    private byte[]? _outboundOverlapTail = null;          // Last 250ms for overlap
    private readonly List<byte[]> _outboundPendingChunks = new();  // Buffer during WAIT_COMPLETE
    private int _outboundPendingBytes = 0;
    private float _outboundLastRms = 0;                   // For auto-restart check
    private float _outboundPeakRmsDuringWait = 0;         // Peak RMS during WAIT_COMPLETE

    // Call simulation
    private FileAudioInjector? _fileAudioInjector;
    private bool _isSimulating = false;  // When true, bypass VAD and send audio directly
    private int _simulationChunksSent = 0;
    private System.Threading.Timer? _turnTimer;  // Timer for periodic activity_end signals
    private const int TurnIntervalMs = 5000;  // Send activity_end every 5 seconds

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
            _inboundClient.OnTurnComplete += OnInboundTurnComplete;
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
            _outboundClient.OnTurnComplete += OnOutboundTurnComplete;
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
            _trayService.OnVadSettingsOpened += OnVadSettingsOpened;
            _trayService.OnVadSettingsChanged += OnVadSettingsChanged;
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

    /// <summary>
    /// Provide callbacks for live RMS and turn state display in VAD settings dialog
    /// </summary>
    private (Func<float> GetRms, Func<string> GetTurnState) OnVadSettingsOpened()
    {
        return (
            () => _inboundLastRms,
            () => _inboundTurnState.ToString()
        );
    }

    /// <summary>
    /// Handle VAD settings changes from the dialog
    /// Settings are already modified in the VadConfig object, just log the change
    /// </summary>
    private void OnVadSettingsChanged(VadConfig vadConfig)
    {
        _logger.LogInformation("VAD settings updated:");
        _logger.LogInformation("  SpeechThreshold: {Value:F3}", vadConfig.SpeechThreshold);
        _logger.LogInformation("  SilenceThreshold: {Value:F3}", vadConfig.SilenceThreshold);
        _logger.LogInformation("  AutoRestartThreshold: {Value:F3}", vadConfig.AutoRestartThreshold);
        _logger.LogInformation("  SilenceDurationMs: {Value}ms", vadConfig.SilenceDurationMs);
        _logger.LogInformation("  MinTurnDurationMs: {Value}ms", vadConfig.MinTurnDurationMs);
        _logger.LogInformation("  MaxTurnMs: {Value}ms", vadConfig.MaxTurnMs);
        _logger.LogInformation("  OverlapMs: {Value}ms", vadConfig.OverlapMs);
        _logger.LogInformation("  PendingMaxBytes: {Value}KB", vadConfig.PendingMaxBytes / 1024);
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

    private async void OnSimulateCallRequested(string filePath)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("  📞 Simulazione Chiamata avviata (Manual VAD)");
        _logger.LogInformation("  File: {Path}", filePath);
        _logger.LogInformation("  Turn interval: {Interval}ms", TurnIntervalMs);
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        try
        {
            // Stop existing simulation if running
            StopTurnTimer();
            _fileAudioInjector?.Stop();
            _fileAudioInjector?.Dispose();

            // Create new injector
            _fileAudioInjector = new FileAudioInjector(
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<FileAudioInjector>(),
                _audioBridge);

            // Enable simulation mode - bypasses VAD
            _isSimulating = true;
            _simulationChunksSent = 0;

            // Subscribe to events
            _fileAudioInjector.OnPlaybackComplete += async () =>
            {
                _logger.LogInformation("═══════════════════════════════════════════════════════════");
                _logger.LogInformation("  📞 Simulazione completata - Chunks totali: {Count}", _simulationChunksSent);
                _logger.LogInformation("═══════════════════════════════════════════════════════════");

                // Stop the turn timer
                StopTurnTimer();

                // Disable simulation mode
                _isSimulating = false;

                // Send final activity_end to trigger last translation
                _logger.LogInformation("  📤 Sending final activity_end (Manual VAD)...");
                try
                {
                    if (_inboundClient != null && _inboundClient.IsStreamingMode)
                    {
                        await _inboundClient.SendActivityEndAsync();
                        _logger.LogInformation("  ✓ Final activity_end sent - waiting for translation");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send final activity_end");
                }

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
                // Manual VAD: Send activity_start before audio
                _logger.LogInformation("  📤 Sending activity_start (Manual VAD)...");
                if (_inboundClient != null && _inboundClient.IsStreamingMode)
                {
                    await _inboundClient.SendActivityStartAsync();
                }

                // Start periodic turn timer - sends activity_end → activity_start every N seconds
                StartTurnTimer();

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

    private void StartTurnTimer()
    {
        StopTurnTimer();
        _turnTimer = new System.Threading.Timer(
            OnTurnTimerElapsed,
            null,
            TurnIntervalMs,  // First tick after TurnIntervalMs
            TurnIntervalMs); // Then every TurnIntervalMs
        _logger.LogDebug("Turn timer started ({Interval}ms interval)", TurnIntervalMs);
    }

    private void StopTurnTimer()
    {
        _turnTimer?.Dispose();
        _turnTimer = null;
    }

    private async void OnTurnTimerElapsed(object? state)
    {
        if (!_isSimulating || _inboundClient == null || !_inboundClient.IsStreamingMode)
        {
            return;
        }

        try
        {
            // During simulation with Manual VAD, manage the turn state properly
            lock (_inboundLock)
            {
                if (_inboundTurnState == TurnState.IDLE)
                {
                    // No active turn - need to start one first
                    _logger.LogInformation("  🔄 Turn timer: starting new turn (activity_start)");
                    _ = _inboundClient.SendActivityStartAsync();
                    _inboundTurnState = TurnState.ACTIVE;
                    _inboundSpeechStart = DateTime.UtcNow;
                    _inboundLastSpeech = DateTime.UtcNow;
                    return;  // Let audio flow, end the turn on next timer tick
                }

                if (_inboundTurnState == TurnState.WAIT_COMPLETE)
                {
                    // Already waiting for turn_complete, don't send another activity_end
                    _logger.LogDebug("  🔄 Turn timer: already waiting for turn_complete, skipping");
                    return;
                }
            }

            _logger.LogInformation("  🔄 Turn timer: sending activity_end to trigger translation");

            // Send activity_end to trigger translation of buffered audio
            await _inboundClient.SendActivityEndAsync();

            lock (_inboundLock)
            {
                _inboundTurnState = TurnState.WAIT_COMPLETE;
                _inboundPeakRmsDuringWait = 0;
            }

            _logger.LogInformation("  ✓ activity_end sent - waiting for turn_complete");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in turn timer callback");
        }
    }

    private void OnStopSimulationRequested()
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("  ⏹ Simulazione Chiamata fermata");
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        // Stop the turn timer
        StopTurnTimer();

        // Disable simulation mode
        _isSimulating = false;

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
        // Skip if client not connected or not in streaming mode
        if (_inboundClient == null || !_inboundClient.IsConnected || !_inboundClient.IsStreamingMode) return;

        // During simulation, bypass VAD but still track energy for auto-restart logic
        if (_isSimulating)
        {
            var simEnergy = CalculateEnergy(audioData);
            _inboundLastRms = simEnergy;  // Update for auto-restart check after turn_complete

            // Also update peak during WAIT_COMPLETE for auto-restart decision
            if (_inboundTurnState == TurnState.WAIT_COMPLETE && simEnergy > _inboundPeakRmsDuringWait)
                _inboundPeakRmsDuringWait = simEnergy;

            _ = _inboundClient.SendAudioChunkAsync(audioData);
            _simulationChunksSent++;
            if (_simulationChunksSent % 40 == 0)
            {
                _logger.LogInformation("[SIMULATION] Chunks sent: {Count}, RMS: {Rms:F4}, State: {State}",
                    _simulationChunksSent, simEnergy, _inboundTurnState);
            }
            return;
        }

        // Skip normal processing if translation is paused (but simulation above still works)
        if (!_isTranslationActive) return;

        var energy = CalculateEnergy(audioData);
        _inboundLastRms = energy;  // Store for auto-restart check

        lock (_inboundLock)
        {
            var now = DateTime.UtcNow;

            // ============================================================
            // 3-STATE MACHINE (same as HTML client)
            // ============================================================
            switch (_inboundTurnState)
            {
                case TurnState.IDLE:
                    if (energy > _config.Vad.SpeechThreshold)
                    {
                        _logger.LogInformation("[INBOUND] Speech detected (RMS: {Rms:F4}) - sending activity_start", energy);
                        _ = _inboundClient.SendActivityStartAsync();

                        // Send overlap from previous turn for lexical continuity
                        if (_inboundOverlapTail != null)
                        {
                            _logger.LogDebug("[INBOUND] Sending overlap: {Bytes} bytes", _inboundOverlapTail.Length);
                            _ = _inboundClient.SendAudioChunkAsync(_inboundOverlapTail);
                            _inboundOverlapTail = null;
                        }

                        _inboundTurnState = TurnState.ACTIVE;
                        _inboundSpeechStart = now;
                        _inboundLastSpeech = now;
                        _inboundIsSpeaking = true;

                        // CRITICAL: Also send THIS chunk - don't lose the first audio!
                        SaveOverlapBuffer(audioData, ref _inboundOverlapTail);
                        _ = _inboundClient.SendAudioChunkAsync(audioData);
                    }
                    else
                    {
                        return; // Don't send audio in IDLE state
                    }
                    break;

                case TurnState.ACTIVE:
                    var turnDuration = (now - _inboundSpeechStart).TotalMilliseconds;

                    // MAX_TURN as safety - only close if near-silence
                    if (turnDuration >= _config.Vad.MaxTurnMs &&
                        energy < _config.Vad.SilenceThreshold)
                    {
                        _logger.LogDebug("[INBOUND] Max turn {Duration}ms near-silence - sending activity_end", turnDuration);
                        SaveOverlapBuffer(audioData, ref _inboundOverlapTail);
                        _ = _inboundClient.SendActivityEndAsync();
                        _inboundTurnState = TurnState.WAIT_COMPLETE;
                        _inboundPeakRmsDuringWait = 0;  // Reset peak for new wait period
                        return;
                    }

                    // Preferred closure: on real silence
                    if (energy > _config.Vad.SilenceThreshold)
                    {
                        _inboundLastSpeech = now;
                    }
                    else
                    {
                        var silenceDuration = (now - _inboundLastSpeech).TotalMilliseconds;
                        if (silenceDuration >= _config.Vad.SilenceDurationMs &&
                            turnDuration >= _config.Vad.MinTurnDurationMs)
                        {
                            _logger.LogDebug("[INBOUND] Silence {Silence}ms (turn {Turn}ms) - sending activity_end",
                                silenceDuration, turnDuration);
                            SaveOverlapBuffer(audioData, ref _inboundOverlapTail);
                            _ = _inboundClient.SendActivityEndAsync();
                            _inboundTurnState = TurnState.WAIT_COMPLETE;
                            _inboundPeakRmsDuringWait = 0;  // Reset peak for new wait period
                            _inboundIsSpeaking = false;
                            return;
                        }
                    }

                    // Save overlap and send audio
                    SaveOverlapBuffer(audioData, ref _inboundOverlapTail);
                    _ = _inboundClient.SendAudioChunkAsync(audioData);
                    break;

                case TurnState.WAIT_COMPLETE:
                    // DON'T drop audio! Buffer it during WAIT_COMPLETE
                    // Track peak RMS for auto-restart decision
                    if (energy > _inboundPeakRmsDuringWait)
                        _inboundPeakRmsDuringWait = energy;
                    BufferPendingAudio(audioData, _inboundPendingChunks, ref _inboundPendingBytes, _config.Vad.PendingMaxBytes);
                    break;
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
            // In streaming mode, Gemini handles segmentation with its internal VAD
            // We should NOT send end_of_turn - let Gemini decide when to translate
            if (_inboundClient.IsStreamingMode)
            {
                // Let Gemini's internal VAD handle translation timing
                _logger.LogDebug("[INBOUND] Silence detected - Gemini VAD handles segmentation");
            }
            else
            {
                _logger.LogDebug("[INBOUND] Requesting translation (buffer mode)");
                await _inboundClient.RequestTranslationAsync(cancellationToken);
            }
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

        // Skip if client not connected or not in streaming mode
        if (_outboundClient == null || !_outboundClient.IsConnected || !_outboundClient.IsStreamingMode) return;

        var energy = CalculateEnergy(audioData);
        _outboundLastRms = energy;  // Store for auto-restart check

        lock (_outboundLock)
        {
            var now = DateTime.UtcNow;

            // ============================================================
            // 3-STATE MACHINE (same as HTML client)
            // ============================================================
            switch (_outboundTurnState)
            {
                case TurnState.IDLE:
                    if (energy > _config.Vad.SpeechThreshold)
                    {
                        _logger.LogInformation("[OUTBOUND] Speech detected (RMS: {Rms:F4}) - sending activity_start", energy);
                        _ = _outboundClient.SendActivityStartAsync();

                        // Send overlap from previous turn for lexical continuity
                        if (_outboundOverlapTail != null)
                        {
                            _logger.LogDebug("[OUTBOUND] Sending overlap: {Bytes} bytes", _outboundOverlapTail.Length);
                            _ = _outboundClient.SendAudioChunkAsync(_outboundOverlapTail);
                            _outboundOverlapTail = null;
                        }

                        _outboundTurnState = TurnState.ACTIVE;
                        _outboundSpeechStart = now;
                        _outboundLastSpeech = now;
                        _outboundIsSpeaking = true;

                        // CRITICAL: Also send THIS chunk - don't lose the first audio!
                        SaveOverlapBuffer(audioData, ref _outboundOverlapTail);
                        _ = _outboundClient.SendAudioChunkAsync(audioData);
                    }
                    else
                    {
                        return; // Don't send audio in IDLE state
                    }
                    break;

                case TurnState.ACTIVE:
                    var turnDuration = (now - _outboundSpeechStart).TotalMilliseconds;

                    // MAX_TURN as safety - only close if near-silence
                    if (turnDuration >= _config.Vad.MaxTurnMs &&
                        energy < _config.Vad.SilenceThreshold)
                    {
                        _logger.LogDebug("[OUTBOUND] Max turn {Duration}ms near-silence - sending activity_end", turnDuration);
                        SaveOverlapBuffer(audioData, ref _outboundOverlapTail);
                        _ = _outboundClient.SendActivityEndAsync();
                        _outboundTurnState = TurnState.WAIT_COMPLETE;
                        _outboundPeakRmsDuringWait = 0;  // Reset peak for new wait period
                        return;
                    }

                    // Preferred closure: on real silence
                    if (energy > _config.Vad.SilenceThreshold)
                    {
                        _outboundLastSpeech = now;
                    }
                    else
                    {
                        var silenceDuration = (now - _outboundLastSpeech).TotalMilliseconds;
                        if (silenceDuration >= _config.Vad.SilenceDurationMs &&
                            turnDuration >= _config.Vad.MinTurnDurationMs)
                        {
                            _logger.LogDebug("[OUTBOUND] Silence {Silence}ms (turn {Turn}ms) - sending activity_end",
                                silenceDuration, turnDuration);
                            SaveOverlapBuffer(audioData, ref _outboundOverlapTail);
                            _ = _outboundClient.SendActivityEndAsync();
                            _outboundTurnState = TurnState.WAIT_COMPLETE;
                            _outboundPeakRmsDuringWait = 0;  // Reset peak for new wait period
                            _outboundIsSpeaking = false;
                            return;
                        }
                    }

                    // Save overlap and send audio
                    SaveOverlapBuffer(audioData, ref _outboundOverlapTail);
                    _ = _outboundClient.SendAudioChunkAsync(audioData);
                    break;

                case TurnState.WAIT_COMPLETE:
                    // DON'T drop audio! Buffer it during WAIT_COMPLETE
                    // Track peak RMS for auto-restart decision
                    if (energy > _outboundPeakRmsDuringWait)
                        _outboundPeakRmsDuringWait = energy;
                    BufferPendingAudio(audioData, _outboundPendingChunks, ref _outboundPendingBytes, _config.Vad.PendingMaxBytes);
                    break;
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
            
            // In streaming mode, Gemini handles segmentation with its internal VAD
            // We should NOT send end_of_turn on every pause - that interrupts translations
            if (_outboundClient.IsStreamingMode)
            {
                // Let Gemini's internal VAD handle translation timing
                _logger.LogDebug("[OUTBOUND] Silence detected → {Lang} - Gemini VAD handles segmentation", targetLang);
            }
            else
            {
                _logger.LogDebug("[OUTBOUND] Requesting translation → {Lang} (buffer mode)", targetLang);
                await _outboundClient.RequestTranslationAsync(cancellationToken);
            }
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

    /// <summary>
    /// Save the last OverlapMs of audio for lexical continuity between turns
    /// </summary>
    private void SaveOverlapBuffer(byte[] audioData, ref byte[]? overlapTail)
    {
        int overlapBytes = _config.Vad.OverlapMs * 16000 * 2 / 1000; // PCM16 mono at 16kHz
        if (audioData.Length >= overlapBytes)
        {
            overlapTail = new byte[overlapBytes];
            Array.Copy(audioData, audioData.Length - overlapBytes, overlapTail, 0, overlapBytes);
        }
        else
        {
            overlapTail = (byte[])audioData.Clone();
        }
    }

    /// <summary>
    /// Buffer audio during WAIT_COMPLETE state, with size limit
    /// </summary>
    private static void BufferPendingAudio(byte[] audioData, List<byte[]> pendingChunks, ref int pendingBytes, int maxBytes)
    {
        pendingChunks.Add((byte[])audioData.Clone());
        pendingBytes += audioData.Length;

        // Limit to ~2s of audio
        while (pendingBytes > maxBytes && pendingChunks.Count > 0)
        {
            var removed = pendingChunks[0];
            pendingChunks.RemoveAt(0);
            pendingBytes -= removed.Length;
        }
    }

    /// <summary>
    /// Handle turn_complete for inbound (remote party) - auto-restart if still speaking
    /// </summary>
    private void OnInboundTurnComplete()
    {
        if (_inboundClient == null || !_inboundClient.IsConnected || !_inboundClient.IsStreamingMode) return;

        lock (_inboundLock)
        {
            // During simulation, always auto-restart since audio file is still playing
            if (_isSimulating)
            {
                _logger.LogInformation("[INBOUND] turn_complete during simulation - forcing auto-restart");
                _ = _inboundClient.SendActivityStartAsync();
                _inboundTurnState = TurnState.ACTIVE;
                _inboundSpeechStart = DateTime.UtcNow;
                _inboundLastSpeech = DateTime.UtcNow;
                _inboundPeakRmsDuringWait = 0;
                return;
            }

            // Use peak RMS during wait period for auto-restart decision
            // This captures if there was speech activity while waiting for turn_complete
            var rmsForRestart = Math.Max(_inboundLastRms, _inboundPeakRmsDuringWait);
            _logger.LogInformation("[INBOUND] turn_complete - peakRms: {Peak:F4}, lastRms: {Last:F4}, threshold: {Threshold:F4}",
                _inboundPeakRmsDuringWait, _inboundLastRms, _config.Vad.AutoRestartThreshold);

            if (rmsForRestart > _config.Vad.AutoRestartThreshold)
            {
                _logger.LogInformation("[INBOUND] Auto-restart (RMS: {Rms:F4}) - sending activity_start", rmsForRestart);
                _ = _inboundClient.SendActivityStartAsync();

                // Send overlap for lexical continuity
                if (_inboundOverlapTail != null)
                {
                    _logger.LogDebug("[INBOUND] Sending overlap: {Bytes} bytes", _inboundOverlapTail.Length);
                    _ = _inboundClient.SendAudioChunkAsync(_inboundOverlapTail);
                    _inboundOverlapTail = null;
                }

                // Flush buffered audio from WAIT_COMPLETE
                if (_inboundPendingChunks.Count > 0)
                {
                    _logger.LogDebug("[INBOUND] Flushing buffered audio: {Count} chunks ({Bytes} bytes)",
                        _inboundPendingChunks.Count, _inboundPendingBytes);
                    foreach (var chunk in _inboundPendingChunks)
                    {
                        _ = _inboundClient.SendAudioChunkAsync(chunk);
                    }
                    _inboundPendingChunks.Clear();
                    _inboundPendingBytes = 0;
                }

                _inboundTurnState = TurnState.ACTIVE;
                _inboundSpeechStart = DateTime.UtcNow;
                _inboundLastSpeech = DateTime.UtcNow;
            }
            else
            {
                _logger.LogInformation("[INBOUND] No auto-restart (RMS: {Rms:F4} < {Threshold}) - going IDLE",
                    rmsForRestart, _config.Vad.AutoRestartThreshold);
                _inboundTurnState = TurnState.IDLE;
                _inboundPendingChunks.Clear();
                _inboundPendingBytes = 0;
            }

            // Reset peak for next wait period
            _inboundPeakRmsDuringWait = 0;
        }
    }

    /// <summary>
    /// Handle turn_complete for outbound (operator) - auto-restart if still speaking
    /// </summary>
    private void OnOutboundTurnComplete()
    {
        if (_outboundClient == null || !_outboundClient.IsConnected || !_outboundClient.IsStreamingMode) return;

        lock (_outboundLock)
        {
            // Use peak RMS during wait period for auto-restart decision
            // This captures if there was speech activity while waiting for turn_complete
            var rmsForRestart = Math.Max(_outboundLastRms, _outboundPeakRmsDuringWait);
            _logger.LogInformation("[OUTBOUND] turn_complete - peakRms: {Peak:F4}, lastRms: {Last:F4}, threshold: {Threshold:F4}",
                _outboundPeakRmsDuringWait, _outboundLastRms, _config.Vad.AutoRestartThreshold);

            if (rmsForRestart > _config.Vad.AutoRestartThreshold)
            {
                _logger.LogInformation("[OUTBOUND] Auto-restart (RMS: {Rms:F4}) - sending activity_start", rmsForRestart);
                _ = _outboundClient.SendActivityStartAsync();

                // Send overlap for lexical continuity
                if (_outboundOverlapTail != null)
                {
                    _logger.LogDebug("[OUTBOUND] Sending overlap: {Bytes} bytes", _outboundOverlapTail.Length);
                    _ = _outboundClient.SendAudioChunkAsync(_outboundOverlapTail);
                    _outboundOverlapTail = null;
                }

                // Flush buffered audio from WAIT_COMPLETE
                if (_outboundPendingChunks.Count > 0)
                {
                    _logger.LogDebug("[OUTBOUND] Flushing buffered audio: {Count} chunks ({Bytes} bytes)",
                        _outboundPendingChunks.Count, _outboundPendingBytes);
                    foreach (var chunk in _outboundPendingChunks)
                    {
                        _ = _outboundClient.SendAudioChunkAsync(chunk);
                    }
                    _outboundPendingChunks.Clear();
                    _outboundPendingBytes = 0;
                }

                _outboundTurnState = TurnState.ACTIVE;
                _outboundSpeechStart = DateTime.UtcNow;
                _outboundLastSpeech = DateTime.UtcNow;
            }
            else
            {
                _logger.LogInformation("[OUTBOUND] No auto-restart (RMS: {Rms:F4} < {Threshold}) - going IDLE",
                    rmsForRestart, _config.Vad.AutoRestartThreshold);
                _outboundTurnState = TurnState.IDLE;
                _outboundPendingChunks.Clear();
                _outboundPendingBytes = 0;
            }

            // Reset peak for next wait period
            _outboundPeakRmsDuringWait = 0;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping translation worker...");

        // Stop turn timer if running
        StopTurnTimer();

        _audioBridge.OnInboundAudioCaptured -= OnInboundAudioCaptured;
        _audioBridge.OnOutboundAudioCaptured -= OnOutboundAudioCaptured;
        
        if (_trayService != null)
        {
            _trayService.OnLanguageSelected -= OnManualLanguageSelected;
            _trayService.OnAutoDetectEnabled -= OnAutoDetectEnabled;
            _trayService.OnTranslationToggled -= OnTranslationToggled;
            _trayService.OnVoiceChanged -= OnVoiceChanged;
            _trayService.OnTestTranslationRequested -= OnTestTranslationRequested;
            _trayService.OnVadSettingsOpened -= OnVadSettingsOpened;
            _trayService.OnVadSettingsChanged -= OnVadSettingsChanged;
        }
        
        if (_inboundClient != null)
        {
            _inboundClient.OnAudioReceived -= OnInboundTranslatedAudio;
            _inboundClient.OnTranslationSkipped -= OnInboundTranslationSkipped;
            _inboundClient.OnLanguageDetected -= OnRemoteLanguageDetected;
            _inboundClient.OnTurnComplete -= OnInboundTurnComplete;
        }
        if (_outboundClient != null)
        {
            _outboundClient.OnAudioReceived -= OnOutboundTranslatedAudio;
            _outboundClient.OnTranslationSkipped -= OnOutboundTranslationSkipped;
            _outboundClient.OnTurnComplete -= OnOutboundTurnComplete;
        }
        
        await base.StopAsync(cancellationToken);
    }
}
