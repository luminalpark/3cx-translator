using System.Drawing;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Text;

namespace TranslationBridge.Services;

/// <summary>
/// Dialog for testing translation quality and voice intonation.
/// Operator speaks, audio is translated, and result is played back.
/// </summary>
public class TestTranslationDialog : Form
{
    private readonly Label _instructionLabel;
    private readonly Label _statusLabel;
    private readonly Label _sourceTextLabel;
    private readonly TextBox _sourceTextBox;
    private readonly Label _translatedTextLabel;
    private readonly TextBox _translatedTextBox;
    private readonly Button _recordButton;
    private readonly Button _playButton;
    private readonly Button _closeButton;
    private readonly ProgressBar _levelMeter;
    private readonly Label _timerLabel;
    private readonly ComboBox _micCombo;
    private readonly ComboBox _speakerCombo;
    private readonly ComboBox _targetLangCombo;

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;
    private readonly MemoryStream _recordedAudio = new();
    private readonly System.Windows.Forms.Timer _recordTimer;
    private int _recordingSeconds;
    private bool _isRecording;

    private readonly SeamlessClient _client;
    private byte[]? _translatedAudio;
    private int _translatedSampleRate = 16000;

    private const int SampleRate = 16000;
    private const int MaxRecordingSeconds = 15;

    private static readonly Dictionary<string, string> TargetLanguages = new()
    {
        { "it", "Italiano" },
        { "en", "English" },
        { "de", "Deutsch" },
        { "fr", "Français" },
        { "es", "Español" },
        { "pt", "Português" },
        { "ru", "Русский" },
        { "zh", "中文" },
        { "ja", "日本語" },
        { "ko", "한국어" },
        { "ar", "العربية" },
        { "nl", "Nederlands" },
        { "pl", "Polski" },
        { "tr", "Türkçe" }
    };

