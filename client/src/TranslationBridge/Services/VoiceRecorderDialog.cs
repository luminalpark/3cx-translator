using System.Drawing;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TranslationBridge.Services;

/// <summary>
/// Dialog for recording operator's voice for voice cloning.
/// Records 5-10 seconds of speech at 16kHz mono.
/// </summary>
public class VoiceRecorderDialog : Form
{
    private readonly Label _instructionLabel;
    private readonly TextBox _sampleTextBox;
    private readonly Button _recordButton;
    private readonly Button _stopButton;
    private readonly Button _playButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly ProgressBar _levelMeter;
    private readonly Label _timerLabel;
    private readonly Label _statusLabel;
    private readonly ComboBox _deviceCombo;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private WaveOutEvent? _waveOut;
    private AudioFileReader? _audioReader;

    private readonly string _tempFilePath;
    private readonly System.Windows.Forms.Timer _recordTimer;
    private int _recordingSeconds;
    private bool _isRecording;
    private bool _hasRecording;

    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int MaxRecordingSeconds = 15;
    private const int MinRecordingSeconds = 3;

    // Sample texts in Italian for the operator to read
    private static readonly string[] SampleTexts = new[]
    {
        "Buongiorno, sono l'operatore del servizio clienti.\n" +
        "Come posso aiutarla oggi?\n" +
        "La prego di descrivere il suo problema\n" +
        "e faremo del nostro meglio per risolverlo.",

        "Salve, grazie per aver chiamato il nostro servizio.\n" +
        "Mi chiamo Carlo e sono qui per assisterla.\n" +
        "Per favore mi dica come posso essere utile\n" +
        "e troveremo insieme una soluzione.",

        "Benvenuto al servizio di assistenza clienti.\n" +
        "La ringrazio per la sua pazienza.\n" +
        "Ora verifico la sua richiesta nel sistema\n" +
        "e le fornisco tutte le informazioni necessarie."
    };

    public string? RecordedFilePath { get; private set; }

