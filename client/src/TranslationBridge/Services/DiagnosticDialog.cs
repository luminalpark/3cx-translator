using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using TranslationBridge.Configuration;

namespace TranslationBridge.Services;

/// <summary>
/// Dialog for running streaming diagnostic tests
/// </summary>
public class DiagnosticDialog : Form
{
    private readonly TextBox _logTextBox;
    private readonly Label _statsLabel;
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly Button _openOutputButton;
    private readonly Button _closeButton;
    private readonly ProgressBar _progressBar;
    private readonly Label _inputFileLabel;
    private readonly CheckBox _prerollCheckBox;

    private readonly BridgeConfig _config;
    private StreamingDiagnostic? _diagnostic;
    private CancellationTokenSource? _cts;
    private string? _inputFilePath;
    private string? _outputFolder;

    public DiagnosticDialog(BridgeConfig config)
    {
        _config = config;

        // Form setup
        Text = "Diagnostica Streaming Translation";
        Size = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(600, 400);
        BackColor = Color.White;

        int y = 15;

        // Instructions
        var instructionLabel = new Label
        {
            Text = "Questo test invia un file audio al server e registra tutti gli eventi e l'audio ricevuto.\n" +
                   "Utile per diagnosticare problemi di traduzione frammentata.",
            Location = new Point(20, y),
            Size = new Size(740, 40),
            Font = new Font("Segoe UI", 10f)
        };
        Controls.Add(instructionLabel);
        y += 50;

        // Input file selection
        var selectFileButton = new Button
        {
            Text = "Seleziona File Audio...",
            Location = new Point(20, y),
            Size = new Size(180, 30),
            Font = new Font("Segoe UI", 9f)
        };
        selectFileButton.Click += SelectFileButton_Click;
        Controls.Add(selectFileButton);

        _inputFileLabel = new Label
        {
            Text = "(nessun file selezionato)",
            Location = new Point(210, y + 5),
            Size = new Size(550, 25),
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.Gray
        };
        Controls.Add(_inputFileLabel);
        y += 40;

        // Buttons
        _runButton = new Button
        {
            Text = "Avvia Test",
            Location = new Point(20, y),
            Size = new Size(120, 35),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            BackColor = Color.FromArgb(40, 167, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _runButton.Click += RunButton_Click;
        Controls.Add(_runButton);

        _stopButton = new Button
        {
            Text = "Stop",
            Location = new Point(150, y),
            Size = new Size(100, 35),
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(220, 53, 69),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _stopButton.Click += StopButton_Click;
        Controls.Add(_stopButton);

        _openOutputButton = new Button
        {
            Text = "Apri Output",
            Location = new Point(260, y),
            Size = new Size(120, 35),
            Font = new Font("Segoe UI", 10f),
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _openOutputButton.Click += OpenOutputButton_Click;
        Controls.Add(_openOutputButton);

        _closeButton = new Button
        {
            Text = "Chiudi",
            Location = new Point(660, y),
            Size = new Size(100, 35),
            Font = new Font("Segoe UI", 10f),
            FlatStyle = FlatStyle.Flat
        };
        _closeButton.Click += (s, e) => Close();
        Controls.Add(_closeButton);
        y += 45;

        // Preroll checkbox
        _prerollCheckBox = new CheckBox
        {
            Text = "Abilita preroll buffer (1000ms) - Riproduce audio con buffering",
            Location = new Point(20, y),
            Size = new Size(500, 25),
            Font = new Font("Segoe UI", 9f),
            Checked = true  // Enabled by default
        };
        Controls.Add(_prerollCheckBox);
        y += 30;

        // Stats label
        _statsLabel = new Label
        {
            Text = "Pronto per il test",
            Location = new Point(20, y),
            Size = new Size(740, 25),
            Font = new Font("Consolas", 9f),
            ForeColor = Color.DarkBlue
        };
        Controls.Add(_statsLabel);
        y += 30;

        // Progress bar
        _progressBar = new ProgressBar
        {
            Location = new Point(20, y),
            Size = new Size(740, 20),
            Style = ProgressBarStyle.Marquee,
            Visible = false
        };
        Controls.Add(_progressBar);
        y += 30;

        // Log text box
        var logLabel = new Label
        {
            Text = "Log in tempo reale:",
            Location = new Point(20, y),
            Size = new Size(200, 20),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        Controls.Add(logLabel);
        y += 22;

        _logTextBox = new TextBox
        {
            Location = new Point(20, y),
            Size = new Size(740, 380),
            Multiline = true,
            ReadOnly = true,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.Lime,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };
        Controls.Add(_logTextBox);

        // Set output folder to logs directory
        _outputFolder = Path.Combine(AppContext.BaseDirectory, "logs", "diagnostic");
        Directory.CreateDirectory(_outputFolder);

        // Cleanup
        FormClosing += (s, e) =>
        {
            _cts?.Cancel();
            _diagnostic?.Dispose();
        };
    }

    private void SelectFileButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Seleziona file audio per il test diagnostico",
            Filter = "File WAV|*.wav|Tutti i file audio|*.wav;*.mp3|Tutti i file|*.*",
            FilterIndex = 1,
            CheckFileExists = true
        };

        // Try to find default location (tools folder)
        var toolsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools");
        if (Directory.Exists(toolsPath))
        {
            dialog.InitialDirectory = Path.GetFullPath(toolsPath);
        }

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _inputFilePath = dialog.FileName;
            _inputFileLabel.Text = Path.GetFileName(_inputFilePath);
            _inputFileLabel.ForeColor = Color.Black;
            _runButton.Enabled = true;
        }
    }

    private async void RunButton_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_inputFilePath) || !File.Exists(_inputFilePath))
        {
            MessageBox.Show("Seleziona un file audio valido", "Errore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Clear log
        _logTextBox.Clear();
        _runButton.Enabled = false;
        _stopButton.Enabled = true;
        _openOutputButton.Enabled = false;
        _progressBar.Visible = true;

        _cts = new CancellationTokenSource();

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        _diagnostic = new StreamingDiagnostic(
            loggerFactory.CreateLogger<StreamingDiagnostic>(),
            _config);

        // Enable preroll if checkbox is checked
        if (_prerollCheckBox.Checked)
        {
            _diagnostic.EnablePrerollPlayback(prerollMs: 1000);
            AppendLog("Preroll buffer abilitato: 1000ms");
        }
        else
        {
            AppendLog("Preroll buffer disabilitato (solo salvataggio WAV)");
        }

        _diagnostic.OnLogMessage += message =>
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => AppendLog(message));
            }
            else
            {
                AppendLog(message);
            }
        };

        _diagnostic.OnStatsUpdated += stats =>
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => UpdateStats(stats));
            }
            else
            {
                UpdateStats(stats);
            }
        };

        _diagnostic.OnComplete += () =>
        {
            if (InvokeRequired)
            {
                BeginInvoke(OnTestComplete);
            }
            else
            {
                OnTestComplete();
            }
        };

        try
        {
            await _diagnostic.RunDiagnosticAsync(_inputFilePath, _outputFolder!, _cts.Token);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRORE: {ex.Message}");
        }
    }

    private void StopButton_Click(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _stopButton.Enabled = false;
        AppendLog("=== TEST INTERROTTO ===");
    }

    private void OpenOutputButton_Click(object? sender, EventArgs e)
    {
        if (_diagnostic?.OutputWavPath != null && File.Exists(_diagnostic.OutputWavPath))
        {
            // Open the folder containing the output file
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_diagnostic.OutputWavPath}\"");
        }
        else if (_outputFolder != null)
        {
            System.Diagnostics.Process.Start("explorer.exe", _outputFolder);
        }
    }

    private void AppendLog(string message)
    {
        _logTextBox.AppendText(message + Environment.NewLine);
        _logTextBox.SelectionStart = _logTextBox.Text.Length;
        _logTextBox.ScrollToCaret();
    }

    private void UpdateStats(DiagnosticStats stats)
    {
        _statsLabel.Text = $"Sent: {stats.ChunksSent} chunks ({stats.BytesSent:N0} bytes) | " +
                           $"Received: {stats.ChunksReceived} chunks ({stats.BytesReceived:N0} bytes) | " +
                           $"Time: {stats.ElapsedTime.TotalSeconds:F1}s";
    }

    private void OnTestComplete()
    {
        _runButton.Enabled = true;
        _stopButton.Enabled = false;
        _progressBar.Visible = false;
        _openOutputButton.Enabled = _diagnostic?.OutputWavPath != null && File.Exists(_diagnostic.OutputWavPath);

        if (_openOutputButton.Enabled)
        {
            AppendLog("");
            AppendLog($"=== Output WAV salvato: {_diagnostic!.OutputWavPath} ===");
            AppendLog("Premi 'Apri Output' per vedere il file");
        }
    }
}
