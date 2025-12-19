using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranslationBridge.Configuration;

namespace TranslationBridge.Services;

/// <summary>
/// System Tray UI for language selection and override
/// 
/// Features:
/// - Auto-detect mode for inbound calls
/// - Manual language selection for outbound calls
/// - Override detected language during call
/// - Visual indicator of current mode/language
/// </summary>
public class TrayIconService : IDisposable
{
    private readonly ILogger<TrayIconService> _logger;
    private readonly BridgeConfig _config;
    
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _contextMenu;
    
    // Menu items
    private readonly Dictionary<string, ToolStripMenuItem> _languageMenuItems = new();
    private ToolStripMenuItem? _autoDetectItem;
    private ToolStripMenuItem? _currentLanguageItem;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _overrideHintItem;

    // Toggle translation items
    private ToolStripMenuItem? _toggleItem;
    private ToolStripMenuItem? _translationStatusItem;
    private bool _isTranslationActive = false;

    // Device selection items
    private ToolStripMenuItem? _deviceMenu;
    private ToolStripMenuItem? _inboundCaptureMenu;
    private ToolStripMenuItem? _inboundPlaybackMenu;
    private ToolStripMenuItem? _outboundCaptureMenu;
    private ToolStripMenuItem? _outboundPlaybackMenu;

    // Voice cloning items
    private ToolStripMenuItem? _voiceMenu;
    private ToolStripMenuItem? _voiceEnabledItem;
    private ToolStripMenuItem? _voiceSelectItem;
    private ToolStripMenuItem? _voiceCurrentItem;
    private ToolStripMenuItem? _voiceClearItem;
    private string? _currentVoicePath;
    private bool _voiceEnabled;

    // Current state
    private string _currentMode = "auto";  // "auto" or language code
    private string? _detectedLanguage;
    private string? _detectedLanguageName;
    private bool _isOverridden = false;
    
    /// <summary>
    /// Fired when operator manually selects/overrides a language
    /// </summary>
    public event Action<string>? OnLanguageSelected;

    /// <summary>
    /// Fired when auto-detect mode is enabled
    /// </summary>
    public event Action? OnAutoDetectEnabled;

    /// <summary>
    /// Fired when translation is toggled on/off
    /// </summary>
    public event Action<bool>? OnTranslationToggled;

    /// <summary>
    /// Fired when audio device is changed
    /// Parameters: deviceType (InboundCapture, InboundPlayback, OutboundCapture, OutboundPlayback), deviceName
    /// </summary>
    public event Action<string, string>? OnDeviceChanged;

    /// <summary>
    /// Fired when voice reference is changed
    /// Parameters: voicePath (full path to WAV file, or null to disable)
    /// </summary>
    public event Action<string?>? OnVoiceChanged;

    /// <summary>
    /// Fired when test translation is requested
    /// </summary>
    public event Action? OnTestTranslationRequested;

    /// <summary>
    /// Fired when call simulation is requested with a WAV file path
    /// </summary>
    public event Action<string>? OnSimulateCallRequested;

    /// <summary>
    /// Fired when call simulation should be stopped
    /// </summary>
    public event Action? OnStopSimulationRequested;

    // Simulation state
    private ToolStripMenuItem? _simulateItem;
    private ToolStripMenuItem? _stopSimulateItem;
    private bool _isSimulating = false;

    // Supported languages for the menu
    private static readonly Dictionary<string, (string Flag, string Italian, string English)> SupportedLanguages = new()
    {
        { "de", ("🇩🇪", "Tedesco", "German") },
        { "en", ("🇬🇧", "Inglese", "English") },
        { "fr", ("🇫🇷", "Francese", "French") },
        { "es", ("🇪🇸", "Spagnolo", "Spanish") },
        { "pt", ("🇵🇹", "Portoghese", "Portuguese") },
        { "ru", ("🇷🇺", "Russo", "Russian") },
        { "zh", ("🇨🇳", "Cinese", "Chinese") },
        { "ja", ("🇯🇵", "Giapponese", "Japanese") },
        { "ko", ("🇰🇷", "Coreano", "Korean") },
        { "ar", ("🇸🇦", "Arabo", "Arabic") },
        { "nl", ("🇳🇱", "Olandese", "Dutch") },
        { "pl", ("🇵🇱", "Polacco", "Polish") },
        { "tr", ("🇹🇷", "Turco", "Turkish") },
        { "uk", ("🇺🇦", "Ucraino", "Ukrainian") },
        { "ro", ("🇷🇴", "Rumeno", "Romanian") },
    };

