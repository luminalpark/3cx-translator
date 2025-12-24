/**
 * Voice Activity Detection (VAD) Module
 * Manages turn-based speech detection for real-time translation
 *
 * State Machine:
 *   IDLE -> ACTIVE (speech detected) -> WAIT_COMPLETE (silence detected) -> IDLE (turn complete)
 */

class VADProcessor {
    constructor(options = {}) {
        // VAD thresholds (optimized for VoIP)
        this.speechThreshold = options.speechThreshold || 0.012;
        this.silenceThreshold = options.silenceThreshold || 0.006;
        this.autoRestartThreshold = options.autoRestartThreshold || 0.009;

        // Timing parameters (ms)
        this.silenceDurationMs = options.silenceDurationMs || 350;
        this.minTurnDurationMs = options.minTurnDurationMs || 900;
        this.maxTurnMs = options.maxTurnMs || 5000;

        // Buffer settings
        this.overlapMs = options.overlapMs || 250;
        this.pendingMaxBytes = options.pendingMaxBytes || 64 * 1024;

        // State
        this.state = 'IDLE';
        this.silenceStartTime = null;
        this.speechStartTime = null;
        this.lastRms = 0;

        // Buffers
        this.overlapTail = null;
        this.pendingAudioChunks = [];
        this.pendingBytes = 0;

        // Callbacks
        this.onStateChange = options.onStateChange || (() => {});
        this.onActivityStart = options.onActivityStart || (() => {});
        this.onActivityEnd = options.onActivityEnd || (() => {});
        this.onSendAudio = options.onSendAudio || (() => {});
        this.onRmsUpdate = options.onRmsUpdate || (() => {});
    }

    /**
     * Calculate overlap bytes based on sample rate
     */
    get overlapBytes() {
        return Math.floor(16000 * (this.overlapMs / 1000)) * 2;
    }

    /**
     * Set state and notify
     */
    setState(newState) {
        const oldState = this.state;
        this.state = newState;
        this.onStateChange(newState, oldState);
    }

    /**
     * Calculate RMS (Root Mean Square) energy from audio samples
     */
    calculateRms(samples) {
        let sum = 0;
        for (let i = 0; i < samples.length; i++) {
            const normalized = samples[i] / 32768; // Normalize PCM16
            sum += normalized * normalized;
        }
        return Math.sqrt(sum / samples.length);
    }

    /**
     * Process audio chunk through VAD state machine
     * @param {Int16Array} pcm16Data - PCM16 audio samples
     */
    processAudio(pcm16Data) {
        const rms = this.calculateRms(pcm16Data);
        this.lastRms = rms;
        this.onRmsUpdate(rms);

        const now = Date.now();

        switch (this.state) {
            case 'IDLE':
                this._processIdle(pcm16Data, rms, now);
                break;

            case 'ACTIVE':
                this._processActive(pcm16Data, rms, now);
                break;

            case 'WAIT_COMPLETE':
                this._processWaitComplete(pcm16Data, rms, now);
                break;
        }
    }

    _processIdle(pcm16Data, rms, now) {
        if (rms > this.speechThreshold) {
            // Speech detected - start new turn
            this.onActivityStart();

            // Send overlap from previous turn for lexical continuity
            if (this.overlapTail) {
                this.onSendAudio(this.overlapTail);
                this.overlapTail = null;
            }

            this.setState('ACTIVE');
            this.speechStartTime = now;
            this.silenceStartTime = null;

            // Send this first chunk
            this._sendAndSaveOverlap(pcm16Data);
        }
        // In IDLE with no speech, don't send audio
    }

    _processActive(pcm16Data, rms, now) {
        const turnDuration = now - this.speechStartTime;

        // Safety limit: close if max turn AND near-silence
        if (turnDuration >= this.maxTurnMs && rms < this.silenceThreshold) {
            this.onActivityEnd();
            this.setState('WAIT_COMPLETE');
            this.silenceStartTime = null;
            return;
        }

        // Track silence duration
        if (rms > this.silenceThreshold) {
            // Still speaking - reset silence timer
            this.silenceStartTime = null;
        } else {
            // Silence detected
            if (!this.silenceStartTime) {
                this.silenceStartTime = now;
            }

            const silenceDuration = now - this.silenceStartTime;

            // Close turn on natural pause
            if (silenceDuration >= this.silenceDurationMs &&
                turnDuration >= this.minTurnDurationMs) {
                this.onActivityEnd();
                this.setState('WAIT_COMPLETE');
                this.silenceStartTime = null;
                return;
            }
        }

        // Send audio and save overlap
        this._sendAndSaveOverlap(pcm16Data);
    }

