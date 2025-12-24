/**
 * Gemini WebSocket Client
 * Handles communication with the Gemini translation server
 */

class GeminiClient {
    constructor(options = {}) {
        this.url = options.url || 'ws://localhost:8001/ws/translate';
        this.apiKey = options.apiKey || '';
        this.targetLang = options.targetLang || 'it';
        this.sourceLang = options.sourceLang || 'auto';

        this.ws = null;
        this.connected = false;
        this.streamingEnabled = false;

        // Audio playback
        this.audioContext = null;
        this.audioQueue = [];
        this.isPlaying = false;
        this.audioOutputDeviceId = options.audioOutputDeviceId || null;
        this.audioElement = null;  // For setSinkId support

        // Callbacks
        this.onConnected = options.onConnected || (() => {});
        this.onDisconnected = options.onDisconnected || (() => {});
        this.onConfigured = options.onConfigured || (() => {});
        this.onStreamingReady = options.onStreamingReady || (() => {});
        this.onTurnComplete = options.onTurnComplete || (() => {});
        this.onSourceText = options.onSourceText || (() => {});
        this.onTranslatedText = options.onTranslatedText || (() => {});
        this.onTranslatedAudio = options.onTranslatedAudio || (() => {});
        this.onError = options.onError || (() => {});
        this.onLog = options.onLog || (() => {});
    }

    /**
     * Connect to Gemini server
     */
    async connect() {
        return new Promise((resolve, reject) => {
            let url = this.url;
            if (this.apiKey) {
                const separator = url.includes('?') ? '&' : '?';
                url = `${url}${separator}key=${encodeURIComponent(this.apiKey)}`;
            }

            this.onLog(`Connecting to ${this.url}...`, 'event');

            this.ws = new WebSocket(url);
            this.ws.binaryType = 'arraybuffer';

            this.ws.onopen = () => {
                this.connected = true;
                this.onLog('WebSocket connected', 'event');
                this.onConnected();
                resolve();
            };

            this.ws.onmessage = (event) => this._handleMessage(event);

            this.ws.onclose = () => {
                this.connected = false;
                this.streamingEnabled = false;
                this.onLog('WebSocket disconnected', 'error');
                this.onDisconnected();
            };

            this.ws.onerror = (err) => {
                this.onLog(`WebSocket error: ${err.message || 'Unknown error'}`, 'error');
                this.onError(err);
                reject(err);
            };
        });
    }

    /**
     * Disconnect from server
     */
    disconnect() {
        if (this.ws) {
            if (this.ws.readyState === WebSocket.OPEN) {
                this.ws.send(JSON.stringify({ type: 'set_streaming', enabled: false }));
            }
            this.ws.close();
            this.ws = null;
        }
        this.connected = false;
        this.streamingEnabled = false;
    }

    /**
     * Configure language settings
     */
    configure(sourceLang, targetLang) {
        if (!this.connected) return;

        this.sourceLang = sourceLang || 'auto';
        this.targetLang = targetLang;

        this.ws.send(JSON.stringify({
            type: 'configure',
            source_lang: this.sourceLang,
            target_lang: this.targetLang
        }));
    }

    /**
     * Enable streaming mode
     */
    enableStreaming() {
        if (!this.connected) return;
        this.ws.send(JSON.stringify({ type: 'set_streaming', enabled: true }));
    }

    /**
     * Send activity start signal (beginning of speech)
     */
    sendActivityStart() {
        if (!this.connected || !this.streamingEnabled) return;
        this.ws.send(JSON.stringify({ type: 'activity_start' }));
    }

    /**
     * Send activity end signal (end of speech, trigger translation)
     */
    sendActivityEnd() {
        if (!this.connected || !this.streamingEnabled) return;
        this.ws.send(JSON.stringify({ type: 'activity_end' }));
    }

    /**
     * Send audio chunk
     * @param {Int16Array|ArrayBuffer} audioData - PCM16 audio data
     */
    sendAudio(audioData) {
        if (!this.connected || !this.streamingEnabled) return;

        if (audioData instanceof Int16Array) {
            this.ws.send(audioData.buffer);
        } else {
            this.ws.send(audioData);
        }
    }

    /**
     * Handle incoming WebSocket messages
     */
    async _handleMessage(event) {
        if (event.data instanceof ArrayBuffer) {
            // Audio data
            const audioData = new Int16Array(event.data);
            this.onLog(`Received ${audioData.length} audio samples`, 'audio');
            this.onTranslatedAudio(audioData);
            this._queueAudioPlayback(audioData);
        } else if (typeof event.data === 'string') {
            // JSON message
            const data = JSON.parse(event.data);
            this._handleJsonMessage(data);
        }
    }

