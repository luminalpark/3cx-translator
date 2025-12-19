namespace TranslationBridge.Configuration;

/// <summary>
/// Configuration for the Bidirectional Translation Bridge
/// </summary>
public class BridgeConfig
{
    /// <summary>
    /// Translation server URL (WebSocket)
    /// </summary>
    public string ServerUrl { get; set; } = "ws://3cxtranslate.luminalpark.com/ws/translate";

    /// <summary>
    /// Audio device settings for bidirectional translation
    /// </summary>
    public AudioDeviceConfig AudioDevices { get; set; } = new();

    /// <summary>
    /// Language settings
    /// </summary>
    public LanguageConfig Languages { get; set; } = new();

    /// <summary>
    /// Voice Activity Detection settings
    /// </summary>
    public VadConfig Vad { get; set; } = new();

    /// <summary>
    /// Voice cloning settings
    /// </summary>
    public VoiceConfig Voice { get; set; } = new();
}

/// <summary>
/// Voice cloning configuration
/// </summary>
public class VoiceConfig
{
    /// <summary>
    /// Path to the voice reference WAV file for voice cloning.
    /// The file should be 5-10 seconds of clear speech.
    /// Leave empty to use the default server voice.
    /// </summary>
    public string ReferencePath { get; set; } = "";

    /// <summary>
    /// Whether voice cloning is enabled
    /// </summary>
    public bool Enabled { get; set; } = false;
}

public class AudioDeviceConfig
{
    /// <summary>
    /// Sample rate (must be 16000)
    /// </summary>
    public int SampleRate { get; set; } = 16000;
    
    /// <summary>
    /// Buffer size in milliseconds
    /// </summary>
    public int BufferMs { get; set; } = 100;
    
    // ============================================================
    // INBOUND PATH: Remote Party → Operator
    // ============================================================
    
    /// <summary>
    /// Device to capture remote party's audio FROM 3CX
    /// This is where 3CX outputs the call audio (speaker)
    /// Example: "CABLE Output (VB-Audio Virtual Cable)"
    /// </summary>
    public string InboundCaptureDevice { get; set; } = "CABLE Output (VB-Audio Virtual Cable)";
    
    /// <summary>
    /// Device to play translated audio TO the operator
    /// This is the operator's headphones/speakers
    /// Example: "Speakers (Realtek)" or leave empty for default
    /// </summary>
    public string InboundPlaybackDevice { get; set; } = "";
    
    // ============================================================
    // OUTBOUND PATH: Operator → Remote Party
    // ============================================================
    
    /// <summary>
    /// Device to capture operator's voice
    /// This is the operator's real microphone
    /// Example: "Microphone (Realtek)" or leave empty for default
    /// </summary>
    public string OutboundCaptureDevice { get; set; } = "";
    
    /// <summary>
    /// Device to send translated audio TO 3CX as microphone
    /// This is what 3CX sees as the microphone input
    /// Example: "CABLE-A Input (VB-Audio Cable A)"
    /// </summary>
    public string OutboundPlaybackDevice { get; set; } = "CABLE-A Input (VB-Audio Cable A)";
}

public class LanguageConfig
{
    /// <summary>
    /// Operator's language (the language the operator speaks and wants to hear)
    /// </summary>
    public string LocalLanguage { get; set; } = "it";
    
    /// <summary>
    /// Remote party's language. Use "auto" for automatic detection.
    /// Supported: de, en, fr, es, it, pt, ru, zh, ja, ko, etc.
    /// </summary>
    public string RemoteLanguage { get; set; } = "auto";
    
    /// <summary>
    /// List of expected languages for auto-detection hints
    /// </summary>
    public List<string> ExpectedLanguages { get; set; } = new() { "de", "en", "fr", "es", "it" };
    
    /// <summary>
    /// If true, skip translation when detected language matches local language
    /// </summary>
    public bool SkipSameLanguage { get; set; } = true;
}

public class VadConfig
{
    /// <summary>
    /// Energy threshold for voice detection (0.0 - 1.0)
    /// </summary>
    public float Threshold { get; set; } = 0.01f;
    
    /// <summary>
    /// Minimum silence duration (ms) to trigger translation
    /// </summary>
    public int SilenceDurationMs { get; set; } = 600;
    
    /// <summary>
    /// Maximum speech duration (ms) before forced translation
    /// </summary>
    public int MaxSpeechDurationMs { get; set; } = 10000;
    
    /// <summary>
    /// Minimum speech duration (ms) to consider valid
    /// </summary>
    public int MinSpeechDurationMs { get; set; } = 300;
}