    public TestTranslationDialog(SeamlessClient client)
    {
        _client = client;

        // Form setup
        Text = "Test Traduzione";
        Size = new Size(600, 550);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;

        int y = 15;

        // Instructions
        _instructionLabel = new Label
        {
            Text = "Parla nel microfono e ascolta la traduzione.\n" +
                   "Utile per testare qualità e intonazione della voce.",
            Location = new Point(20, y),
            Size = new Size(550, 40),
            Font = new Font("Segoe UI", 10f)
        };
        Controls.Add(_instructionLabel);
        y += 50;

        // Device selection row
        var micLabel = new Label
        {
            Text = "Microfono:",
            Location = new Point(20, y + 3),
            Size = new Size(80, 25),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(micLabel);

        _micCombo = new ComboBox
        {
            Location = new Point(100, y),
            Size = new Size(250, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        Controls.Add(_micCombo);

        var langLabel = new Label
        {
            Text = "Traduci in:",
            Location = new Point(360, y + 3),
            Size = new Size(70, 25),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(langLabel);

        _targetLangCombo = new ComboBox
        {
            Location = new Point(435, y),
            Size = new Size(130, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        foreach (var lang in TargetLanguages)
        {
            _targetLangCombo.Items.Add($"{lang.Value} ({lang.Key})");
        }
        _targetLangCombo.SelectedIndex = 0; // Italian default
        Controls.Add(_targetLangCombo);
        y += 35;

        // Speaker selection
        var speakerLabel = new Label
        {
            Text = "Altoparlante:",
            Location = new Point(20, y + 3),
            Size = new Size(80, 25),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(speakerLabel);

        _speakerCombo = new ComboBox
        {
            Location = new Point(100, y),
            Size = new Size(250, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        Controls.Add(_speakerCombo);
        y += 40;

        PopulateDevices();

        // Level meter
        var levelLabel = new Label
        {
            Text = "Livello:",
            Location = new Point(20, y + 2),
            Size = new Size(60, 20),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(levelLabel);

        _levelMeter = new ProgressBar
        {
            Location = new Point(80, y),
            Size = new Size(400, 22),
            Style = ProgressBarStyle.Continuous,
            Maximum = 100
        };
        Controls.Add(_levelMeter);

        _timerLabel = new Label
        {
            Text = "00:00",
            Location = new Point(490, y),
            Size = new Size(60, 22),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.DarkBlue,
            TextAlign = ContentAlignment.MiddleRight
        };
        Controls.Add(_timerLabel);
        y += 35;

        // Status
        _statusLabel = new Label
        {
            Text = "Premi REGISTRA per iniziare il test",
            Location = new Point(20, y),
            Size = new Size(550, 25),
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_statusLabel);
        y += 35;

        // Buttons
        var buttonPanel = new Panel
        {
            Location = new Point(20, y),
            Size = new Size(550, 50)
        };
        Controls.Add(buttonPanel);

        _recordButton = new Button
        {
            Text = "🎤 REGISTRA",
            Location = new Point(0, 0),
            Size = new Size(150, 45),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _recordButton.Click += RecordButton_Click;
        buttonPanel.Controls.Add(_recordButton);

        _playButton = new Button
        {
            Text = "🔊 ASCOLTA",
            Location = new Point(160, 0),
            Size = new Size(150, 45),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            BackColor = Color.FromArgb(40, 167, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
            Cursor = Cursors.Hand
        };
        _playButton.Click += PlayButton_Click;
        buttonPanel.Controls.Add(_playButton);

        _closeButton = new Button
        {
            Text = "Chiudi",
            Location = new Point(470, 0),
            Size = new Size(80, 45),
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(220, 220, 220),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _closeButton.Click += (s, e) => Close();
        buttonPanel.Controls.Add(_closeButton);
        y += 60;

        // Source text
        _sourceTextLabel = new Label
        {
            Text = "Testo originale (rilevato):",
            Location = new Point(20, y),
            Size = new Size(250, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        Controls.Add(_sourceTextLabel);
        y += 22;

        _sourceTextBox = new TextBox
        {
            Location = new Point(20, y),
            Size = new Size(545, 60),
            Multiline = true,
            ReadOnly = true,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(245, 245, 245),
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = ScrollBars.Vertical
        };
        Controls.Add(_sourceTextBox);
        y += 70;

        // Translated text
        _translatedTextLabel = new Label
        {
            Text = "Testo tradotto:",
            Location = new Point(20, y),
            Size = new Size(250, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        Controls.Add(_translatedTextLabel);
        y += 22;

        _translatedTextBox = new TextBox
        {
            Location = new Point(20, y),
            Size = new Size(545, 60),
            Multiline = true,
            ReadOnly = true,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(240, 255, 240),
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = ScrollBars.Vertical
        };
        Controls.Add(_translatedTextBox);
        y += 70;

        // Info
        var infoLabel = new Label
        {
            Text = "💡 Parla con chiarezza per 3-10 secondi. Il test usa la lingua auto-detect.",
            Location = new Point(20, y),
            Size = new Size(550, 25),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.Gray
        };
        Controls.Add(infoLabel);

        // Timer
        _recordTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _recordTimer.Tick += RecordTimer_Tick;

        // Cleanup
        FormClosing += (s, e) => CleanupResources();
    }

    private void PopulateDevices()
    {
        try
        {
            // Use WaveIn API for microphones (matches WaveInEvent device numbering)
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var caps = WaveIn.GetCapabilities(i);
                _micCombo.Items.Add(caps.ProductName);
            }
            if (_micCombo.Items.Count > 0)
            {
                _micCombo.SelectedIndex = 0; // First device is usually default
            }

            // Use WaveOut API for speakers (matches WaveOutEvent device numbering)
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                _speakerCombo.Items.Add(caps.ProductName);
            }
            if (_speakerCombo.Items.Count > 0)
            {
                _speakerCombo.SelectedIndex = 0; // First device is usually default
            }
        }
        catch
        {
            _micCombo.Items.Add("(Microfono predefinito)");
            _micCombo.SelectedIndex = 0;
            _speakerCombo.Items.Add("(Altoparlante predefinito)");
            _speakerCombo.SelectedIndex = 0;
        }
    }

    private void RecordButton_Click(object? sender, EventArgs e)
    {
        if (_isRecording)
        {
            StopRecordingAndTranslate();
        }
        else
        {
            StartRecording();
        }
    }

    private void StartRecording()
    {
        try
        {
            _recordedAudio.SetLength(0);
            _sourceTextBox.Clear();
            _translatedTextBox.Clear();
            _translatedAudio = null;
            _playButton.Enabled = false;

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                DeviceNumber = _micCombo.SelectedIndex
            };

            _waveIn.DataAvailable += WaveIn_DataAvailable;
            _waveIn.RecordingStopped += WaveIn_RecordingStopped;

            _waveIn.StartRecording();
            _isRecording = true;
            _recordingSeconds = 0;

            _recordButton.Text = "⏹ STOP";
            _recordButton.BackColor = Color.FromArgb(108, 117, 125);
            _micCombo.Enabled = false;
            _speakerCombo.Enabled = false;
            _targetLangCombo.Enabled = false;
            _statusLabel.Text = "🔴 Registrazione in corso... Parla adesso!";
            _statusLabel.ForeColor = Color.Red;

            _recordTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Errore avvio registrazione:\n{ex.Message}", "Errore",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        _recordedAudio.Write(e.Buffer, 0, e.BytesRecorded);

        // Level meter
        float maxLevel = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            var sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            var level = Math.Abs(sample / 32768f);
            if (level > maxLevel) maxLevel = level;
        }

        BeginInvoke(() => _levelMeter.Value = Math.Min(100, (int)(maxLevel * 100)));
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Recording stopped
    }

    private void RecordTimer_Tick(object? sender, EventArgs e)
    {
        _recordingSeconds++;
        _timerLabel.Text = $"{_recordingSeconds / 60:00}:{_recordingSeconds % 60:00}";

        if (_recordingSeconds < 3)
            _timerLabel.ForeColor = Color.Orange;
        else if (_recordingSeconds <= 10)
            _timerLabel.ForeColor = Color.Green;
        else
            _timerLabel.ForeColor = Color.DarkGreen;

        if (_recordingSeconds >= MaxRecordingSeconds)
        {
            StopRecordingAndTranslate();
        }
    }

    private async void StopRecordingAndTranslate()
    {
        _recordTimer.Stop();
        _waveIn?.StopRecording();
        _isRecording = false;
        _levelMeter.Value = 0;

        _recordButton.Text = "🎤 REGISTRA";
        _recordButton.BackColor = Color.FromArgb(220, 53, 69);
        _recordButton.Enabled = false;

        if (_recordingSeconds < 1)
        {
            _statusLabel.Text = "⚠️ Registrazione troppo breve. Riprova.";
            _statusLabel.ForeColor = Color.Orange;
            _recordButton.Enabled = true;
            _micCombo.Enabled = true;
            _speakerCombo.Enabled = true;
            _targetLangCombo.Enabled = true;
            return;
        }

        _statusLabel.Text = "⏳ Traduzione in corso...";
        _statusLabel.ForeColor = Color.Blue;

        try
        {
            // Get target language from combo - parse from format "English (en)"
            var selectedItem = _targetLangCombo.SelectedItem?.ToString() ?? "Italiano (it)";
            var targetLang = "it"; // default
            var match = System.Text.RegularExpressions.Regex.Match(selectedItem, @"\((\w+)\)$");
            if (match.Success)
            {
                targetLang = match.Groups[1].Value;
            }

            // Check if client is connected
            if (!_client.IsConnected)
            {
                _statusLabel.Text = "❌ Server non connesso. Avvia la traduzione prima.";
                _statusLabel.ForeColor = Color.Red;
                _recordButton.Enabled = true;
                _micCombo.Enabled = true;
                _speakerCombo.Enabled = true;
                _targetLangCombo.Enabled = true;
                return;
            }

            // Configure for test translation
            await _client.ConfigureLanguagesAsync("auto", targetLang);

            // Setup event handlers for this translation
            var translationComplete = new TaskCompletionSource<bool>();
            string? sourceText = null;
            string? translatedText = null;
            byte[]? audioData = null;

            Task OnTranslation(TranslationResult result)
            {
                sourceText = result.SourceText;
                translatedText = result.TranslatedText;
                _translatedSampleRate = result.AudioSampleRate;

                if (result.DetectedLanguage != null)
                {
                    BeginInvoke(() =>
                    {
                        _sourceTextLabel.Text = $"Testo originale (lingua: {result.DetectedLanguage}):";
                    });
                }
                return Task.CompletedTask;
            }

            Task OnAudio(byte[] audio)
            {
                audioData = audio;
                translationComplete.TrySetResult(true);
                return Task.CompletedTask;
            }

            Task OnSkipped(SkippedResult result)
            {
                sourceText = $"[Stessa lingua rilevata: {result.DetectedLanguageName}]";
                translatedText = "[Traduzione saltata - stessa lingua]";
                translationComplete.TrySetResult(true);
                return Task.CompletedTask;
            }

            _client.OnTranslationReceived += OnTranslation;
            _client.OnAudioReceived += OnAudio;
            _client.OnTranslationSkipped += OnSkipped;

            try
            {
                // Send audio
                var audioBytes = _recordedAudio.ToArray();
                await _client.SendAudioChunkAsync(audioBytes);
                await _client.RequestTranslationAsync();

                // Wait for result with timeout
                var timeoutTask = Task.Delay(30000);
                var completedTask = await Task.WhenAny(translationComplete.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException("Timeout durante la traduzione");
                }

                // Update UI
                BeginInvoke(() =>
                {
                    _sourceTextBox.Text = sourceText ?? "(nessun testo rilevato)";
                    _translatedTextBox.Text = translatedText ?? "(nessuna traduzione)";

                    if (audioData != null && audioData.Length > 0)
                    {
                        _translatedAudio = audioData;
                        _playButton.Enabled = true;
                        _statusLabel.Text = $"✅ Traduzione completata! Premi ASCOLTA per sentire.";
                        _statusLabel.ForeColor = Color.Green;
                    }
                    else
                    {
                        _statusLabel.Text = "⚠️ Traduzione completata ma nessun audio generato.";
                        _statusLabel.ForeColor = Color.Orange;
                    }
                });
            }
            finally
            {
                _client.OnTranslationReceived -= OnTranslation;
                _client.OnAudioReceived -= OnAudio;
                _client.OnTranslationSkipped -= OnSkipped;
            }
        }
        catch (Exception ex)
        {
            BeginInvoke(() =>
            {
                _statusLabel.Text = $"❌ Errore: {ex.Message}";
                _statusLabel.ForeColor = Color.Red;
            });
        }
        finally
        {
            BeginInvoke(() =>
            {
                _recordButton.Enabled = true;
                _micCombo.Enabled = true;
                _speakerCombo.Enabled = true;
                _targetLangCombo.Enabled = true;
            });
        }
    }

    private void PlayButton_Click(object? sender, EventArgs e)
    {
        if (_translatedAudio == null || _translatedAudio.Length == 0)
            return;

        try
        {
            StopPlayback();

            // Convert bytes to float samples
            var samples = new float[_translatedAudio.Length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                var sample = (short)(_translatedAudio[i * 2] | (_translatedAudio[i * 2 + 1] << 8));
                samples[i] = sample / 32768f;
            }

            // Create wave provider
            var format = new WaveFormat(_translatedSampleRate, 16, 1);
            _playbackBuffer = new BufferedWaveProvider(format)
            {
                BufferLength = _translatedAudio.Length * 2,
                DiscardOnBufferOverflow = true
            };
            _playbackBuffer.AddSamples(_translatedAudio, 0, _translatedAudio.Length);

            // Use selected index directly (matches WaveOut device numbering)
            int deviceNumber = _speakerCombo.SelectedIndex;

            _waveOut = new WaveOutEvent { DeviceNumber = deviceNumber };
            _waveOut.Init(_playbackBuffer);
            _waveOut.PlaybackStopped += (s, args) =>
            {
                BeginInvoke(() =>
                {
                    _playButton.Text = "🔊 ASCOLTA";
                    _playButton.BackColor = Color.FromArgb(40, 167, 69);
                });
            };

            _waveOut.Play();
            _playButton.Text = "⏹ STOP";
            _playButton.BackColor = Color.FromArgb(108, 117, 125);
            _statusLabel.Text = "🔊 Riproduzione in corso...";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Errore riproduzione:\n{ex.Message}", "Errore",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopPlayback()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _playbackBuffer = null;
    }

    private void CleanupResources()
    {
        _recordTimer.Stop();
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        StopPlayback();
        _recordedAudio.Dispose();
    }
}