    _handleJsonMessage(data) {
        switch (data.type) {
            case 'connected':
                this.onLog(`Server: ${data.server}, Model: ${data.model}`, 'event');
                // Auto-configure
                this.configure(this.sourceLang, this.targetLang);
                break;

            case 'configured':
                this.onLog(`Language: ${data.source_lang} -> ${data.target_lang}`, 'event');
                this.onConfigured(data);
                // Enable streaming
                this.enableStreaming();
                break;

            case 'streaming_enabled':
                this.streamingEnabled = data.enabled;
                this.onLog(`Streaming mode: ${data.enabled ? 'enabled' : 'disabled'}`, 'event');
                if (data.enabled) {
                    this.onStreamingReady();
                }
                break;

            case 'streaming_session_ready':
                this.onLog(`Session ready: ${data.source_lang} -> ${data.target_lang}`, 'event');
                break;

            case 'source_text':
                if (data.text) {
                    this.onSourceText(data.text);
                }
                break;

            case 'translated_text':
                if (data.text) {
                    this.onTranslatedText(data.text);
                }
                break;

            case 'model_text':
                if (data.text) {
                    this.onLog(`Model text: "${data.text}"`, 'translation');
                }
                break;

            case 'model_turn_started':
                this.onLog('Gemini speaking...', 'event');
                break;

            case 'turn_complete':
                this.onLog('Turn complete', 'event');
                this.onTurnComplete();
                break;

            case 'activity_start_sent':
                this.onLog('Activity start acknowledged', 'event');
                break;

            case 'activity_end_sent':
                this.onLog('Activity end acknowledged - translating...', 'event');
                break;

            case 'error':
                this.onLog(`Error: ${data.message}`, 'error');
                this.onError(new Error(data.message));
                break;

            default:
                this.onLog(`Event: ${data.type}`, 'event');
        }
    }

    /**
     * Initialize audio context for playback
     */
    async initAudioContext() {
        if (!this.audioContext) {
            this.audioContext = new AudioContext({ sampleRate: 16000 });

            // Create audio element for output device selection
            if (!this.audioElement) {
                this.audioElement = document.createElement('audio');
                this.audioElement.id = 'geminiAudioOutput';
                document.body.appendChild(this.audioElement);

                // Create MediaStreamDestination for routing
                this.mediaStreamDest = this.audioContext.createMediaStreamDestination();
                this.audioElement.srcObject = this.mediaStreamDest.stream;

                // Set output device if specified
                if (this.audioOutputDeviceId && typeof this.audioElement.setSinkId === 'function') {
                    try {
                        await this.audioElement.setSinkId(this.audioOutputDeviceId);
                        console.log(`[GeminiClient] Audio output set to device: ${this.audioOutputDeviceId}`);
                    } catch (err) {
                        console.warn(`[GeminiClient] Failed to set audio output device: ${err.message}`);
                    }
                }

                this.audioElement.play().catch(err => {
                    console.warn(`[GeminiClient] Audio element play error: ${err.message}`);
                });
            }
        }
        if (this.audioContext.state === 'suspended') {
            await this.audioContext.resume();
        }
    }

    /**
     * Set audio output device
     */
    async setAudioOutputDevice(deviceId) {
        this.audioOutputDeviceId = deviceId;
        if (this.audioElement && typeof this.audioElement.setSinkId === 'function') {
            try {
                await this.audioElement.setSinkId(deviceId);
                console.log(`[GeminiClient] Audio output changed to: ${deviceId}`);
            } catch (err) {
                console.warn(`[GeminiClient] Failed to set audio output: ${err.message}`);
            }
        }
    }

    /**
     * Queue audio for playback
     */
    _queueAudioPlayback(int16Data) {
        this.audioQueue.push(int16Data);
        if (!this.isPlaying) {
            this._playNextChunk();
        }
    }

    /**
     * Play next audio chunk from queue
     */
    async _playNextChunk() {
        if (this.audioQueue.length === 0) {
            this.isPlaying = false;
            return;
        }

        await this.initAudioContext();
        this.isPlaying = true;

        const int16Data = this.audioQueue.shift();

        // Convert Int16 to Float32
        const float32Data = new Float32Array(int16Data.length);
        for (let i = 0; i < int16Data.length; i++) {
            float32Data[i] = int16Data[i] / 32768;
        }

        // Create audio buffer at 16kHz (Gemini outputs 16kHz audio)
        const buffer = this.audioContext.createBuffer(1, float32Data.length, 16000);
        buffer.getChannelData(0).set(float32Data);

        // Play through mediaStreamDest to use selected output device
        const source = this.audioContext.createBufferSource();
        source.buffer = buffer;
        // Connect to mediaStreamDest if available (for device selection), otherwise direct
        const destination = this.mediaStreamDest || this.audioContext.destination;
        source.connect(destination);
        source.onended = () => this._playNextChunk();
        source.start();
    }

    /**
     * Clear audio queue
     */
    clearAudioQueue() {
        this.audioQueue = [];
        this.isPlaying = false;
    }
}

// Export for use in other modules
if (typeof module !== 'undefined' && module.exports) {
    module.exports = { GeminiClient };
}
