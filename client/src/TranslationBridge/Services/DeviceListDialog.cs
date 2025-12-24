using System.Drawing;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace TranslationBridge.Services;

/// <summary>
/// Dialog to show all available audio devices for diagnostic purposes
/// </summary>
public class DeviceListDialog : Form
{
    private ListBox _captureListBox = null!;
    private ListBox _renderListBox = null!;
    private Label _selectedCaptureLabel = null!;
    private Button _copyButton = null!;
    private string _selectedDevice = "";

    public string SelectedDevice => _selectedDevice;

    public DeviceListDialog(string currentOutboundCapture)
    {
        InitializeComponents(currentOutboundCapture);
        LoadDevices(currentOutboundCapture);
    }

    private void InitializeComponents(string currentDevice)
    {
        Text = "Dispositivi Audio - Diagnostica";
        Size = new Size(600, 500);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        // Title
        var titleLabel = new Label
        {
            Text = "Dispositivi di cattura (Microfoni) disponibili:",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(15, 15),
            AutoSize = true
        };
        Controls.Add(titleLabel);

        // Current config info
        var currentLabel = new Label
        {
            Text = $"Config attuale OutboundCaptureDevice: \"{currentDevice}\"",
            ForeColor = Color.DarkBlue,
            Location = new Point(15, 40),
            AutoSize = true
        };
        Controls.Add(currentLabel);

        // Capture devices listbox
        _captureListBox = new ListBox
        {
            Location = new Point(15, 65),
            Size = new Size(550, 150),
            Font = new Font("Consolas", 9),
            SelectionMode = SelectionMode.One
        };
        _captureListBox.SelectedIndexChanged += (s, e) =>
        {
            if (_captureListBox.SelectedItem != null)
            {
                _selectedDevice = _captureListBox.SelectedItem.ToString() ?? "";
                _selectedCaptureLabel.Text = $"Selezionato: {_selectedDevice}";
            }
        };
        Controls.Add(_captureListBox);

        // Selected device label
        _selectedCaptureLabel = new Label
        {
            Text = "Selezionato: (nessuno)",
            Location = new Point(15, 220),
            Size = new Size(450, 20),
            ForeColor = Color.Green,
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };
        Controls.Add(_selectedCaptureLabel);

        // Copy button
        _copyButton = new Button
        {
            Text = "Copia Nome",
            Location = new Point(470, 218),
            Size = new Size(95, 25)
        };
        _copyButton.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(_selectedDevice))
            {
                Clipboard.SetText(_selectedDevice);
                MessageBox.Show($"Nome copiato:\n{_selectedDevice}\n\nIncollalo in appsettings.json come OutboundCaptureDevice",
                    "Copiato", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        Controls.Add(_copyButton);

        // Render devices section
        var renderLabel = new Label
        {
            Text = "Dispositivi di output (Speakers) - per riferimento:",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(15, 255),
            AutoSize = true
        };
        Controls.Add(renderLabel);

        _renderListBox = new ListBox
        {
            Location = new Point(15, 280),
            Size = new Size(550, 120),
            Font = new Font("Consolas", 9),
            ForeColor = Color.Gray
        };
        Controls.Add(_renderListBox);

        // Instructions
        var instructionsLabel = new Label
        {
            Text = "ISTRUZIONI:\n" +
                   "1. Seleziona il microfono dell'operatore dalla lista sopra\n" +
                   "2. Clicca 'Copia Nome' per copiare il nome esatto\n" +
                   "3. Modifica appsettings.json: OutboundCaptureDevice = \"<nome copiato>\"\n" +
                   "4. Riavvia l'applicazione\n\n" +
                   "OPPURE: Usa il menu Tray > Dispositivi Audio > Outbound Capture",
            Location = new Point(15, 410),
            Size = new Size(550, 80),
            ForeColor = Color.DarkRed
        };
        Controls.Add(instructionsLabel);

        // Close button
        var closeButton = new Button
        {
            Text = "Chiudi",
            DialogResult = DialogResult.OK,
            Location = new Point(490, 450),
            Size = new Size(75, 25)
        };
        Controls.Add(closeButton);
        AcceptButton = closeButton;
    }

    private void LoadDevices(string currentDevice)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();

            // Load capture devices
            var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in captureDevices)
            {
                var prefix = device.FriendlyName.Contains(currentDevice, StringComparison.OrdinalIgnoreCase)
                    ? "[MATCH] "
                    : "";
                _captureListBox.Items.Add($"{prefix}{device.FriendlyName}");
            }

            // Load render devices
            var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in renderDevices)
            {
                _renderListBox.Items.Add(device.FriendlyName);
            }

            // Auto-select matching device
            for (int i = 0; i < _captureListBox.Items.Count; i++)
            {
                var item = _captureListBox.Items[i]?.ToString() ?? "";
                if (item.StartsWith("[MATCH]"))
                {
                    _captureListBox.SelectedIndex = i;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _captureListBox.Items.Add($"Errore: {ex.Message}");
        }
    }
}
