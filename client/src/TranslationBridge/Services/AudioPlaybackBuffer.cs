using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace TranslationBridge.Services;

/// <summary>
/// Audio playback buffer with pre-roll buffering.
/// Accumulates audio until minimum threshold is reached before starting playback.
/// This prevents choppy audio when receiving small incremental chunks from streaming translation.
/// </summary>
public class AudioPlaybackBuffer : IDisposable
{
    private readonly ILogger _logger;
    private readonly WaveFormat _waveFormat;
    private readonly int _prerollBytes;
    private readonly int _minBufferBytes;

    private readonly object _lock = new();
    private readonly List<byte> _pendingBuffer = new();
    private BufferedWaveProvider? _playbackBuffer;
    private WasapiOut? _player;
    private bool _isPlaying;
    private bool _prerollComplete;
    private DateTime _lastAudioReceived;
    private readonly System.Timers.Timer _silenceTimer;

    /// <summary>
    /// Fired when playback starts after preroll is complete
    /// </summary>
    public event Action? OnPlaybackStarted;

    /// <summary>
    /// Fired when playback stops due to buffer underrun or silence
    /// </summary>
    public event Action? OnPlaybackStopped;

    /// <summary>
    /// Creates a new audio playback buffer
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="waveFormat">Audio format (default: 16kHz, 16-bit, mono)</param>
    /// <param name="prerollMs">Milliseconds of audio to buffer before starting playback (default: 1000ms)</param>
    /// <param name="silenceTimeoutMs">Milliseconds of silence before resetting buffer (default: 3000ms)</param>
    public AudioPlaybackBuffer(
        ILogger logger,
        WaveFormat? waveFormat = null,
        int prerollMs = 1000,
        int silenceTimeoutMs = 3000)
    {
        _logger = logger;
        _waveFormat = waveFormat ?? new WaveFormat(16000, 16, 1);

        // Calculate bytes for preroll (16kHz, 16-bit, mono = 32 bytes/ms)
        var bytesPerMs = _waveFormat.SampleRate * _waveFormat.BitsPerSample / 8 * _waveFormat.Channels / 1000;
        _prerollBytes = prerollMs * bytesPerMs;
        _minBufferBytes = 100 * bytesPerMs; // Minimum 100ms before considering underrun

        _logger.LogDebug("AudioPlaybackBuffer: preroll={PrerollMs}ms ({PrerollBytes} bytes), format={Format}",
            prerollMs, _prerollBytes, _waveFormat);

        // Timer to detect silence and reset buffer
        _silenceTimer = new System.Timers.Timer(silenceTimeoutMs);
        _silenceTimer.Elapsed += (s, e) => CheckSilenceTimeout();
        _silenceTimer.AutoReset = true;
    }

    /// <summary>
    /// Initialize the buffer with a specific output device
    /// </summary>
    public void Initialize(NAudio.CoreAudioApi.MMDevice outputDevice)
    {
        lock (_lock)
        {
            _playbackBuffer?.ClearBuffer();
            _player?.Stop();
            _player?.Dispose();

            _playbackBuffer = new BufferedWaveProvider(_waveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true
            };

            _player = new WasapiOut(outputDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100);
            _player.Init(_playbackBuffer);

            _isPlaying = false;
            _prerollComplete = false;
            _pendingBuffer.Clear();

            _logger.LogInformation("AudioPlaybackBuffer initialized with device: {Device}", outputDevice.FriendlyName);
        }
    }

    /// <summary>
    /// Add audio data to the buffer
    /// </summary>
    public void AddAudio(byte[] audioData)
    {
        if (audioData == null || audioData.Length == 0)
            return;

        lock (_lock)
        {
            _lastAudioReceived = DateTime.UtcNow;

            if (!_prerollComplete)
            {
                // Still in preroll phase - accumulate audio
                _pendingBuffer.AddRange(audioData);

                if (_pendingBuffer.Count >= _prerollBytes)
                {
                    // Preroll complete - start playback
                    _prerollComplete = true;

                    // Flush pending buffer to playback
                    var pendingData = _pendingBuffer.ToArray();
                    _pendingBuffer.Clear();
                    _playbackBuffer?.AddSamples(pendingData, 0, pendingData.Length);

                    StartPlayback();

                    _logger.LogInformation("Preroll complete ({Bytes} bytes buffered), starting playback",
                        pendingData.Length);
                }
                else
                {
                    _logger.LogDebug("Buffering: {Current}/{Target} bytes",
                        _pendingBuffer.Count, _prerollBytes);
                }
            }
            else
            {
                // Preroll complete - add directly to playback buffer
                _playbackBuffer?.AddSamples(audioData, 0, audioData.Length);

                // Ensure playback is running
                if (!_isPlaying)
                {
                    StartPlayback();
                }
            }

            // Start/reset silence timer
            if (!_silenceTimer.Enabled)
            {
                _silenceTimer.Start();
            }
        }
    }

    private void StartPlayback()
    {
        if (_player == null || _isPlaying)
            return;

        try
        {
            _player.Play();
            _isPlaying = true;
            OnPlaybackStarted?.Invoke();
            _logger.LogDebug("Playback started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting playback");
        }
    }

    private void CheckSilenceTimeout()
    {
        lock (_lock)
        {
            var silenceDuration = DateTime.UtcNow - _lastAudioReceived;

            if (silenceDuration.TotalMilliseconds > 2000)
            {
                // No audio received for a while - reset for next phrase
                Reset();
                _silenceTimer.Stop();
            }
        }
    }

    /// <summary>
    /// Reset the buffer state (call between phrases/turns)
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _pendingBuffer.Clear();
            _prerollComplete = false;

            // Don't stop playback - let it drain naturally
            // The BufferedWaveProvider will handle the end gracefully

            _logger.LogDebug("Buffer reset, ready for next phrase");
        }
    }

    /// <summary>
    /// Stop playback immediately
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _silenceTimer.Stop();
            _player?.Stop();
            _isPlaying = false;
            _prerollComplete = false;
            _pendingBuffer.Clear();
            _playbackBuffer?.ClearBuffer();

            OnPlaybackStopped?.Invoke();
            _logger.LogDebug("Playback stopped");
        }
    }

    /// <summary>
    /// Start the player (call after Initialize)
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            // Don't start playing yet - wait for preroll
            _isPlaying = false;
            _prerollComplete = false;
            _pendingBuffer.Clear();
            _player?.Play(); // Start the player but buffer is empty
            _logger.LogDebug("Player started, waiting for audio preroll");
        }
    }

    public void Dispose()
    {
        _silenceTimer.Stop();
        _silenceTimer.Dispose();
        _player?.Stop();
        _player?.Dispose();
    }
}