    public VoiceRecorderDialog()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"voice_recording_{Guid.NewGuid():N}.wav");

        // Form setup
        Text = "Registra la tua Voce";
        Size = new Size(550, 480);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;

        // Instructions
        _instructionLabel = new Label
        {
            Text = "Leggi il testo qui sotto con voce chiara e naturale.\n" +
                   "La registrazione deve durare almeno 5 secondi.",
            Location = new Point(20, 15),
            Size = new Size(500, 40),
            Font = new Font("Segoe UI", 10f)
        };
        Controls.Add(_instructionLabel);

        // Microphone selection
        var deviceLabel = new Label
        {
            Text = "Microfono:",
            Location = new Point(20, 60),
            Size = new Size(80, 25),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(deviceLabel);

        _deviceCombo = new ComboBox
        {
            Location = new Point(100, 57),
            Size = new Size(350, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        PopulateDevices();
        Controls.Add(_deviceCombo);

        // Sample text box
        var textLabel = new Label
        {
            Text = "Testo da leggere:",
            Location = new Point(20, 95),
            Size = new Size(150, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        Controls.Add(textLabel);

        _sampleTextBox = new TextBox
        {
            Location = new Point(20, 118),
            Size = new Size(495, 120),
            Multiline = true,
            ReadOnly = true,
            Font = new Font("Segoe UI", 11f),
            BackColor = Color.FromArgb(250, 250, 240),
            BorderStyle = BorderStyle.FixedSingle,
            Text = SampleTexts[new Random().Next(SampleTexts.Length)]
        };
        Controls.Add(_sampleTextBox);

        // Level meter
        var levelLabel = new Label
        {
            Text = "Livello audio:",
            Location = new Point(20, 250),
            Size = new Size(100, 20),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(levelLabel);

        _levelMeter = new ProgressBar
        {
            Location = new Point(120, 248),
            Size = new Size(300, 22),
            Style = ProgressBarStyle.Continuous,
            Maximum = 100
        };
        Controls.Add(_levelMeter);

        // Timer label
        _timerLabel = new Label
        {
            Text = "00:00",
            Location = new Point(430, 248),
            Size = new Size(60, 22),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.DarkBlue,
            TextAlign = ContentAlignment.MiddleRight
        };
        Controls.Add(_timerLabel);

        // Status label
        _statusLabel = new Label
        {
            Text = "Premi REGISTRA per iniziare",
            Location = new Point(20, 280),
            Size = new Size(495, 25),
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_statusLabel);

        // Buttons panel
        var buttonPanel = new Panel
        {
            Location = new Point(20, 315),
            Size = new Size(495, 50)
        };
        Controls.Add(buttonPanel);

        _recordButton = new Button
        {
            Text = "🔴 REGISTRA",
            Location = new Point(0, 0),
            Size = new Size(115, 45),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _recordButton.Click += RecordButton_Click;
        buttonPanel.Controls.Add(_recordButton);

        _stopButton = new Button
        {
            Text = "⏹ STOP",
            Location = new Point(125, 0),
            Size = new Size(100, 45),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            BackColor = Color.FromArgb(108, 117, 125),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
            Cursor = Cursors.Hand
        };
        _stopButton.Click += StopButton_Click;
        buttonPanel.Controls.Add(_stopButton);

        _playButton = new Button
        {
            Text = "▶ Ascolta",
            Location = new Point(235, 0),
            Size = new Size(100, 45),
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(23, 162, 184),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
            Cursor = Cursors.Hand
        };
        _playButton.Click += PlayButton_Click;
        buttonPanel.Controls.Add(_playButton);

        _saveButton = new Button
        {
            Text = "💾 Salva",
            Location = new Point(345, 0),
            Size = new Size(70, 45),
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(40, 167, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
            Cursor = Cursors.Hand
        };
        _saveButton.Click += SaveButton_Click;
        buttonPanel.Controls.Add(_saveButton);

        _cancelButton = new Button
        {
            Text = "Annulla",
            Location = new Point(425, 0),
            Size = new Size(70, 45),
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(220, 220, 220),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        buttonPanel.Controls.Add(_cancelButton);

        // Tips section
        var tipsLabel = new Label
        {
            Text = "💡 Suggerimenti:\n" +
                   "• Parla con il tuo tono naturale\n" +
                   "• Evita rumori di fondo\n" +
                   "• Mantieni una distanza costante dal microfono",
            Location = new Point(20, 375),
            Size = new Size(495, 70),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(100, 100, 100)
        };
        Controls.Add(tipsLabel);

        // Timer for recording
        _recordTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _recordTimer.Tick += RecordTimer_Tick;

        // Cleanup on close
        FormClosing += (s, e) => CleanupResources();
    }

    private void PopulateDevices()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            foreach (var device in devices)
            {
                _deviceCombo.Items.Add(device.FriendlyName);
            }

            if (_deviceCombo.Items.Count > 0)
            {
                // Try to select default device
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                var defaultIndex = _deviceCombo.Items.IndexOf(defaultDevice.FriendlyName);
                _deviceCombo.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;
            }
        }
        catch
        {
            _deviceCombo.Items.Add("(Microfono predefinito)");
            _deviceCombo.SelectedIndex = 0;
        }
    }

    private void RecordButton_Click(object? sender, EventArgs e)
    {
        StartRecording();
    }

    private void StopButton_Click(object? sender, EventArgs e)
    {
        StopRecording();
    }

    private void PlayButton_Click(object? sender, EventArgs e)
    {
        PlayRecording();
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        SaveRecording();
    }

    private void StartRecording()
    {
        try
        {
            // Clean up any previous recording
            CleanupRecordingResources();

            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }

            // Setup recording
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                DeviceNumber = _deviceCombo.SelectedIndex
            };

            _writer = new WaveFileWriter(_tempFilePath, _waveIn.WaveFormat);

            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.RecordingStopped += WaveIn_RecordingStopped;

            _waveIn.StartRecording();
            _isRecording = true;
            _hasRecording = false;
            _recordingSeconds = 0;

            // Update UI
            _recordButton.Enabled = false;
            _stopButton.Enabled = true;
            _playButton.Enabled = false;
            _saveButton.Enabled = false;
            _deviceCombo.Enabled = false;
            _statusLabel.Text = "🔴 Registrazione in corso... Leggi il testo!";
            _statusLabel.ForeColor = Color.Red;

            _recordTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Errore nell'avvio della registrazione:\n{ex.Message}",
                "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            CleanupRecordingResources();
        }
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        // Write to file
        _writer?.Write(e.Buffer, 0, e.BytesRecorded);

        // Calculate level for meter
        float maxLevel = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            var sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            var level = Math.Abs(sample / 32768f);
            if (level > maxLevel) maxLevel = level;
        }

        // Update level meter (on UI thread)
        BeginInvoke(() =>
        {
            _levelMeter.Value = Math.Min(100, (int)(maxLevel * 100));
        });
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        _writer?.Dispose();
        _writer = null;
    }

    private void RecordTimer_Tick(object? sender, EventArgs e)
    {
        _recordingSeconds++;
        _timerLabel.Text = $"{_recordingSeconds / 60:00}:{_recordingSeconds % 60:00}";

        // Change timer color based on duration
        if (_recordingSeconds < MinRecordingSeconds)
        {
            _timerLabel.ForeColor = Color.Orange;
        }
        else if (_recordingSeconds <= 10)
        {
            _timerLabel.ForeColor = Color.Green;
        }
        else
        {
            _timerLabel.ForeColor = Color.DarkGreen;
        }

        // Auto-stop after max duration
        if (_recordingSeconds >= MaxRecordingSeconds)
        {
            StopRecording();
            _statusLabel.Text = "Registrazione completata (durata massima raggiunta)";
        }
    }

    private void StopRecording()
    {
        if (!_isRecording) return;

        _recordTimer.Stop();
        _waveIn?.StopRecording();
        _isRecording = false;

        // Update UI
        _recordButton.Enabled = true;
        _stopButton.Enabled = false;
        _deviceCombo.Enabled = true;
        _levelMeter.Value = 0;

        if (_recordingSeconds >= MinRecordingSeconds)
        {
            _hasRecording = true;
            _playButton.Enabled = true;
            _saveButton.Enabled = true;
            _statusLabel.Text = $"✅ Registrazione completata ({_recordingSeconds} secondi). Ascolta o salva.";
            _statusLabel.ForeColor = Color.Green;
        }
        else
        {
            _statusLabel.Text = $"⚠️ Registrazione troppo breve ({_recordingSeconds}s). Minimo {MinRecordingSeconds} secondi.";
            _statusLabel.ForeColor = Color.Orange;
            _hasRecording = false;
        }

        CleanupRecordingResources();
    }

    private void PlayRecording()
    {
        if (!_hasRecording || !File.Exists(_tempFilePath)) return;

        try
        {
            // Stop any current playback
            StopPlayback();

            _audioReader = new AudioFileReader(_tempFilePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioReader);
            _waveOut.PlaybackStopped += (s, e) =>
            {
                BeginInvoke(() =>
                {
                    _playButton.Text = "▶ Ascolta";
                    _playButton.BackColor = Color.FromArgb(23, 162, 184);
                });
                StopPlayback();
            };

            _waveOut.Play();
            _playButton.Text = "⏹ Stop";
            _playButton.BackColor = Color.FromArgb(220, 53, 69);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Errore nella riproduzione:\n{ex.Message}",
                "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopPlayback()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _audioReader?.Dispose();
        _audioReader = null;
    }

    private void SaveRecording()
    {
        if (!_hasRecording || !File.Exists(_tempFilePath)) return;

        using var dialog = new SaveFileDialog
        {
            Title = "Salva registrazione voce",
            Filter = "File WAV|*.wav",
            DefaultExt = "wav",
            FileName = $"my_voice_{DateTime.Now:yyyyMMdd_HHmmss}.wav",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                File.Copy(_tempFilePath, dialog.FileName, true);
                RecordedFilePath = dialog.FileName;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Errore nel salvataggio:\n{ex.Message}",
                    "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void CleanupRecordingResources()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;

        _writer?.Dispose();
        _writer = null;
    }

    private void CleanupResources()
    {
        _recordTimer.Stop();
        CleanupRecordingResources();
        StopPlayback();

        // Delete temp file if not saved
        if (RecordedFilePath == null && File.Exists(_tempFilePath))
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }
}
