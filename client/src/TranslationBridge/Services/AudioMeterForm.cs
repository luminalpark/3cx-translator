using System.Drawing;
using System.Windows.Forms;

namespace TranslationBridge.Services;

/// <summary>
/// Floating VU Meter window for monitoring audio levels
/// Shows real-time RMS for mic capture and playback
/// </summary>
public class AudioMeterForm : Form
{
    private ProgressBar _micMeter = null!;
    private ProgressBar _playbackMeter = null!;
    private Label _micLabel = null!;
    private Label _playbackLabel = null!;
    private Label _micValueLabel = null!;
    private Label _playbackValueLabel = null!;
    private Label _vadStateLabel = null!;
    private Label _statsLabel = null!;

    // Stats
    private int _micChunks;
    private int _playbackChunks;
    private float _lastMicRms;
    private float _lastPlaybackRms;

    public AudioMeterForm()
    {
        InitializeComponents();
    }

    private void InitializeComponents()
    {
        // Form settings
        Text = "Audio Meter - Outbound Debug";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(280, 220);
        TopMost = true;
        ShowInTaskbar = false;

        // Position in top-right corner
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(screen.Right - Width - 20, screen.Top + 20);

        // Background
        BackColor = Color.FromArgb(40, 40, 40);

        // === MIC IN Section ===
        _micLabel = new Label
        {
            Text = "MIC IN (Operatore)",
            ForeColor = Color.LightGreen,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(10, 10),
            Size = new Size(150, 20)
        };
        Controls.Add(_micLabel);

        _micValueLabel = new Label
        {
            Text = "RMS: 0.0000",
            ForeColor = Color.White,
            Font = new Font("Consolas", 8),
            Location = new Point(160, 10),
            Size = new Size(100, 20),
            TextAlign = ContentAlignment.MiddleRight
        };
        Controls.Add(_micValueLabel);

        _micMeter = new ProgressBar
        {
            Location = new Point(10, 32),
            Size = new Size(250, 20),
            Minimum = 0,
            Maximum = 1000,
            Value = 0,
            Style = ProgressBarStyle.Continuous
        };
        Controls.Add(_micMeter);

        // === PLAYBACK OUT Section ===
        _playbackLabel = new Label
        {
            Text = "PLAYBACK OUT (→3CX)",
            ForeColor = Color.Orange,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(10, 60),
            Size = new Size(150, 20)
        };
        Controls.Add(_playbackLabel);

        _playbackValueLabel = new Label
        {
            Text = "RMS: 0.0000",
            ForeColor = Color.White,
            Font = new Font("Consolas", 8),
            Location = new Point(160, 60),
            Size = new Size(100, 20),
            TextAlign = ContentAlignment.MiddleRight
        };
        Controls.Add(_playbackValueLabel);

        _playbackMeter = new ProgressBar
        {
            Location = new Point(10, 82),
            Size = new Size(250, 20),
            Minimum = 0,
            Maximum = 1000,
            Value = 0,
            Style = ProgressBarStyle.Continuous
        };
        Controls.Add(_playbackMeter);

        // === VAD State ===
        _vadStateLabel = new Label
        {
            Text = "VAD: IDLE",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(10, 115),
            Size = new Size(250, 25),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(60, 60, 60)
        };
        Controls.Add(_vadStateLabel);

        // === Stats ===
        _statsLabel = new Label
        {
            Text = "Chunks: MIC 0 | PLAY 0",
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 8),
            Location = new Point(10, 150),
            Size = new Size(250, 20),
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_statsLabel);

        // === Legend ===
        var legendLabel = new Label
        {
            Text = "Se MIC mostra attività quando parli = OK",
            ForeColor = Color.DarkGray,
            Font = new Font("Segoe UI", 7),
            Location = new Point(10, 175),
            Size = new Size(250, 30),
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(legendLabel);
    }

    /// <summary>
    /// Update mic capture RMS level
    /// </summary>
    public void UpdateMicRms(float rms)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateMicRms(rms));
            return;
        }

        _lastMicRms = rms;
        _micChunks++;

        // Scale RMS to meter (0-1000)
        var scaledValue = (int)(rms * 5000); // Amplify for visibility
        _micMeter.Value = Math.Min(scaledValue, 1000);
        _micValueLabel.Text = $"RMS: {rms:F4}";

        // Change color based on level
        _micLabel.ForeColor = rms > 0.01f ? Color.LimeGreen : Color.Gray;

        UpdateStats();
    }

    /// <summary>
    /// Update playback RMS level
    /// </summary>
    public void UpdatePlaybackRms(float rms)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdatePlaybackRms(rms));
            return;
        }

        _lastPlaybackRms = rms;
        _playbackChunks++;

        // Scale RMS to meter (0-1000)
        var scaledValue = (int)(rms * 5000);
        _playbackMeter.Value = Math.Min(scaledValue, 1000);
        _playbackValueLabel.Text = $"RMS: {rms:F4}";

        // Change color based on level
        _playbackLabel.ForeColor = rms > 0.01f ? Color.Orange : Color.Gray;

        UpdateStats();
    }

    /// <summary>
    /// Update VAD state display
    /// </summary>
    public void UpdateVadState(string state)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateVadState(state));
            return;
        }

        _vadStateLabel.Text = $"VAD: {state}";
        _vadStateLabel.ForeColor = state switch
        {
            "ACTIVE" => Color.LimeGreen,
            "WAIT_COMPLETE" => Color.Yellow,
            _ => Color.Gray
        };
        _vadStateLabel.BackColor = state switch
        {
            "ACTIVE" => Color.FromArgb(0, 80, 0),
            "WAIT_COMPLETE" => Color.FromArgb(80, 80, 0),
            _ => Color.FromArgb(60, 60, 60)
        };
    }

    private void UpdateStats()
    {
        _statsLabel.Text = $"Chunks: MIC {_micChunks} | PLAY {_playbackChunks}";
    }

    /// <summary>
    /// Reset all counters and meters
    /// </summary>
    public void Reset()
    {
        if (InvokeRequired)
        {
            BeginInvoke(Reset);
            return;
        }

        _micChunks = 0;
        _playbackChunks = 0;
        _micMeter.Value = 0;
        _playbackMeter.Value = 0;
        _micValueLabel.Text = "RMS: 0.0000";
        _playbackValueLabel.Text = "RMS: 0.0000";
        _vadStateLabel.Text = "VAD: IDLE";
        _vadStateLabel.ForeColor = Color.Gray;
        _vadStateLabel.BackColor = Color.FromArgb(60, 60, 60);
        UpdateStats();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Hide instead of close
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}
