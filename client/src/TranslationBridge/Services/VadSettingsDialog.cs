using System.Drawing;
using System.Windows.Forms;
using TranslationBridge.Configuration;

namespace TranslationBridge.Services;

/// <summary>
/// Dialog for editing VAD (Voice Activity Detection) settings in real-time
/// </summary>
public class VadSettingsDialog : Form
{
    private readonly VadConfig _vadConfig;
    private readonly Action<VadConfig> _onSettingsChanged;

    // Threshold controls
    private TrackBar _speechThresholdTrack = null!;
    private Label _speechThresholdValue = null!;
    private TrackBar _silenceThresholdTrack = null!;
    private Label _silenceThresholdValue = null!;
    private TrackBar _autoRestartThresholdTrack = null!;
    private Label _autoRestartThresholdValue = null!;

    // Timing controls
    private TrackBar _silenceDurationTrack = null!;
    private Label _silenceDurationValue = null!;
    private TrackBar _minTurnDurationTrack = null!;
    private Label _minTurnDurationValue = null!;
    private TrackBar _maxTurnDurationTrack = null!;
    private Label _maxTurnDurationValue = null!;

    // Buffer controls
    private TrackBar _overlapDurationTrack = null!;
    private Label _overlapDurationValue = null!;
    private TrackBar _pendingBufferTrack = null!;
    private Label _pendingBufferValue = null!;

    // RMS meter
    private ProgressBar _rmsMeter = null!;
    private Label _rmsValueLabel = null!;
    private Label _turnStateLabel = null!;
    private System.Windows.Forms.Timer _updateTimer = null!;

    // Callback for live RMS updates
    private Func<float>? _getRmsCallback;
    private Func<string>? _getTurnStateCallback;

    public VadSettingsDialog(VadConfig vadConfig, Action<VadConfig> onSettingsChanged)
    {
        _vadConfig = vadConfig;
        _onSettingsChanged = onSettingsChanged;

        InitializeComponents();
        LoadCurrentSettings();
    }

    public void SetRmsCallbacks(Func<float> getRms, Func<string> getTurnState)
    {
        _getRmsCallback = getRms;
        _getTurnStateCallback = getTurnState;
    }