    _processWaitComplete(pcm16Data, rms, now) {
        // Buffer audio during WAIT_COMPLETE for potential auto-restart
        this.pendingAudioChunks.push(pcm16Data);
        this.pendingBytes += pcm16Data.byteLength;

        // Limit buffer size
        while (this.pendingBytes > this.pendingMaxBytes) {
            const removed = this.pendingAudioChunks.shift();
            this.pendingBytes -= removed.byteLength;
        }
    }

    _sendAndSaveOverlap(pcm16Data) {
        // Send audio
        this.onSendAudio(pcm16Data);

        // Save tail for overlap
        const bytes = new Uint8Array(pcm16Data.buffer);
        if (bytes.byteLength >= this.overlapBytes) {
            this.overlapTail = new Int16Array(
                bytes.slice(bytes.byteLength - this.overlapBytes).buffer
            );
        } else {
            this.overlapTail = pcm16Data;
        }
    }

    /**
     * Called when server sends turn_complete
     */
    onTurnComplete() {
        if (this.lastRms > this.autoRestartThreshold) {
            // Auto-restart: speech still present
            this.onActivityStart();

            // Send overlap for continuity
            if (this.overlapTail) {
                this.onSendAudio(this.overlapTail);
                this.overlapTail = null;
            }

            // Flush pending buffer
            for (const chunk of this.pendingAudioChunks) {
                this.onSendAudio(chunk);
            }
            this.pendingAudioChunks = [];
            this.pendingBytes = 0;

            this.setState('ACTIVE');
            this.speechStartTime = Date.now();
            this.silenceStartTime = null;
        } else {
            // No speech - go to IDLE
            this.setState('IDLE');
            this.silenceStartTime = null;
            this.speechStartTime = null;
            this.pendingAudioChunks = [];
            this.pendingBytes = 0;
        }
    }

    /**
     * Reset VAD state
     */
    reset() {
        this.state = 'IDLE';
        this.silenceStartTime = null;
        this.speechStartTime = null;
        this.lastRms = 0;
        this.overlapTail = null;
        this.pendingAudioChunks = [];
        this.pendingBytes = 0;
    }

    /**
     * Update VAD parameters
     */
    setParams(params) {
        if (params.speechThreshold !== undefined) {
            this.speechThreshold = params.speechThreshold;
        }
        if (params.silenceThreshold !== undefined) {
            this.silenceThreshold = params.silenceThreshold;
        }
        if (params.autoRestartThreshold !== undefined) {
            this.autoRestartThreshold = params.autoRestartThreshold;
        }
        if (params.silenceDurationMs !== undefined) {
            this.silenceDurationMs = params.silenceDurationMs;
        }
        if (params.minTurnDurationMs !== undefined) {
            this.minTurnDurationMs = params.minTurnDurationMs;
        }
        if (params.maxTurnMs !== undefined) {
            this.maxTurnMs = params.maxTurnMs;
        }
    }
}

// VAD presets
const VAD_PRESETS = {
    voip: {
        speechThreshold: 0.012,
        silenceThreshold: 0.006,
        autoRestartThreshold: 0.009,
        silenceDurationMs: 350,
        minTurnDurationMs: 900,
        maxTurnMs: 5000
    },
    quiet: {
        speechThreshold: 0.008,
        silenceThreshold: 0.003,
        autoRestartThreshold: 0.005,
        silenceDurationMs: 400,
        minTurnDurationMs: 800,
        maxTurnMs: 6000
    },
    noisy: {
        speechThreshold: 0.025,
        silenceThreshold: 0.015,
        autoRestartThreshold: 0.020,
        silenceDurationMs: 300,
        minTurnDurationMs: 1000,
        maxTurnMs: 4000
    },
    fast: {
        speechThreshold: 0.015,
        silenceThreshold: 0.008,
        autoRestartThreshold: 0.012,
        silenceDurationMs: 250,
        minTurnDurationMs: 600,
        maxTurnMs: 3000
    }
};

// Export for use in other modules
if (typeof module !== 'undefined' && module.exports) {
    module.exports = { VADProcessor, VAD_PRESETS };
}
