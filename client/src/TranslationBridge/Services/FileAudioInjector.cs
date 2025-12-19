using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace TranslationBridge.Services;

/// <summary>
/// Injects audio from a WAV file into the translation system,
/// simulating incoming audio from a caller.
///
/// The audio is streamed in real-time chunks to simulate live speech.
/// </summary>
public class FileAudioInjector : IDisposable
{
    private readonly ILogger<FileAudioInjector> _logger;
    private readonly AudioBridge _audioBridge;

    private AudioFileReader? _audioReader;
    private byte[]? _audioData;
    private int _currentPosition;
    private System.Threading.Timer? _playbackTimer;
    private bool _isPlaying;
    private bool _isPaused;
    private readonly object _lock = new();

    // Target format: 16kHz, 16-bit, mono (same as AudioBridge)
    private readonly WaveFormat _targetFormat = new(16000, 16, 1);

    // Chunk size: ~100ms of audio at 16kHz/16-bit/mono = 3200 bytes
    private const int ChunkSizeBytes = 3200;
    private const int ChunkIntervalMs = 100;

    /// <summary>
    /// Fired when audio chunk is ready to be injected
    /// </summary>
    public event Action<byte[]>? OnAudioChunk;

    /// <summary>
    /// Fired when playback completes
    /// </summary>
    public event Action? OnPlaybackComplete;

    /// <summary>
    /// Fired when playback progress changes
    /// </summary>
    public event Action<TimeSpan, TimeSpan>? OnProgressChanged;

    public bool IsPlaying => _isPlaying && !_isPaused;
    public bool IsPaused => _isPaused;
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position => TimeSpan.FromSeconds((double)_currentPosition / _targetFormat.AverageBytesPerSecond);

    public FileAudioInjector(
        ILogger<FileAudioInjector> logger,
        AudioBridge audioBridge)
    {
        _logger = logger;
        _audioBridge = audioBridge;
    }

    /// <summary>
    /// Load a WAV file for playback
    /// </summary>
    public bool LoadFile(string filePath)
    {
        try
        {
            Stop();

            if (!File.Exists(filePath))
            {
                _logger.LogError("File not found: {Path}", filePath);
                return false;
            }

            _audioReader = new AudioFileReader(filePath);
            _logger.LogInformation("Loaded audio file: {Path}", filePath);
            _logger.LogInformation("  Format: {Format}", _audioReader.WaveFormat);

            // Convert to target format
            _audioData = ConvertToTargetFormat(_audioReader);
            if (_audioData == null || _audioData.Length == 0)
            {
                _logger.LogError("Failed to convert audio to target format");
                return false;
            }

            Duration = TimeSpan.FromSeconds((double)_audioData.Length / _targetFormat.AverageBytesPerSecond);
            _currentPosition = 0;

            _logger.LogInformation("  Converted: {Bytes} bytes, {Duration:mm\\:ss}",
                _audioData.Length, Duration);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading audio file: {Path}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Start streaming audio chunks
    /// </summary>
    public void Start()
    {
        if (_audioData == null)
        {
            _logger.LogWarning("No audio loaded. Call LoadFile first.");
            return;
        }

        lock (_lock)
        {
            if (_isPlaying && !_isPaused) return;

            _isPlaying = true;
            _isPaused = false;

            if (_currentPosition >= _audioData.Length)
            {
                _currentPosition = 0;
            }

            _playbackTimer = new System.Threading.Timer(
                PlaybackCallback,
                null,
                0,
                ChunkIntervalMs);

            _logger.LogInformation("Started audio injection at position {Position:mm\\:ss}", Position);
        }
    }

    /// <summary>
    /// Stop streaming
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _playbackTimer?.Dispose();
            _playbackTimer = null;
            _isPlaying = false;
            _isPaused = false;
            _currentPosition = 0;

            _logger.LogInformation("Stopped audio injection");
        }
    }

    /// <summary>
    /// Pause streaming
    /// </summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (!_isPlaying || _isPaused) return;

            _playbackTimer?.Dispose();
            _playbackTimer = null;
            _isPaused = true;

            _logger.LogInformation("Paused audio injection at {Position:mm\\:ss}", Position);
        }
    }

    /// <summary>
    /// Resume streaming
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (!_isPlaying || !_isPaused) return;

            _isPaused = false;
            _playbackTimer = new System.Threading.Timer(
                PlaybackCallback,
                null,
                0,
                ChunkIntervalMs);

            _logger.LogInformation("Resumed audio injection at {Position:mm\\:ss}", Position);
        }
    }

    /// <summary>
    /// Seek to position
    /// </summary>
    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_audioData == null) return;

            var bytes = (int)(position.TotalSeconds * _targetFormat.AverageBytesPerSecond);
            _currentPosition = Math.Clamp(bytes, 0, _audioData.Length);

            _logger.LogInformation("Seeked to {Position:mm\\:ss}", Position);
            OnProgressChanged?.Invoke(Position, Duration);
        }
    }

    private void PlaybackCallback(object? state)
    {
        lock (_lock)
        {
            if (!_isPlaying || _isPaused || _audioData == null) return;

            if (_currentPosition >= _audioData.Length)
            {
                // Playback complete
                _playbackTimer?.Dispose();
                _playbackTimer = null;
                _isPlaying = false;

                _logger.LogInformation("Audio injection complete");
                OnPlaybackComplete?.Invoke();
                return;
            }

            // Get next chunk
            var bytesToRead = Math.Min(ChunkSizeBytes, _audioData.Length - _currentPosition);
            var chunk = new byte[bytesToRead];
            Array.Copy(_audioData, _currentPosition, chunk, 0, bytesToRead);
            _currentPosition += bytesToRead;

            // Inject into audio bridge
            try
            {
                _audioBridge.InjectInboundAudio(chunk);
                OnAudioChunk?.Invoke(chunk);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error injecting audio chunk");
            }

            // Report progress
            OnProgressChanged?.Invoke(Position, Duration);
        }
    }

    private byte[]? ConvertToTargetFormat(AudioFileReader reader)
    {
        try
        {
            using var memoryStream = new MemoryStream();

            // Convert to sample provider for processing
            var sampleProvider = reader.ToSampleProvider();

            // Resample if needed
            ISampleProvider resampledProvider;
            if (reader.WaveFormat.SampleRate != _targetFormat.SampleRate)
            {
                var resampler = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(
                    sampleProvider, _targetFormat.SampleRate);
                resampledProvider = resampler;
            }
            else
            {
                resampledProvider = sampleProvider;
            }

            // Convert to mono if stereo
            ISampleProvider monoProvider;
            if (resampledProvider.WaveFormat.Channels > 1)
            {
                monoProvider = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(resampledProvider);
            }
            else
            {
                monoProvider = resampledProvider;
            }

            // Convert to 16-bit PCM
            var pcmProvider = monoProvider.ToWaveProvider16();

            // Read all bytes
            var buffer = new byte[4096];
            int read;
            while ((read = pcmProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                memoryStream.Write(buffer, 0, read);
            }

            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting audio format");
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _audioReader?.Dispose();
    }
}