    private void InitializeComponents()
    {
        // Form setup
        Text = "VAD Settings - Voice Activity Detection";
        Size = new Size(550, 700);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(30, 30, 50);
        ForeColor = Color.White;

        int y = 15;

        // === RMS METER ===
        var rmsMeterLabel = CreateSectionLabel("Live RMS Level", y);
        Controls.Add(rmsMeterLabel);
        y += 25;

        _rmsMeter = new ProgressBar
        {
            Location = new Point(20, y),
            Size = new Size(490, 25),
            Style = ProgressBarStyle.Continuous,
            Maximum = 100
        };
        Controls.Add(_rmsMeter);
        y += 30;

        _rmsValueLabel = new Label
        {
            Text = "RMS: 0.0000",
            Location = new Point(20, y),
            Size = new Size(150, 20),
            ForeColor = Color.FromArgb(0, 212, 255)
        };
        Controls.Add(_rmsValueLabel);

        _turnStateLabel = new Label
        {
            Text = "State: IDLE",
            Location = new Point(360, y),
            Size = new Size(150, 20),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.TopRight
        };
        Controls.Add(_turnStateLabel);
        y += 35;

        // === THRESHOLDS SECTION ===
        var thresholdSection = CreateSectionLabel("Thresholds (RMS 0.0 - 1.0)", y);
        Controls.Add(thresholdSection);
        y += 30;

        // Speech Threshold
        (_speechThresholdTrack, _speechThresholdValue) = CreateTrackBarRow(
            "Speech Threshold:", y, 1, 50, 12,
            v => $"{v / 1000f:F3}",
            v => { _vadConfig.SpeechThreshold = v / 1000f; NotifyChange(); },
            "RMS level to start a turn (green)");
        y += 55;

        // Silence Threshold
        (_silenceThresholdTrack, _silenceThresholdValue) = CreateTrackBarRow(
            "Silence Threshold:", y, 1, 30, 6,
            v => $"{v / 1000f:F3}",
            v => { _vadConfig.SilenceThreshold = v / 1000f; NotifyChange(); },
            "RMS level considered silence (red)");
        y += 55;

        // Auto-Restart Threshold
        (_autoRestartThresholdTrack, _autoRestartThresholdValue) = CreateTrackBarRow(
            "Auto-Restart Threshold:", y, 1, 30, 9,
            v => $"{v / 1000f:F3}",
            v => { _vadConfig.AutoRestartThreshold = v / 1000f; NotifyChange(); },
            "RMS to auto-restart after turn_complete (yellow)");
        y += 55;

        // === TIMING SECTION ===
        var timingSection = CreateSectionLabel("Timing (milliseconds)", y);
        Controls.Add(timingSection);
        y += 30;

        // Silence Duration
        (_silenceDurationTrack, _silenceDurationValue) = CreateTrackBarRow(
            "Silence Duration:", y, 100, 1000, 350,
            v => $"{v} ms",
            v => { _vadConfig.SilenceDurationMs = v; NotifyChange(); },
            "Silence duration to trigger activity_end");
        y += 55;

        // Min Turn Duration
        (_minTurnDurationTrack, _minTurnDurationValue) = CreateTrackBarRow(
            "Min Turn Duration:", y, 300, 2000, 900,
            v => $"{v} ms",
            v => { _vadConfig.MinTurnDurationMs = v; NotifyChange(); },
            "Minimum turn length before allowing closure");
        y += 55;

        // Max Turn Duration
        (_maxTurnDurationTrack, _maxTurnDurationValue) = CreateTrackBarRow(
            "Max Turn Duration:", y, 2000, 15000, 5000,
            v => $"{v} ms",
            v => { _vadConfig.MaxTurnMs = v; NotifyChange(); },
            "Safety limit - closes only if near-silence");
        y += 55;

        // === BUFFER SECTION ===
        var bufferSection = CreateSectionLabel("Buffer Settings", y);
        Controls.Add(bufferSection);
        y += 30;

        // Overlap Duration
        (_overlapDurationTrack, _overlapDurationValue) = CreateTrackBarRow(
            "Overlap Duration:", y, 0, 500, 250,
            v => $"{v} ms",
            v => { _vadConfig.OverlapMs = v; NotifyChange(); },
            "Audio carried between turns for continuity");
        y += 55;

        // Pending Buffer
        (_pendingBufferTrack, _pendingBufferValue) = CreateTrackBarRow(
            "Pending Buffer:", y, 16, 128, 64,
            v => $"{v} KB",
            v => { _vadConfig.PendingMaxBytes = v * 1024; NotifyChange(); },
            "Max buffer during WAIT_COMPLETE (~2s = 64KB)");
        y += 55;

        // === PRESETS ===
        var presetSection = CreateSectionLabel("Presets", y);
        Controls.Add(presetSection);
        y += 30;

        var presetPanel = new Panel
        {
            Location = new Point(20, y),
            Size = new Size(490, 35),
            BackColor = Color.Transparent
        };
        Controls.Add(presetPanel);

        var presets = new[]
        {
            ("VoIP/Telephony", new VadPreset(0.012f, 0.006f, 0.009f, 350, 900, 5000, 250, 64)),
            ("Quiet", new VadPreset(0.008f, 0.003f, 0.005f, 400, 800, 6000, 300, 64)),
            ("Noisy", new VadPreset(0.025f, 0.015f, 0.020f, 300, 1000, 4000, 200, 64)),
            ("Fast", new VadPreset(0.015f, 0.008f, 0.012f, 250, 600, 3000, 150, 48))
        };

        int btnX = 0;
        foreach (var (name, preset) in presets)
        {
            var btn = new Button
            {
                Text = name,
                Location = new Point(btnX, 0),
                Size = new Size(115, 30),
                BackColor = Color.FromArgb(50, 50, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btn.Click += (s, e) => ApplyPreset(preset);
            presetPanel.Controls.Add(btn);
            btnX += 120;
        }

        y += 50;

        // === CLOSE BUTTON ===
        var closeButton = new Button
        {
            Text = "Chiudi",
            Location = new Point(400, y),
            Size = new Size(110, 35),
            BackColor = Color.FromArgb(0, 212, 255),
            ForeColor = Color.Black,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        closeButton.Click += (s, e) => Close();
        Controls.Add(closeButton);

        // Update timer for RMS meter
        _updateTimer = new System.Windows.Forms.Timer
        {
            Interval = 50  // 20 FPS
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();

        FormClosed += (s, e) => _updateTimer.Stop();
    }

    private Label CreateSectionLabel(string text, int y)
    {
        return new Label
        {
            Text = text,
            Location = new Point(20, y),
            Size = new Size(490, 20),
            ForeColor = Color.FromArgb(0, 212, 255),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
    }

    private (TrackBar, Label) CreateTrackBarRow(
        string labelText, int y, int min, int max, int defaultValue,
        Func<int, string> formatValue, Action<int> onChange, string tooltip)
    {
        var label = new Label
        {
            Text = labelText,
            Location = new Point(20, y),
            Size = new Size(150, 20),
            ForeColor = Color.LightGray
        };
        Controls.Add(label);

        var trackBar = new TrackBar
        {
            Location = new Point(170, y - 5),
            Size = new Size(280, 45),
            Minimum = min,
            Maximum = max,
            Value = defaultValue,
            TickFrequency = (max - min) / 10,
            BackColor = Color.FromArgb(30, 30, 50)
        };
        Controls.Add(trackBar);

        var valueLabel = new Label
        {
            Text = formatValue(defaultValue),
            Location = new Point(455, y),
            Size = new Size(70, 20),
            ForeColor = Color.FromArgb(0, 255, 136),
            TextAlign = ContentAlignment.TopRight
        };
        Controls.Add(valueLabel);

        var hintLabel = new Label
        {
            Text = tooltip,
            Location = new Point(20, y + 25),
            Size = new Size(490, 15),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f)
        };
        Controls.Add(hintLabel);

        trackBar.ValueChanged += (s, e) =>
        {
            valueLabel.Text = formatValue(trackBar.Value);
            onChange(trackBar.Value);
        };

        return (trackBar, valueLabel);
    }

    private void LoadCurrentSettings()
    {
        _speechThresholdTrack.Value = Math.Clamp((int)(_vadConfig.SpeechThreshold * 1000), 1, 50);
        _silenceThresholdTrack.Value = Math.Clamp((int)(_vadConfig.SilenceThreshold * 1000), 1, 30);
        _autoRestartThresholdTrack.Value = Math.Clamp((int)(_vadConfig.AutoRestartThreshold * 1000), 1, 30);
        _silenceDurationTrack.Value = Math.Clamp(_vadConfig.SilenceDurationMs, 100, 1000);
        _minTurnDurationTrack.Value = Math.Clamp(_vadConfig.MinTurnDurationMs, 300, 2000);
        _maxTurnDurationTrack.Value = Math.Clamp(_vadConfig.MaxTurnMs, 2000, 15000);
        _overlapDurationTrack.Value = Math.Clamp(_vadConfig.OverlapMs, 0, 500);
        _pendingBufferTrack.Value = Math.Clamp(_vadConfig.PendingMaxBytes / 1024, 16, 128);

        // Trigger value label updates
        _speechThresholdValue.Text = $"{_vadConfig.SpeechThreshold:F3}";
        _silenceThresholdValue.Text = $"{_vadConfig.SilenceThreshold:F3}";
        _autoRestartThresholdValue.Text = $"{_vadConfig.AutoRestartThreshold:F3}";
        _silenceDurationValue.Text = $"{_vadConfig.SilenceDurationMs} ms";
        _minTurnDurationValue.Text = $"{_vadConfig.MinTurnDurationMs} ms";
        _maxTurnDurationValue.Text = $"{_vadConfig.MaxTurnMs} ms";
        _overlapDurationValue.Text = $"{_vadConfig.OverlapMs} ms";
        _pendingBufferValue.Text = $"{_vadConfig.PendingMaxBytes / 1024} KB";
    }

    private void ApplyPreset(VadPreset preset)
    {
        _vadConfig.SpeechThreshold = preset.SpeechThreshold;
        _vadConfig.SilenceThreshold = preset.SilenceThreshold;
        _vadConfig.AutoRestartThreshold = preset.AutoRestartThreshold;
        _vadConfig.SilenceDurationMs = preset.SilenceDurationMs;
        _vadConfig.MinTurnDurationMs = preset.MinTurnDurationMs;
        _vadConfig.MaxTurnMs = preset.MaxTurnMs;
        _vadConfig.OverlapMs = preset.OverlapMs;
        _vadConfig.PendingMaxBytes = preset.PendingBufferKb * 1024;

        LoadCurrentSettings();
        NotifyChange();
    }

    private void NotifyChange()
    {
        _onSettingsChanged?.Invoke(_vadConfig);
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_getRmsCallback != null)
        {
            var rms = _getRmsCallback();
            _rmsMeter.Value = Math.Clamp((int)(rms * 1000), 0, 100);
            _rmsValueLabel.Text = $"RMS: {rms:F4}";
        }

        if (_getTurnStateCallback != null)
        {
            var state = _getTurnStateCallback();
            _turnStateLabel.Text = $"State: {state}";
            _turnStateLabel.ForeColor = state switch
            {
                "ACTIVE" => Color.FromArgb(0, 255, 136),
                "WAIT_COMPLETE" => Color.FromArgb(255, 215, 0),
                _ => Color.Gray
            };
        }
    }

    private record VadPreset(
        float SpeechThreshold,
        float SilenceThreshold,
        float AutoRestartThreshold,
        int SilenceDurationMs,
        int MinTurnDurationMs,
        int MaxTurnMs,
        int OverlapMs,
        int PendingBufferKb);
}
