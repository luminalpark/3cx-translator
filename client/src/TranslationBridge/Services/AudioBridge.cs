using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TranslationBridge.Configuration;

namespace TranslationBridge.Services;

/// <summary>
/// Bidirectional audio bridge for 3CX translation
/// 
/// Audio Flow:
/// 
/// INBOUND (Remote → Operator):
///   3CX Speaker Out → VB-Cable A → [Translate] → Operator Headphones
///   
/// OUTBOUND (Operator → Remote):  
///   Operator Microphone → [Translate] → VB-Cable B → 3CX Mic In
/// </summary>
public class AudioBridge : IDisposable
{
    private readonly ILogger<AudioBridge> _logger;
    private readonly BridgeConfig _config;
    
    // Inbound: Remote party audio (from 3CX)
    private WasapiCapture? _inboundCapture;
    private WasapiOut? _inboundPlayback;
    private BufferedWaveProvider? _inboundBuffer;
    
    // Outbound: Operator audio (to 3CX)
    private WasapiCapture? _outboundCapture;
    private WasapiOut? _outboundPlayback;
    private BufferedWaveProvider? _outboundBuffer;
    
    private readonly WaveFormat _waveFormat;
    private bool _isRunning;

    /// <summary>
    /// Fired when INBOUND audio is captured (remote party speaking)
    /// This audio needs to be translated FROM remote language TO local language
    /// </summary>
    public event Action<byte[]>? OnInboundAudioCaptured;
    
    /// <summary>
    /// Fired when OUTBOUND audio is captured (operator speaking)
    /// This audio needs to be translated FROM local language TO remote language
    /// </summary>
    public event Action<byte[]>? OnOutboundAudioCaptured;

    public bool IsRunning => _isRunning;