    public TrayIconService(
        ILogger<TrayIconService> logger,
        IOptions<BridgeConfig> config,
        TranslationWorker worker)
    {
        _logger = logger;
        _config = config.Value;

        // Initialize voice settings from config
        _currentVoicePath = string.IsNullOrEmpty(_config.Voice.ReferencePath) ? null : _config.Voice.ReferencePath;
        _voiceEnabled = _config.Voice.Enabled && !string.IsNullOrEmpty(_currentVoicePath);
    }

    public void Initialize()
    {
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            CreateTrayIcon();
            Application.Run();
        });
        
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        
        _logger.LogInformation("Tray icon service initialized");
    }

    private void CreateTrayIcon()
    {
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Opening += (s, e) => UpdateMenuState();

        // === TOGGLE TRADUZIONE ===
        _toggleItem = new ToolStripMenuItem("▶ AVVIA Traduzione")
        {
            Font = new Font(_contextMenu.Font, FontStyle.Bold),
            BackColor = Color.FromArgb(220, 255, 220)  // Verde chiaro
        };
        _toggleItem.Click += (s, e) => ToggleTranslation();
        _contextMenu.Items.Add(_toggleItem);

        _translationStatusItem = new ToolStripMenuItem("⏸ Traduzione: IN PAUSA")
        {
            Enabled = false,
            ForeColor = Color.Gray
        };
        _contextMenu.Items.Add(_translationStatusItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // === HEADER ===
        var headerItem = new ToolStripMenuItem("3CX Translation Bridge")
        {
            Enabled = false,
            Font = new Font(_contextMenu.Font, FontStyle.Bold)
        };
        _contextMenu.Items.Add(headerItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        
        // === STATUS SECTION ===
        _statusItem = new ToolStripMenuItem($"Operatore: {_config.Languages.LocalLanguage.ToUpper()}")
        {
            Enabled = false
        };
        _contextMenu.Items.Add(_statusItem);
        
        _currentLanguageItem = new ToolStripMenuItem("Lingua cliente: Auto-detect")
        {
            Enabled = false,
            Font = new Font(_contextMenu.Font, FontStyle.Bold)
        };
        _contextMenu.Items.Add(_currentLanguageItem);
        
        _contextMenu.Items.Add(new ToolStripSeparator());
        
        // === MODE SECTION ===
        var modeLabel = new ToolStripMenuItem("═══ MODALITÀ ═══") { Enabled = false };
        _contextMenu.Items.Add(modeLabel);
        
        // Auto-detect option
        _autoDetectItem = new ToolStripMenuItem("🔍 Auto-Detect (rileva automaticamente)")
        {
            Checked = true,
            CheckOnClick = false
        };
        _autoDetectItem.Click += (s, e) => SetAutoDetectMode();
        _contextMenu.Items.Add(_autoDetectItem);
        
        _contextMenu.Items.Add(new ToolStripSeparator());
        
        // === OVERRIDE HINT ===
        _overrideHintItem = new ToolStripMenuItem("💡 Seleziona lingua per override manuale")
        {
            Enabled = false,
            ForeColor = Color.Gray
        };
        _contextMenu.Items.Add(_overrideHintItem);
        
        // === LANGUAGE SECTION ===
        var langLabel = new ToolStripMenuItem("═══ SELEZIONA LINGUA ═══") { Enabled = false };
        _contextMenu.Items.Add(langLabel);
        
        // Language options
        foreach (var lang in SupportedLanguages)
        {
            var displayName = $"{lang.Value.Flag} {lang.Value.Italian} ({lang.Value.English})";
            var item = new ToolStripMenuItem(displayName)
            {
                Tag = lang.Key,
                CheckOnClick = false
            };
            item.Click += (s, e) => SetManualLanguage(lang.Key);
            _contextMenu.Items.Add(item);
            _languageMenuItems[lang.Key] = item;
        }
        
        _contextMenu.Items.Add(new ToolStripSeparator());
        
        // === ACTIONS ===
        var resetItem = new ToolStripMenuItem("🔄 Reset (torna ad Auto-Detect)");
        resetItem.Click += (s, e) => SetAutoDetectMode();
        _contextMenu.Items.Add(resetItem);
        
        _contextMenu.Items.Add(new ToolStripSeparator());

        // === DEVICE SECTION ===
        _deviceMenu = new ToolStripMenuItem("🔊 Dispositivi Audio");
        CreateDeviceMenus();
        _contextMenu.Items.Add(_deviceMenu);

        // === VOICE CLONING SECTION ===
        _voiceMenu = new ToolStripMenuItem("🎤 Voce Operatore");
        CreateVoiceMenu();
        _contextMenu.Items.Add(_voiceMenu);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // === TEST SECTION ===
        var testItem = new ToolStripMenuItem("🧪 Test Traduzione...")
        {
            ToolTipText = "Parla e ascolta la traduzione per testare qualità e intonazione"
        };
        testItem.Click += (s, e) => OnTestTranslationRequested?.Invoke();
        _contextMenu.Items.Add(testItem);

        // === SIMULATION SECTION ===
        _simulateItem = new ToolStripMenuItem("📞 Simula Chiamata...")
        {
            ToolTipText = "Inietta audio da file WAV come se fosse una chiamata in arrivo"
        };
        _simulateItem.Click += (s, e) => StartCallSimulation();
        _contextMenu.Items.Add(_simulateItem);

        _stopSimulateItem = new ToolStripMenuItem("⏹ Ferma Simulazione")
        {
            Visible = false,
            BackColor = Color.FromArgb(255, 200, 200)
        };
        _stopSimulateItem.Click += (s, e) => StopCallSimulation();
        _contextMenu.Items.Add(_stopSimulateItem);

        // === DIAGNOSTIC SECTION ===
        var diagnosticItem = new ToolStripMenuItem("🔬 Diagnostica Streaming...")
        {
            ToolTipText = "Test diagnostico: invia file audio e salva output per analisi"
        };
        diagnosticItem.Click += (s, e) => OpenDiagnosticDialog();
        _contextMenu.Items.Add(diagnosticItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("❌ Esci");
        exitItem.Click += (s, e) =>
        {
            _trayIcon?.Dispose();
            Application.Exit();
            Environment.Exit(0);
        };
        _contextMenu.Items.Add(exitItem);
        
        // Create tray icon - starts in PAUSED state
        _trayIcon = new NotifyIcon
        {
            Icon = CreateIcon("OFF", Color.Gray),
            Text = "3CX Translation - IN PAUSA",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (s, e) => ToggleTranslation();

        // Show balloon tip
        _trayIcon.ShowBalloonTip(
            3000,
            "3CX Translation Bridge",
            "Traduzione in pausa.\nDouble-click o menu per avviare.",
            ToolTipIcon.Info);
    }

    private void CreateDeviceMenus()
    {
        if (_deviceMenu == null) return;

        try
        {
            var (captureDevices, renderDevices) = AudioBridge.GetAvailableDevices();

            // === INBOUND CAPTURE (3CX Speaker → capture loopback) ===
            _inboundCaptureMenu = new ToolStripMenuItem("📥 Inbound Capture (audio 3CX)")
            {
                ToolTipText = "Device da cui catturare l'audio del cliente (output 3CX)"
            };
            foreach (var device in renderDevices)  // Loopback capture uses render devices
            {
                var item = new ToolStripMenuItem(device.Name)
                {
                    Tag = device.Name,
                    Checked = device.Name.Contains(_config.AudioDevices.InboundCaptureDevice, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (s, e) => SelectDevice("InboundCapture", device.Name, _inboundCaptureMenu!);
                _inboundCaptureMenu.DropDownItems.Add(item);
            }
            _deviceMenu.DropDownItems.Add(_inboundCaptureMenu);

            // === INBOUND PLAYBACK (translated audio → operator headphones) ===
            _inboundPlaybackMenu = new ToolStripMenuItem("🎧 Inbound Playback (cuffie operatore)")
            {
                ToolTipText = "Device per riprodurre l'audio tradotto all'operatore"
            };
            foreach (var device in renderDevices)
            {
                var item = new ToolStripMenuItem(device.Name)
                {
                    Tag = device.Name,
                    Checked = device.Name.Contains(_config.AudioDevices.InboundPlaybackDevice, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (s, e) => SelectDevice("InboundPlayback", device.Name, _inboundPlaybackMenu!);
                _inboundPlaybackMenu.DropDownItems.Add(item);
            }
            _deviceMenu.DropDownItems.Add(_inboundPlaybackMenu);

            _deviceMenu.DropDownItems.Add(new ToolStripSeparator());

            // === OUTBOUND CAPTURE (operator microphone) ===
            _outboundCaptureMenu = new ToolStripMenuItem("🎤 Outbound Capture (mic operatore)")
            {
                ToolTipText = "Microfono dell'operatore"
            };
            foreach (var device in captureDevices)
            {
                var item = new ToolStripMenuItem(device.Name)
                {
                    Tag = device.Name,
                    Checked = device.Name.Contains(_config.AudioDevices.OutboundCaptureDevice, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (s, e) => SelectDevice("OutboundCapture", device.Name, _outboundCaptureMenu!);
                _outboundCaptureMenu.DropDownItems.Add(item);
            }
            _deviceMenu.DropDownItems.Add(_outboundCaptureMenu);

            // === OUTBOUND PLAYBACK (translated audio → 3CX mic input) ===
            _outboundPlaybackMenu = new ToolStripMenuItem("📤 Outbound Playback (mic 3CX)")
            {
                ToolTipText = "Device per inviare l'audio tradotto al 3CX"
            };
            foreach (var device in renderDevices)
            {
                var item = new ToolStripMenuItem(device.Name)
                {
                    Tag = device.Name,
                    Checked = device.Name.Contains(_config.AudioDevices.OutboundPlaybackDevice, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (s, e) => SelectDevice("OutboundPlayback", device.Name, _outboundPlaybackMenu!);
                _outboundPlaybackMenu.DropDownItems.Add(item);
            }
            _deviceMenu.DropDownItems.Add(_outboundPlaybackMenu);

            _deviceMenu.DropDownItems.Add(new ToolStripSeparator());

            // Info item
            var infoItem = new ToolStripMenuItem("ℹ️ Richiede riavvio per applicare")
            {
                Enabled = false,
                ForeColor = Color.Gray
            };
            _deviceMenu.DropDownItems.Add(infoItem);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate audio devices");
            _deviceMenu.DropDownItems.Add(new ToolStripMenuItem("Errore caricamento dispositivi") { Enabled = false });
        }
    }

    private void CreateVoiceMenu()
    {
        if (_voiceMenu == null) return;

        // Enable/Disable voice cloning
        _voiceEnabledItem = new ToolStripMenuItem("✅ Abilita Voice Cloning")
        {
            Checked = _voiceEnabled,
            CheckOnClick = false
        };
        _voiceEnabledItem.Click += (s, e) => ToggleVoiceCloning();
        _voiceMenu.DropDownItems.Add(_voiceEnabledItem);

        _voiceMenu.DropDownItems.Add(new ToolStripSeparator());

        // Current voice display
        _voiceCurrentItem = new ToolStripMenuItem(GetVoiceDisplayText())
        {
            Enabled = false,
            ForeColor = _voiceEnabled ? Color.Green : Color.Gray
        };
        _voiceMenu.DropDownItems.Add(_voiceCurrentItem);

        _voiceMenu.DropDownItems.Add(new ToolStripSeparator());

        // Select voice file
        _voiceSelectItem = new ToolStripMenuItem("📂 Seleziona file voce...");
        _voiceSelectItem.Click += (s, e) => SelectVoiceFile();
        _voiceMenu.DropDownItems.Add(_voiceSelectItem);

        // Record voice
        var recordItem = new ToolStripMenuItem("🔴 Registra voce...");
        recordItem.Click += (s, e) => RecordVoice();
        _voiceMenu.DropDownItems.Add(recordItem);

        _voiceMenu.DropDownItems.Add(new ToolStripSeparator());

        // Clear voice
        _voiceClearItem = new ToolStripMenuItem("🗑️ Rimuovi voce")
        {
            Enabled = !string.IsNullOrEmpty(_currentVoicePath)
        };
        _voiceClearItem.Click += (s, e) => ClearVoice();
        _voiceMenu.DropDownItems.Add(_voiceClearItem);

        _voiceMenu.DropDownItems.Add(new ToolStripSeparator());

        // Info
        var infoItem = new ToolStripMenuItem("ℹ️ WAV 16kHz, 5-10 sec di parlato")
        {
            Enabled = false,
            ForeColor = Color.Gray
        };
        _voiceMenu.DropDownItems.Add(infoItem);
    }

    private string GetVoiceDisplayText()
    {
        if (string.IsNullOrEmpty(_currentVoicePath))
        {
            return "🔇 Voce: Default server";
        }

        var fileName = Path.GetFileName(_currentVoicePath);
        return _voiceEnabled
            ? $"🎙️ Voce: {fileName}"
            : $"🔇 Voce: {fileName} (disabilitata)";
    }

    private void ToggleVoiceCloning()
    {
        if (string.IsNullOrEmpty(_currentVoicePath))
        {
            ShowNotification("Voce non configurata",
                "Seleziona prima un file voce WAV.");
            return;
        }

        _voiceEnabled = !_voiceEnabled;

        // Update UI
        if (_voiceEnabledItem != null)
            _voiceEnabledItem.Checked = _voiceEnabled;

        if (_voiceCurrentItem != null)
        {
            _voiceCurrentItem.Text = GetVoiceDisplayText();
            _voiceCurrentItem.ForeColor = _voiceEnabled ? Color.Green : Color.Gray;
        }

        // Save to settings
        SaveVoiceToSettings(_currentVoicePath, _voiceEnabled);

        // Fire event
        OnVoiceChanged?.Invoke(_voiceEnabled ? _currentVoicePath : null);

        _logger.LogInformation("Voice cloning {Status}", _voiceEnabled ? "enabled" : "disabled");

        ShowNotification(
            _voiceEnabled ? "Voice Cloning Attivo" : "Voice Cloning Disattivo",
            _voiceEnabled
                ? "La tua voce verrà usata per la sintesi"
                : "Verrà usata la voce default del server");
    }

    private void SelectVoiceFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Seleziona file voce di riferimento",
            Filter = "File WAV|*.wav|Tutti i file|*.*",
            FilterIndex = 1,
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrEmpty(_currentVoicePath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_currentVoicePath);
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            var filePath = dialog.FileName;

            // Basic validation
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 10 * 1024 * 1024) // 10MB max
            {
                ShowNotification("File troppo grande",
                    "Il file voce deve essere inferiore a 10MB.\nUsa un file di 5-10 secondi.");
                return;
            }

            _currentVoicePath = filePath;
            _voiceEnabled = true;

            // Update UI
            if (_voiceEnabledItem != null)
                _voiceEnabledItem.Checked = true;

            if (_voiceCurrentItem != null)
            {
                _voiceCurrentItem.Text = GetVoiceDisplayText();
                _voiceCurrentItem.ForeColor = Color.Green;
            }

            if (_voiceClearItem != null)
                _voiceClearItem.Enabled = true;

            // Save to settings
            SaveVoiceToSettings(filePath, true);

            // Fire event
            OnVoiceChanged?.Invoke(filePath);

            _logger.LogInformation("Voice file selected: {Path}", filePath);

            ShowNotification("Voce Configurata",
                $"File: {Path.GetFileName(filePath)}\n\nLa tua voce verrà inviata al server.");
        }
    }

    private void RecordVoice()
    {
        using var dialog = new VoiceRecorderDialog();

        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(dialog.RecordedFilePath))
        {
            var filePath = dialog.RecordedFilePath;

            _currentVoicePath = filePath;
            _voiceEnabled = true;

            // Update UI
            if (_voiceEnabledItem != null)
                _voiceEnabledItem.Checked = true;

            if (_voiceCurrentItem != null)
            {
                _voiceCurrentItem.Text = GetVoiceDisplayText();
                _voiceCurrentItem.ForeColor = Color.Green;
            }

            if (_voiceClearItem != null)
                _voiceClearItem.Enabled = true;

            // Save to settings
            SaveVoiceToSettings(filePath, true);

            // Fire event
            OnVoiceChanged?.Invoke(filePath);

            _logger.LogInformation("Voice recorded and saved: {Path}", filePath);

            ShowNotification("Voce Registrata",
                $"File: {Path.GetFileName(filePath)}\n\nLa tua voce verrà usata per la sintesi.");
        }
    }

    private void ClearVoice()
    {
        _currentVoicePath = null;
        _voiceEnabled = false;

        // Update UI
        if (_voiceEnabledItem != null)
            _voiceEnabledItem.Checked = false;

        if (_voiceCurrentItem != null)
        {
            _voiceCurrentItem.Text = GetVoiceDisplayText();
            _voiceCurrentItem.ForeColor = Color.Gray;
        }

        if (_voiceClearItem != null)
            _voiceClearItem.Enabled = false;

        // Save to settings
        SaveVoiceToSettings("", false);

        // Fire event
        OnVoiceChanged?.Invoke(null);

        _logger.LogInformation("Voice reference cleared");

        ShowNotification("Voce Rimossa",
            "Verrà usata la voce default del server.");
    }

    private void SaveVoiceToSettings(string? voicePath, bool enabled)
    {
        try
        {
            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(appSettingsPath))
            {
                _logger.LogWarning("appsettings.json not found at {Path}", appSettingsPath);
                return;
            }

            var json = File.ReadAllText(appSettingsPath);
            var jsonNode = JsonNode.Parse(json);

            if (jsonNode == null)
            {
                _logger.LogWarning("Could not parse appsettings.json");
                return;
            }

            // Navigate to TranslationBridge
            var translationBridge = jsonNode["TranslationBridge"];
            if (translationBridge == null)
            {
                _logger.LogWarning("TranslationBridge section not found in appsettings.json");
                return;
            }

            // Ensure Voice section exists
            if (translationBridge["Voice"] == null)
            {
                translationBridge["Voice"] = new JsonObject();
            }

            var voiceSection = translationBridge["Voice"]!;
            voiceSection["ReferencePath"] = voicePath ?? "";
            voiceSection["Enabled"] = enabled;

            // Write back with formatting
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(appSettingsPath, jsonNode.ToJsonString(options));

            _logger.LogInformation("Saved voice settings: Enabled={Enabled}, Path={Path}",
                enabled, voicePath ?? "(none)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving voice settings to appsettings.json");
        }
    }

    /// <summary>
    /// Get the current voice reference path (if enabled)
    /// </summary>
    public string? GetVoiceReferencePath()
    {
        return _voiceEnabled ? _currentVoicePath : null;
    }

    /// <summary>
    /// Check if voice cloning is enabled
    /// </summary>
    public bool IsVoiceCloningEnabled => _voiceEnabled && !string.IsNullOrEmpty(_currentVoicePath);

    private void SelectDevice(string deviceType, string deviceName, ToolStripMenuItem parentMenu)
    {
        // Update checkmarks
        foreach (ToolStripItem item in parentMenu.DropDownItems)
        {
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.Checked = menuItem.Tag?.ToString() == deviceName;
            }
        }

        _logger.LogInformation("Device {Type} changed to: {Device}", deviceType, deviceName);

        // Save to appsettings.json
        SaveDeviceToSettings(deviceType, deviceName);

        // Fire event
        OnDeviceChanged?.Invoke(deviceType, deviceName);

        ShowNotification("Dispositivo Salvato",
            $"{deviceType}: {deviceName}\n\nRiavviare l'applicazione per applicare.");
    }

    private void SaveDeviceToSettings(string deviceType, string deviceName)
    {
        try
        {
            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(appSettingsPath))
            {
                _logger.LogWarning("appsettings.json not found at {Path}", appSettingsPath);
                return;
            }

            var json = File.ReadAllText(appSettingsPath);
            var jsonNode = JsonNode.Parse(json);

            if (jsonNode == null)
            {
                _logger.LogWarning("Could not parse appsettings.json");
                return;
            }

            // Navigate to TranslationBridge.AudioDevices
            var audioDevices = jsonNode["TranslationBridge"]?["AudioDevices"];
            if (audioDevices == null)
            {
                _logger.LogWarning("AudioDevices section not found in appsettings.json");
                return;
            }

            // Update the appropriate device setting
            var settingName = deviceType switch
            {
                "InboundCapture" => "InboundCaptureDevice",
                "InboundPlayback" => "InboundPlaybackDevice",
                "OutboundCapture" => "OutboundCaptureDevice",
                "OutboundPlayback" => "OutboundPlaybackDevice",
                _ => null
            };

            if (settingName != null)
            {
                audioDevices[settingName] = deviceName;

                // Write back with formatting
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(appSettingsPath, jsonNode.ToJsonString(options));

                _logger.LogInformation("Saved {Setting} = {Value} to appsettings.json", settingName, deviceName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving device to appsettings.json");
        }
    }

    private void UpdateMenuState()
    {
        // Update current language display
        if (_currentLanguageItem != null)
        {
            if (_currentMode == "auto")
            {
                if (_detectedLanguage != null)
                {
                    var overrideHint = _isOverridden ? "" : " (rilevata)";
                    _currentLanguageItem.Text = $"Lingua cliente: {_detectedLanguageName}{overrideHint}";
                }
                else
                {
                    _currentLanguageItem.Text = "Lingua cliente: In attesa...";
                }
            }
            else
            {
                var langInfo = SupportedLanguages.GetValueOrDefault(_currentMode);
                var name = langInfo.Italian ?? _currentMode.ToUpper();
                _currentLanguageItem.Text = $"Lingua cliente: {name} (manuale)";
            }
        }
        
        // Update override hint visibility
        if (_overrideHintItem != null)
        {
            _overrideHintItem.Visible = _currentMode == "auto" && _detectedLanguage != null && !_isOverridden;
            _overrideHintItem.Text = _detectedLanguage != null 
                ? $"💡 Lingua sbagliata? Seleziona quella corretta qui sotto"
                : "💡 Seleziona lingua per override manuale";
        }
        
        // Update checkmarks
        if (_autoDetectItem != null)
        {
            _autoDetectItem.Checked = _currentMode == "auto" && !_isOverridden;
        }
        
        foreach (var kvp in _languageMenuItems)
        {
            var isSelected = (_isOverridden && kvp.Key == _detectedLanguage) || 
                            (_currentMode != "auto" && kvp.Key == _currentMode);
            kvp.Value.Checked = isSelected;
            
            // Highlight detected language
            if (_currentMode == "auto" && !_isOverridden && kvp.Key == _detectedLanguage)
            {
                kvp.Value.Font = new Font(kvp.Value.Font, FontStyle.Italic);
                kvp.Value.Text = $"{SupportedLanguages[kvp.Key].Flag} {SupportedLanguages[kvp.Key].Italian} ← rilevata";
            }
            else
            {
                kvp.Value.Font = new Font(kvp.Value.Font, FontStyle.Regular);
                var lang = SupportedLanguages[kvp.Key];
                kvp.Value.Text = $"{lang.Flag} {lang.Italian} ({lang.English})";
            }
        }
    }

    private void SetAutoDetectMode()
    {
        _currentMode = "auto";
        _isOverridden = false;
        _detectedLanguage = null;
        _detectedLanguageName = null;
        
        UpdateIcon("A", Color.FromArgb(0, 120, 212), "Auto-Detect");
        
        _logger.LogInformation("Mode: AUTO-DETECT enabled");
        OnAutoDetectEnabled?.Invoke();
        
        ShowNotification("Auto-Detect Attivo", 
            "Rilevamento automatico lingua attivo.\nLa lingua verrà rilevata quando il cliente parla.");
    }

    private void SetManualLanguage(string langCode)
    {
        var wasAutoDetect = _currentMode == "auto";
        var wasOverride = _detectedLanguage != null && _currentMode == "auto";
        
        _currentMode = langCode;
        _isOverridden = wasAutoDetect && _detectedLanguage != null;
        _detectedLanguage = langCode;
        
        var langInfo = SupportedLanguages.GetValueOrDefault(langCode);
        _detectedLanguageName = langInfo.Italian ?? langCode.ToUpper();
        
        // Update icon
        var iconColor = _isOverridden ? Color.FromArgb(200, 80, 0) : Color.FromArgb(0, 150, 0);
        UpdateIcon(langCode.ToUpper(), iconColor, _detectedLanguageName);
        
        _logger.LogInformation("Language {Action}: {Lang}", 
            _isOverridden ? "OVERRIDE" : "SELECTED", langCode);
        
        OnLanguageSelected?.Invoke(langCode);
        
        var actionText = _isOverridden ? "Override" : "Selezionata";
        ShowNotification($"Lingua {actionText}", 
            $"{langInfo.Flag} {_detectedLanguageName}\n" +
            (_isOverridden ? "Override manuale attivo" : "Pronto per chiamata in uscita"));
    }

    /// <summary>
    /// Update the detected language indicator (called when auto-detect finds the language)
    /// </summary>
    public void UpdateDetectedLanguage(string langCode, string langName)
    {
        // Don't update if manually overridden
        if (_isOverridden)
        {
            _logger.LogDebug("Ignoring detected language {Lang} - manual override active", langCode);
            return;
        }
        
        var isNewLanguage = _detectedLanguage != langCode;
        _detectedLanguage = langCode;
        _detectedLanguageName = langName;
        
        if (_currentMode == "auto")
        {
            UpdateIcon(langCode.ToUpper(), Color.FromArgb(0, 120, 212), $"Rilevato: {langName}");
            
            if (isNewLanguage)
            {
                var langInfo = SupportedLanguages.GetValueOrDefault(langCode);
                var flag = langInfo.Flag ?? "🌐";
                
                ShowNotification("Lingua Rilevata", 
                    $"{flag} {langName}\n" +
                    "Click destro per correggere se errata");
            }
        }
    }

    /// <summary>
    /// Get current language (detected or manually set)
    /// </summary>
    public string? GetCurrentLanguage()
    {
        return _detectedLanguage ?? (_currentMode != "auto" ? _currentMode : null);
    }

    /// <summary>
    /// Check if in auto-detect mode (not overridden)
    /// </summary>
    public bool IsAutoDetect => _currentMode == "auto" && !_isOverridden;
    
    /// <summary>
    /// Check if language was manually overridden
    /// </summary>
    public bool IsOverridden => _isOverridden;

    /// <summary>
    /// Check if translation is currently active
    /// </summary>
    public bool IsTranslationActive => _isTranslationActive;

    /// <summary>
    /// Toggle translation on/off
    /// </summary>
    private void ToggleTranslation()
    {
        _isTranslationActive = !_isTranslationActive;
        UpdateTranslationState(_isTranslationActive);
        OnTranslationToggled?.Invoke(_isTranslationActive);
    }

    /// <summary>
    /// Update translation state (can be called externally)
    /// </summary>
    public void UpdateTranslationState(bool isActive)
    {
        _isTranslationActive = isActive;

        if (_toggleItem == null || _translationStatusItem == null || _trayIcon == null)
            return;

        // Use Invoke for thread safety
        if (_contextMenu?.InvokeRequired == true)
        {
            _contextMenu.Invoke(() => UpdateTranslationStateUI(isActive));
        }
        else
        {
            UpdateTranslationStateUI(isActive);
        }
    }

    private void UpdateTranslationStateUI(bool isActive)
    {
        if (_toggleItem == null || _translationStatusItem == null)
            return;

        if (isActive)
        {
            _toggleItem.Text = "⏸ PAUSA Traduzione";
            _toggleItem.BackColor = Color.FromArgb(255, 220, 220);  // Rosso chiaro
            _translationStatusItem.Text = "🎙 Traduzione: ATTIVA";
            _translationStatusItem.ForeColor = Color.Green;
            _translationStatusItem.Font = new Font(_translationStatusItem.Font, FontStyle.Bold);

            UpdateIcon("ON", Color.Green, "Traduzione ATTIVA");
        }
        else
        {
            _toggleItem.Text = "▶ AVVIA Traduzione";
            _toggleItem.BackColor = Color.FromArgb(220, 255, 220);  // Verde chiaro
            _translationStatusItem.Text = "⏸ Traduzione: IN PAUSA";
            _translationStatusItem.ForeColor = Color.Gray;
            _translationStatusItem.Font = new Font(_translationStatusItem.Font, FontStyle.Regular);

            UpdateIcon("OFF", Color.Gray, "Traduzione in PAUSA");
        }

        // Notifica
        ShowNotification(
            isActive ? "Traduzione Attivata" : "Traduzione in Pausa",
            isActive ? "La traduzione è ora attiva" : "La traduzione è in pausa"
        );
    }

    private void UpdateIcon(string text, Color bgColor, string tooltip)
    {
        if (_trayIcon == null) return;
        
        _trayIcon.Icon = CreateIcon(text, bgColor);
        _trayIcon.Text = $"3CX Translation - {tooltip}";
    }

    private void ShowNotification(string title, string message)
    {
        _trayIcon?.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
    }

    private static Icon CreateIcon(string text, Color bgColor)
    {
        var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(bgColor);

            using var font = new Font("Segoe UI", text.Length > 2 ? 5.5f : 7f, FontStyle.Bold);
            using var brush = new SolidBrush(Color.White);

            var displayText = text.Length > 2 ? text.Substring(0, 2) : text;
            var size = g.MeasureString(displayText, font);
            var x = (16 - size.Width) / 2;
            var y = (16 - size.Height) / 2;

            g.DrawString(displayText, font, brush, x, y);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>
    /// Open diagnostic dialog for streaming translation testing
    /// </summary>
    private void OpenDiagnosticDialog()
    {
        _logger.LogInformation("Opening diagnostic dialog");

        Task.Run(() =>
        {
            try
            {
                var thread = new Thread(() =>
                {
                    Application.EnableVisualStyles();
                    using var dialog = new DiagnosticDialog(_config);
                    dialog.ShowDialog();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error opening diagnostic dialog");
            }
        });
    }

    /// <summary>
    /// Start call simulation - opens file dialog to select WAV file
    /// </summary>
    private void StartCallSimulation()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Seleziona file audio per simulazione chiamata",
            Filter = "File WAV|*.wav|Tutti i file audio|*.wav;*.mp3|Tutti i file|*.*",
            FilterIndex = 1,
            CheckFileExists = true,
            Multiselect = false
        };

        // Try to find default location (tools folder)
        var toolsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools");
        if (Directory.Exists(toolsPath))
        {
            dialog.InitialDirectory = Path.GetFullPath(toolsPath);
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            var filePath = dialog.FileName;
            var fileName = Path.GetFileName(filePath);

            _logger.LogInformation("Starting call simulation with file: {Path}", filePath);

            // Check if translation is active, if not ask to activate
            if (!_isTranslationActive)
            {
                var result = MessageBox.Show(
                    "La traduzione non è attiva.\n\nVuoi attivarla ora per la simulazione?",
                    "Traduzione non attiva",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    OnTranslationToggled?.Invoke(true);
                    UpdateTranslationState(true);
                }
                else
                {
                    return;
                }
            }

            // Fire event to start simulation
            OnSimulateCallRequested?.Invoke(filePath);

            // Update UI
            UpdateSimulationState(true, fileName);

            ShowNotification("Simulazione Avviata",
                $"File: {fileName}\n\nL'audio verrà iniettato come chiamata in arrivo.");
        }
    }

    /// <summary>
    /// Stop call simulation
    /// </summary>
    private void StopCallSimulation()
    {
        _logger.LogInformation("Stopping call simulation");
        OnStopSimulationRequested?.Invoke();
        UpdateSimulationState(false, null);
        ShowNotification("Simulazione Terminata", "La simulazione è stata fermata.");
    }

    /// <summary>
    /// Update simulation state (can be called externally when simulation completes)
    /// </summary>
    public void UpdateSimulationState(bool isSimulating, string? fileName)
    {
        _isSimulating = isSimulating;

        if (_simulateItem == null || _stopSimulateItem == null)
            return;

        // Use Invoke for thread safety
        if (_contextMenu?.InvokeRequired == true)
        {
            _contextMenu.Invoke(() => UpdateSimulationStateUI(isSimulating, fileName));
        }
        else
        {
            UpdateSimulationStateUI(isSimulating, fileName);
        }
    }

    private void UpdateSimulationStateUI(bool isSimulating, string? fileName)
    {
        if (_simulateItem == null || _stopSimulateItem == null)
            return;

        if (isSimulating)
        {
            _simulateItem.Text = $"📞 Simulando: {fileName ?? "..."}";
            _simulateItem.Enabled = false;
            _stopSimulateItem.Visible = true;
        }
        else
        {
            _simulateItem.Text = "📞 Simula Chiamata...";
            _simulateItem.Enabled = true;
            _stopSimulateItem.Visible = false;
        }
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
    }
}