    /// <summary>
    /// Get all available audio devices for UI selection
    /// </summary>
    public static (List<AudioDeviceInfo> CaptureDevices, List<AudioDeviceInfo> RenderDevices) GetAvailableDevices()
    {
        var enumerator = new MMDeviceEnumerator();

        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, DeviceType.Capture))
            .ToList();

        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, DeviceType.Render))
            .ToList();

        return (captureDevices, renderDevices);
    }

    public AudioBridge(
        ILogger<AudioBridge> logger,
        IOptions<BridgeConfig> config)
    {
        _logger = logger;
        _config = config.Value;
        
        // SeamlessM4T requires 16kHz mono
        _waveFormat = new WaveFormat(
            _config.AudioDevices.SampleRate, 
            16,  // bits per sample
            1);  // mono
    }

    public void Initialize()
    {
        var enumerator = new MMDeviceEnumerator();
        
        _logger.LogInformation("=== Initializing Bidirectional Audio Bridge ===");
        
        // ============================================================
        // INBOUND PATH: Remote Party → Operator
        // ============================================================
        
        // 1. Capture from VB-Cable A (where 3CX outputs remote party audio)
        var inboundCaptureDevice = FindDevice(
            enumerator, 
            _config.AudioDevices.InboundCaptureDevice, 
            DataFlow.Render);
        
        if (inboundCaptureDevice == null)
        {
            throw new InvalidOperationException(
                $"Inbound capture device not found: {_config.AudioDevices.InboundCaptureDevice}\n" +
                $"Available render devices:\n{GetAvailableDevices(enumerator, DataFlow.Render)}");
        }
        _logger.LogInformation("Inbound Capture (3CX Speaker): {Device}", inboundCaptureDevice.FriendlyName);
        
        // 2. Playback to operator's headphones
        var inboundPlaybackDevice = FindDevice(
            enumerator,
            _config.AudioDevices.InboundPlaybackDevice,
            DataFlow.Render);
        
        if (inboundPlaybackDevice == null)
        {
            // Fall back to default output device
            inboundPlaybackDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _logger.LogWarning("Using default audio output for operator playback");
        }
        _logger.LogInformation("Inbound Playback (Operator Headphones): {Device}", inboundPlaybackDevice.FriendlyName);
        
        // Setup inbound capture (loopback from 3CX speaker)
        _inboundCapture = new WasapiLoopbackCapture(inboundCaptureDevice);
        _inboundCapture.DataAvailable += OnInboundDataAvailable;
        
        // Setup inbound playback buffer (translated audio to operator)
        _inboundBuffer = new BufferedWaveProvider(_waveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true
        };
        _inboundPlayback = new WasapiOut(inboundPlaybackDevice, AudioClientShareMode.Shared, true, 100);
        _inboundPlayback.Init(_inboundBuffer);
        
        // ============================================================
        // OUTBOUND PATH: Operator → Remote Party
        // ============================================================
        
        // 3. Capture from operator's microphone
        var outboundCaptureDevice = FindDevice(
            enumerator,
            _config.AudioDevices.OutboundCaptureDevice,
            DataFlow.Capture);
        
        if (outboundCaptureDevice == null)
        {
            // Fall back to default microphone
            outboundCaptureDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _logger.LogWarning("Using default microphone for operator capture");
        }
        _logger.LogInformation("Outbound Capture (Operator Mic): {Device}", outboundCaptureDevice.FriendlyName);
        
        // 4. Playback to VB-Cable B (which 3CX sees as microphone)
        var outboundPlaybackDevice = FindDevice(
            enumerator,
            _config.AudioDevices.OutboundPlaybackDevice,
            DataFlow.Render);
        
        if (outboundPlaybackDevice == null)
        {
            throw new InvalidOperationException(
                $"Outbound playback device not found: {_config.AudioDevices.OutboundPlaybackDevice}\n" +
                $"Available render devices:\n{GetAvailableDevices(enumerator, DataFlow.Render)}");
        }
        _logger.LogInformation("Outbound Playback (3CX Mic): {Device}", outboundPlaybackDevice.FriendlyName);
        
        // Setup outbound capture (operator's real microphone)
        _outboundCapture = new WasapiCapture(outboundCaptureDevice);
        _outboundCapture.DataAvailable += OnOutboundDataAvailable;
        
        // Setup outbound playback buffer (translated audio to remote party)
        _outboundBuffer = new BufferedWaveProvider(_waveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true
        };
        _outboundPlayback = new WasapiOut(outboundPlaybackDevice, AudioClientShareMode.Shared, true, 100);
        _outboundPlayback.Init(_outboundBuffer);
        
        _logger.LogInformation("=== Audio Bridge Initialized (Bidirectional) ===");
    }

    public void Start()
    {
        if (_isRunning) return;
        
        _inboundCapture?.StartRecording();
        _inboundPlayback?.Play();
        _outboundCapture?.StartRecording();
        _outboundPlayback?.Play();
        
        _isRunning = true;
        _logger.LogInformation("Audio bridge started (bidirectional)");
    }

    public void Stop()
    {
        if (!_isRunning) return;
        
        _inboundCapture?.StopRecording();
        _inboundPlayback?.Stop();
        _outboundCapture?.StopRecording();
        _outboundPlayback?.Stop();
        
        _isRunning = false;
        _logger.LogInformation("Audio bridge stopped");
    }

    /// <summary>
    /// Play translated INBOUND audio (remote→local translation) to operator's headphones
    /// </summary>
    public void PlayInboundAudio(byte[] audioData)
    {
        _inboundBuffer?.AddSamples(audioData, 0, audioData.Length);
        _logger.LogDebug("Queued {Bytes} bytes for operator playback", audioData.Length);
    }

    /// <summary>
    /// Play translated OUTBOUND audio (local→remote translation) to 3CX microphone
    /// </summary>
    public void PlayOutboundAudio(byte[] audioData)
    {
        _outboundBuffer?.AddSamples(audioData, 0, audioData.Length);
        _logger.LogDebug("Queued {Bytes} bytes for 3CX microphone", audioData.Length);
    }

    private void OnInboundDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        
        var convertedData = ConvertAudio(e.Buffer, e.BytesRecorded, _inboundCapture!.WaveFormat);
        if (convertedData.Length > 0)
        {
            OnInboundAudioCaptured?.Invoke(convertedData);
        }
    }

    private void OnOutboundDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        
        var convertedData = ConvertAudio(e.Buffer, e.BytesRecorded, _outboundCapture!.WaveFormat);
        if (convertedData.Length > 0)
        {
            OnOutboundAudioCaptured?.Invoke(convertedData);
        }
    }

    private byte[] ConvertAudio(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        // If already in correct format, return as-is
        if (sourceFormat.SampleRate == _waveFormat.SampleRate &&
            sourceFormat.Channels == _waveFormat.Channels &&
            sourceFormat.BitsPerSample == _waveFormat.BitsPerSample)
        {
            var result = new byte[bytesRecorded];
            Array.Copy(buffer, result, bytesRecorded);
            return result;
        }

        try
        {
            using var sourceStream = new RawSourceWaveStream(
                new MemoryStream(buffer, 0, bytesRecorded),
                sourceFormat);

            // Convert IEEE Float to PCM16 if needed
            IWaveProvider pcmStream;
            if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var sampleProvider = sourceStream.ToSampleProvider();
                pcmStream = sampleProvider.ToWaveProvider16();
            }
            else
            {
                pcmStream = sourceStream;
            }

            // Convert to mono if stereo
            var currentFormat = pcmStream.WaveFormat;
            IWaveProvider monoStream = currentFormat.Channels > 1
                ? new StereoToMonoProvider16(pcmStream)
                : pcmStream;

            // Resample if needed
            if (monoStream.WaveFormat.SampleRate != _waveFormat.SampleRate)
            {
                using var resampler = new MediaFoundationResampler(monoStream, _waveFormat);
                resampler.ResamplerQuality = 60;
                
                using var ms = new MemoryStream();
                var readBuffer = new byte[4096];
                int read;
                while ((read = resampler.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    ms.Write(readBuffer, 0, read);
                }
                return ms.ToArray();
            }

            // Just copy mono data
            using var outputMs = new MemoryStream();
            var copyBuffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = monoStream.Read(copyBuffer, 0, copyBuffer.Length)) > 0)
            {
                outputMs.Write(copyBuffer, 0, bytesRead);
            }
            return outputMs.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio conversion error");
            return Array.Empty<byte>();
        }
    }

    private MMDevice? FindDevice(MMDeviceEnumerator enumerator, string name, DataFlow flow)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        
        // Try exact match first
        var device = devices.FirstOrDefault(d => 
            d.FriendlyName.Equals(name, StringComparison.OrdinalIgnoreCase));
        
        // Try partial match
        device ??= devices.FirstOrDefault(d => 
            d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));

        return device;
    }

    private string GetAvailableDevices(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        return string.Join("\n", devices.Select(d => $"  - {d.FriendlyName}"));
    }

    public void Dispose()
    {
        Stop();

        _inboundCapture?.Dispose();
        _inboundPlayback?.Dispose();
        _outboundCapture?.Dispose();
        _outboundPlayback?.Dispose();
    }
}

/// <summary>
/// Audio device type
/// </summary>
public enum DeviceType
{
    Capture,
    Render
}

/// <summary>
/// Audio device information for UI display
/// </summary>
public record AudioDeviceInfo(string Id, string Name, DeviceType Type);
