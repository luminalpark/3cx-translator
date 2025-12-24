/**
 * 3CX Translation Bridge - Main Application
 * Orchestrates SIP calls with real-time Gemini translation
 */

// Global state
let sipHandler = null;
let geminiClientInbound = null;   // Remote→User translation (e.g., EN→IT)
let geminiClientOutbound = null;  // User→Remote translation (e.g., IT→EN)
let inboundVad = null;
let outboundVad = null;
let audioContext = null;
let inboundProcessor = null;
let outboundProcessor = null;
let audioInjector = null;         // For injecting translated audio back to WebRTC

// Call state
let callTimer = null;
let callStartTime = null;

// UI Elements
const elements = {};

/**
 * Initialize on page load
 */
document.addEventListener('DOMContentLoaded', () => {
    // Cache UI elements
    elements.janusUrl = document.getElementById('janusUrl');
    elements.sipServer = document.getElementById('sipServer');
    elements.sipExtension = document.getElementById('sipExtension');
    elements.sipAuthUser = document.getElementById('sipAuthUser');  // 3CX Auth ID
    elements.sipPassword = document.getElementById('sipPassword');
    elements.geminiUrl = document.getElementById('geminiUrl');
    elements.sourceLang = document.getElementById('sourceLang');  // Remote speaker language
    elements.targetLang = document.getElementById('targetLang');  // Your language

    elements.connectBtn = document.getElementById('connectBtn');
    elements.disconnectBtn = document.getElementById('disconnectBtn');
    elements.dialBtn = document.getElementById('dialBtn');
    elements.dialNumber = document.getElementById('dialNumber');

    elements.janusStatus = document.getElementById('janusStatus');
    elements.sipStatus = document.getElementById('sipStatus');
    elements.geminiInboundStatus = document.getElementById('geminiInboundStatus');
    elements.geminiOutboundStatus = document.getElementById('geminiOutboundStatus');
    elements.callStatus = document.getElementById('callStatus');
    elements.callStatusText = document.getElementById('callStatusText');

    elements.incomingCallAlert = document.getElementById('incomingCallAlert');
    elements.callerNumber = document.getElementById('callerNumber');
    elements.activeCallControls = document.getElementById('activeCallControls');
    elements.callTimerDisplay = document.getElementById('callTimer');

    elements.inboundVad = document.getElementById('inboundVad');
    elements.outboundVad = document.getElementById('outboundVad');
    elements.inboundSource = document.getElementById('inboundSource');
    elements.inboundTranslated = document.getElementById('inboundTranslated');
    elements.outboundSource = document.getElementById('outboundSource');
    elements.outboundTranslated = document.getElementById('outboundTranslated');

    elements.log = document.getElementById('log');

    // Audio device selectors
    elements.audioInput = document.getElementById('audioInput');
    elements.audioOutput = document.getElementById('audioOutput');

    // Load saved settings from localStorage
    loadSettings();

    // Enumerate audio devices
    enumerateAudioDevices();

    // Auto-save language settings when changed
    elements.sourceLang.addEventListener('change', saveSettings);
    elements.targetLang.addEventListener('change', saveSettings);
    elements.audioInput.addEventListener('change', saveSettings);
    elements.audioOutput.addEventListener('change', () => {
        saveSettings();
        setAudioOutputDevice();
    });

    log('Bridge initialized', 'event');
});

/**
 * Enumerate available audio devices and populate dropdowns
 */
async function enumerateAudioDevices() {
    try {
        // Request permission first (needed to get device labels)
        await navigator.mediaDevices.getUserMedia({ audio: true });

        const devices = await navigator.mediaDevices.enumerateDevices();

        const audioInputs = devices.filter(d => d.kind === 'audioinput');
        const audioOutputs = devices.filter(d => d.kind === 'audiooutput');

        // Populate input devices (microphones)
        elements.audioInput.innerHTML = '';
        audioInputs.forEach((device, index) => {
            const option = document.createElement('option');
            option.value = device.deviceId;
            option.text = device.label || `Microphone ${index + 1}`;
            elements.audioInput.appendChild(option);
        });

        // Populate output devices (speakers/headphones)
        elements.audioOutput.innerHTML = '';
        audioOutputs.forEach((device, index) => {
            const option = document.createElement('option');
            option.value = device.deviceId;
            option.text = device.label || `Speaker ${index + 1}`;
            elements.audioOutput.appendChild(option);
        });

        // Restore saved selections
        const saved = localStorage.getItem(STORAGE_KEY);
        if (saved) {
            const settings = JSON.parse(saved);
            if (settings.audioInput) elements.audioInput.value = settings.audioInput;
            if (settings.audioOutput) elements.audioOutput.value = settings.audioOutput;
        }

        log(`Found ${audioInputs.length} microphones, ${audioOutputs.length} speakers`, 'event');
    } catch (err) {
        log(`Error enumerating devices: ${err.message}`, 'error');
    }
}

/**
 * Set the audio output device for remote audio and Gemini playback
 */
async function setAudioOutputDevice() {
    const deviceId = elements.audioOutput.value;

    if (!deviceId) return;

    try {
        // Update Gemini client output device (for translated audio)
        if (geminiClientInbound) {
            await geminiClientInbound.setAudioOutputDevice(deviceId);
        }

        log(`Audio output set to: ${elements.audioOutput.options[elements.audioOutput.selectedIndex].text}`, 'event');
    } catch (err) {
        log(`Error setting audio output: ${err.message}`, 'error');
    }
}

// ============================================
// Settings Persistence (localStorage)
// ============================================

const STORAGE_KEY = 'webrtc-bridge-settings';

/**
 * Load settings from localStorage
 */
function loadSettings() {
    try {
        const saved = localStorage.getItem(STORAGE_KEY);
        if (!saved) return;

        const settings = JSON.parse(saved);

        if (settings.janusUrl) elements.janusUrl.value = settings.janusUrl;
        if (settings.sipServer) elements.sipServer.value = settings.sipServer;
        if (settings.sipExtension) elements.sipExtension.value = settings.sipExtension;
        if (settings.sipAuthUser) elements.sipAuthUser.value = settings.sipAuthUser;
        if (settings.sipPassword) elements.sipPassword.value = settings.sipPassword;
        if (settings.geminiUrl) elements.geminiUrl.value = settings.geminiUrl;
        if (settings.sourceLang) elements.sourceLang.value = settings.sourceLang;
        if (settings.targetLang) elements.targetLang.value = settings.targetLang;

        console.log('Settings loaded from localStorage');
    } catch (err) {
        console.warn('Failed to load settings:', err);
    }
}

/**
 * Save settings to localStorage
 */
function saveSettings() {
    try {
        const settings = {
            janusUrl: elements.janusUrl.value,
            sipServer: elements.sipServer.value,
            sipExtension: elements.sipExtension.value,
            sipAuthUser: elements.sipAuthUser.value,
            sipPassword: elements.sipPassword.value,
            geminiUrl: elements.geminiUrl.value,
            sourceLang: elements.sourceLang.value,
            targetLang: elements.targetLang.value,
            audioInput: elements.audioInput.value,
            audioOutput: elements.audioOutput.value
        };

        localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
        console.log('Settings saved to localStorage');
    } catch (err) {
        console.warn('Failed to save settings:', err);
    }
}

/**
 * Connect to Janus and Gemini
 */
async function connect() {
    try {
        elements.connectBtn.disabled = true;
        setStatus('janusStatus', 'connecting');
        setStatus('geminiInboundStatus', 'connecting');
        setStatus('geminiOutboundStatus', 'connecting');

        const sourceLang = elements.sourceLang.value;  // Remote speaker language
        const targetLang = elements.targetLang.value;  // Your language

        // Initialize SIP handler
        sipHandler = new SIPHandler({
            janusUrl: elements.janusUrl.value,
            sipServer: elements.sipServer.value,
            sipExtension: elements.sipExtension.value,
            sipAuthUser: elements.sipAuthUser.value,  // 3CX Auth ID
            sipPassword: elements.sipPassword.value,
            displayName: '3CX Translation Bridge',

            onJanusConnected: () => setStatus('janusStatus', 'connected'),
            onJanusDisconnected: () => {
                setStatus('janusStatus', 'disconnected');
                setStatus('sipStatus', 'disconnected');
            },
            onSipRegistered: (uri) => {
                setStatus('sipStatus', 'connected');
                elements.dialBtn.disabled = false;
                log(`SIP registered: ${uri}`, 'sip');
            },
            onSipUnregistered: () => {
                setStatus('sipStatus', 'disconnected');
                elements.dialBtn.disabled = true;
            },
            onIncomingCall: handleIncomingCall,
            onCallAccepted: handleCallAccepted,
            onCallEnded: handleCallEnded,
            onRemoteStream: handleRemoteStream,
            onError: (err) => log(`SIP Error: ${err.message}`, 'error'),
            onLog: log
        });

        // Connect to Janus
        await sipHandler.connect();
        log('Connected to Janus', 'event');

        // Register with 3CX
        await sipHandler.register();

        // Initialize Gemini client for INBOUND (Remote → User)
        // Example: Remote speaks English, you hear Italian
        geminiClientInbound = new GeminiClient({
            url: elements.geminiUrl.value,
            sourceLang: sourceLang,  // Remote language
            targetLang: targetLang,  // Your language
            audioOutputDeviceId: elements.audioOutput?.value || null,  // Play on selected device

            onConnected: () => setStatus('geminiInboundStatus', 'connected'),
            onDisconnected: () => setStatus('geminiInboundStatus', 'disconnected'),
            onStreamingReady: () => log('Gemini Inbound streaming ready', 'event'),
            onTurnComplete: () => {
                if (inboundVad) inboundVad.onTurnComplete();
            },
            onSourceText: (text) => {
                elements.inboundSource.textContent = text;
                log(`[IN] Source: "${text}"`, 'translation');
            },
            onTranslatedText: (text) => {
                elements.inboundTranslated.textContent = text;
            },
            onTranslatedAudio: handleInboundTranslatedAudio,
            onError: (err) => log(`Gemini Inbound Error: ${err.message}`, 'error'),
            onLog: (msg, type) => log(`[IN] ${msg}`, type)
        });

        // Initialize Gemini client for OUTBOUND (User → Remote)
        // Example: You speak Italian, remote hears English
        geminiClientOutbound = new GeminiClient({
            url: elements.geminiUrl.value,
            sourceLang: targetLang,  // Your language (reversed)
            targetLang: sourceLang,  // Remote language (reversed)

            onConnected: () => setStatus('geminiOutboundStatus', 'connected'),
            onDisconnected: () => setStatus('geminiOutboundStatus', 'disconnected'),
            onStreamingReady: () => log('Gemini Outbound streaming ready', 'event'),
            onTurnComplete: () => {
                if (outboundVad) outboundVad.onTurnComplete();
            },
            onSourceText: (text) => {
                elements.outboundSource.textContent = text;
                log(`[OUT] Source: "${text}"`, 'translation');
            },
            onTranslatedText: (text) => {
                elements.outboundTranslated.textContent = text;
            },
            onTranslatedAudio: handleOutboundTranslatedAudio,
            onError: (err) => log(`Gemini Outbound Error: ${err.message}`, 'error'),
            onLog: (msg, type) => log(`[OUT] ${msg}`, type)
        });

        // Connect both Gemini clients
        await geminiClientInbound.connect();
        log(`Gemini Inbound connected: ${sourceLang} → ${targetLang}`, 'event');

        await geminiClientOutbound.connect();
        log(`Gemini Outbound connected: ${targetLang} → ${sourceLang}`, 'event');

        // Initialize audio injector for outbound translated audio
        audioInjector = new AudioInjector();
        log('Audio injector initialized', 'event');

        // Save settings on successful connection
        saveSettings();

        elements.disconnectBtn.disabled = false;

    } catch (err) {
        log(`Connection error: ${err.message}`, 'error');
        elements.connectBtn.disabled = false;
        setStatus('janusStatus', 'error');
        setStatus('geminiInboundStatus', 'error');
        setStatus('geminiOutboundStatus', 'error');
    }
}

/**
 * Disconnect from all services
 */
function disconnect() {
    if (sipHandler) {
        sipHandler.disconnect();
        sipHandler = null;
    }
    if (geminiClientInbound) {
        geminiClientInbound.disconnect();
        geminiClientInbound = null;
    }
    if (geminiClientOutbound) {
        geminiClientOutbound.disconnect();
        geminiClientOutbound = null;
    }
    if (audioInjector) {
        audioInjector.close();
        audioInjector = null;
    }
    stopCallTimer();

    elements.connectBtn.disabled = false;
    elements.disconnectBtn.disabled = true;
    elements.dialBtn.disabled = true;

    setStatus('janusStatus', 'disconnected');
    setStatus('sipStatus', 'disconnected');
    setStatus('geminiInboundStatus', 'disconnected');
    setStatus('geminiOutboundStatus', 'disconnected');
    setStatus('callStatus', 'disconnected');
    elements.callStatusText.textContent = 'No Call';

    log('Disconnected', 'event');
}

/**
 * Handle incoming call
 */
function handleIncomingCall(info) {
    log(`Incoming call from ${info.displayName}`, 'sip');

    elements.callerNumber.textContent = info.displayName;
    elements.incomingCallAlert.style.display = 'block';

    setStatus('callStatus', 'connecting');
    elements.callStatusText.textContent = 'Incoming...';

    // Play ringtone (optional)
    // playRingtone();
}

/**
 * Answer incoming call
 */
function answerCall() {
    if (sipHandler) {
        sipHandler.answer();
    }
    elements.incomingCallAlert.style.display = 'none';
}

/**
 * Reject incoming call
 */
function rejectCall() {
    if (sipHandler) {
        sipHandler.decline();
    }
    elements.incomingCallAlert.style.display = 'none';
    setStatus('callStatus', 'disconnected');
    elements.callStatusText.textContent = 'No Call';
}

/**
 * Make outbound call
 */
function makeCall() {
    const number = elements.dialNumber.value.trim();
    if (!number) {
        log('Please enter a number', 'error');
        return;
    }

    if (sipHandler && sipHandler.registered) {
        sipHandler.call(number);
        setStatus('callStatus', 'connecting');
        elements.callStatusText.textContent = 'Calling...';
    }
}

/**
 * Handle call accepted (connected)
 */
async function handleCallAccepted(info) {
    log(`Call connected with ${info.peer}`, 'sip');

    setStatus('callStatus', 'connected');
    elements.callStatusText.textContent = 'In Call';

    elements.activeCallControls.style.display = 'block';
    startCallTimer();

    // Note: VAD is initialized in setupAudioProcessing when remote stream arrives

    // Replace WebRTC audio track with AudioInjector output
    // This ensures the remote peer hears ONLY the translated audio
    if (audioInjector && sipHandler) {
        // Small delay to ensure WebRTC connection is fully established
        setTimeout(async () => {
            const sender = sipHandler.getAudioSender();
            if (sender) {
                const success = await audioInjector.replaceTrack(sender);
                if (success) {
                    log('Audio track replaced - remote will hear translated audio only', 'event');
                } else {
                    log('Failed to replace audio track - remote hears original voice', 'error');
                }
            } else {
                log('No audio sender found - cannot inject translated audio', 'error');
            }
        }, 500);
    }
}

/**
 * Handle call ended
 */
function handleCallEnded(info) {
    log(`Call ended: ${info.code} ${info.reason}`, 'sip');

    setStatus('callStatus', 'disconnected');
    elements.callStatusText.textContent = 'No Call';

    elements.activeCallControls.style.display = 'none';
    elements.incomingCallAlert.style.display = 'none';

    stopCallTimer();
    cleanupAudio();

    // Reset mute state
    operatorMuted = false;
    const muteBtn = document.getElementById('muteBtn');
    if (muteBtn) {
        muteBtn.classList.remove('active');
        muteBtn.querySelector('span:last-child').textContent = 'Mute';
        muteBtn.querySelector('.icon').textContent = '🎤';
    }

    // Reset audio meters
    const meterDbOut = document.getElementById('audioDbOut');
    if (meterDbOut) {
        meterDbOut.textContent = '-∞ dB';
        meterDbOut.style.color = '#888';
    }
    const meterDbIn = document.getElementById('audioDbIn');
    if (meterDbIn) {
        meterDbIn.textContent = '-∞ dB';
    }

    // Reset debug counters for next call
    audioDebugCounter = { inbound: 0, outbound: 0 };
    inboundMaxRms = 0;
    lastInboundLogTime = 0;

    // Clear translation display
    elements.inboundSource.textContent = '';
    elements.inboundTranslated.textContent = '';
    elements.outboundSource.textContent = '';
    elements.outboundTranslated.textContent = '';
}

/**
 * Hang up current call
 */
function hangupCall() {
    if (sipHandler) {
        sipHandler.hangup();
    }
}

/**
 * Handle remote audio stream
 */
function handleRemoteStream(stream) {
    log('Remote stream received - setting up audio processing', 'event');

    // Debug: Check stream tracks
    const audioTracks = stream.getAudioTracks();
    log(`Remote stream has ${audioTracks.length} audio track(s)`, 'event');
    if (audioTracks.length > 0) {
        log(`Audio track: ${audioTracks[0].label}, enabled: ${audioTracks[0].enabled}, muted: ${audioTracks[0].muted}`, 'event');
    }

    // Set up remote audio element (for stream capture, NOT playback)
    // The translated audio from Gemini will be played instead
    const remoteAudio = document.getElementById('remoteAudio');
    remoteAudio.srcObject = stream;
    remoteAudio.muted = true;  // MUTED: we play translated audio, not original
    remoteAudio.volume = 0;    // Extra safety: zero volume

    // Explicitly try to play (in case autoplay is blocked)
    remoteAudio.play()
        .then(() => {
            log('Remote audio playback started', 'event');
            log(`Remote audio state: paused=${remoteAudio.paused}, muted=${remoteAudio.muted}, volume=${remoteAudio.volume}`, 'event');
        })
        .catch(err => {
            log(`Remote audio play error: ${err.message}`, 'error');
            // Fallback: try to play on next user interaction
            document.addEventListener('click', () => {
                remoteAudio.play().catch(e => log(`Retry play failed: ${e.message}`, 'error'));
            }, { once: true });
        });

    // Monitor audio element for debugging
    remoteAudio.onplaying = () => log('Remote audio: playing event fired', 'event');
    remoteAudio.onpause = () => log('Remote audio: paused', 'warn');
    remoteAudio.onerror = (e) => log(`Remote audio error: ${e.target.error?.message || 'unknown'}`, 'error');

    // Set the selected output device
    setAudioOutputDevice();

    // Set up audio processing for translation
    setupAudioProcessing(stream);
}

/**
 * Set up audio processing for VAD and translation
 */
async function setupAudioProcessing(remoteStream) {
    try {
        // Initialize VAD processors first (before audio starts flowing)
        initializeVAD();

        // Create audio context with default sample rate (matches WebRTC, typically 48kHz)
        // Note: VAD works with any sample rate - it calculates RMS from raw samples
        audioContext = new AudioContext();
        log(`AudioContext created with sample rate: ${audioContext.sampleRate}Hz, state: ${audioContext.state}`, 'event');

        // Resume AudioContext if suspended (browsers require user interaction)
        if (audioContext.state === 'suspended') {
            await audioContext.resume();
            log(`AudioContext resumed, new state: ${audioContext.state}`, 'event');
        }

        // Get local microphone (use selected device if available)
        const selectedMicId = elements.audioInput?.value;
        const audioConstraints = {
            channelCount: 1,
            echoCancellation: true,
            noiseSuppression: true
        };
        if (selectedMicId) {
            audioConstraints.deviceId = { exact: selectedMicId };
        }
        const localStream = await navigator.mediaDevices.getUserMedia({
            audio: audioConstraints
        });

        // Log which microphone is being used
        const audioTrack = localStream.getAudioTracks()[0];
        if (audioTrack) {
            const settings = audioTrack.getSettings();
            log(`[OUTBOUND SETUP] Microphone: ${audioTrack.label}`, 'event');
            log(`[OUTBOUND SETUP] Device ID: ${settings.deviceId?.slice(0,8)}...`, 'event');
            log(`[OUTBOUND SETUP] Sample rate: ${settings.sampleRate || 'unknown'}Hz`, 'event');
        }

        // Process remote audio (inbound - for translation/VAD)
        const remoteAudio = document.getElementById('remoteAudio');
        let remoteSource;
        let useMediaElement = false;

        // Debug: log audio element state
        log(`[INBOUND SETUP] remoteAudio element - src: ${remoteAudio.src}, srcObject: ${remoteAudio.srcObject ? 'MediaStream' : 'null'}, paused: ${remoteAudio.paused}, muted: ${remoteAudio.muted}`, 'event');

        // Debug: log remoteStream info
        if (remoteStream) {
            const tracks = remoteStream.getAudioTracks();
            log(`[INBOUND SETUP] remoteStream has ${tracks.length} audio track(s)`, 'event');
            tracks.forEach((t, i) => {
                log(`[INBOUND SETUP] Track ${i}: id=${t.id}, label=${t.label}, enabled=${t.enabled}, muted=${t.muted}, readyState=${t.readyState}`, 'event');
            });
        } else {
            log('[INBOUND SETUP] WARNING: remoteStream is null!', 'error');
        }

        // Use MediaStreamSource directly from remoteStream for reliable WebRTC audio capture
        // MediaElementSource can have issues with WebRTC streams
        if (remoteStream && remoteStream.getAudioTracks().length > 0) {
            remoteSource = audioContext.createMediaStreamSource(remoteStream);
            useMediaElement = false;
            log('[INBOUND SETUP] Using MediaStreamSource from remoteStream', 'event');
        } else {
            // Fallback to MediaElementSource if remoteStream not available
            try {
                remoteSource = audioContext.createMediaElementSource(remoteAudio);
                useMediaElement = true;
                log('[INBOUND SETUP] Using MediaElementSource - audio will play through AudioContext', 'event');
            } catch (err) {
                log(`[INBOUND SETUP] MediaElementSource failed: ${err.message}`, 'error');
                throw new Error('No audio source available for inbound processing');
            }
        }

        // Set up the audio processing chain
        inboundProcessor = audioContext.createScriptProcessor(4096, 1, 1);
        inboundProcessor.onaudioprocess = (e) => processInboundAudio(e);

        // Create gain node to amplify inbound audio for VAD detection
        // WebRTC audio often arrives at low levels
        const inboundGain = audioContext.createGain();
        inboundGain.gain.value = 5.0; // Amplify 5x for better VAD detection
        log(`[INBOUND SETUP] Created gain node with value: ${inboundGain.gain.value}`, 'event');

        // Route: remoteSource → inboundGain → inboundProcessor → destination
        remoteSource.connect(inboundGain);
        inboundGain.connect(inboundProcessor);
        log(`[INBOUND SETUP] Connected: remoteSource → inboundGain → inboundProcessor`, 'event');

        if (useMediaElement) {
            // MediaElementSource: route through processor to destination for both analysis and playback
            inboundProcessor.connect(audioContext.destination);
            log('[INBOUND SETUP] Connected: inboundProcessor → audioContext.destination (playback + analysis)', 'event');
        } else {
            // MediaStreamSource: audio element handles playback, processor just needs to stay active
            const inboundSilentGain = audioContext.createGain();
            inboundSilentGain.gain.value = 0;
            inboundProcessor.connect(inboundSilentGain);
            inboundSilentGain.connect(audioContext.destination);
            log('[INBOUND SETUP] Connected: inboundProcessor → silentGain → destination (analysis only, audio element plays)', 'event');
        }

        // Process local audio (outbound - for translation)
        const localSource = audioContext.createMediaStreamSource(localStream);
        outboundProcessor = audioContext.createScriptProcessor(4096, 1, 1);
        outboundProcessor.onaudioprocess = (e) => processOutboundAudio(e);
        localSource.connect(outboundProcessor);
        // Connect to destination through a zero-gain node to keep processor active
        // (ScriptProcessorNode requires being connected to destination to fire onaudioprocess events)
        const silentGain = audioContext.createGain();
        silentGain.gain.value = 0;
        outboundProcessor.connect(silentGain);
        silentGain.connect(audioContext.destination);

        log('Audio processing initialized', 'event');

    } catch (err) {
        log(`Audio setup error: ${err.message}`, 'error');
    }
}

/**
 * Initialize VAD processors for bidirectional translation
 */
function initializeVAD() {
    // Avoid reinitializing if already set up
    if (inboundVad && outboundVad) {
        return;
    }

    // Inbound VAD (remote speaker → translated for you)
    inboundVad = new VADProcessor({
        onStateChange: (state) => {
            elements.inboundVad.textContent = state;
            elements.inboundVad.className = `vad-indicator ${state.toLowerCase()}`;
        },
        onActivityStart: () => geminiClientInbound?.sendActivityStart(),
        onActivityEnd: () => geminiClientInbound?.sendActivityEnd(),
        onSendAudio: (data) => geminiClientInbound?.sendAudio(data),
        onRmsUpdate: (rms) => updateRmsMeter('inbound', rms)
    });

    // Outbound VAD (you → translated for remote)
    // Uses slightly higher thresholds since microphone audio is typically cleaner
    outboundVad = new VADProcessor({
        speechThreshold: 0.015,       // Higher for direct microphone
        silenceThreshold: 0.008,
        autoRestartThreshold: 0.012,
        silenceDurationMs: 400,       // Slightly longer pause
        minTurnDurationMs: 800,

        onStateChange: (state) => {
            elements.outboundVad.textContent = state;
            elements.outboundVad.className = `vad-indicator ${state.toLowerCase()}`;
        },
        onActivityStart: () => geminiClientOutbound?.sendActivityStart(),
        onActivityEnd: () => geminiClientOutbound?.sendActivityEnd(),
        onSendAudio: (data) => geminiClientOutbound?.sendAudio(data),
        onRmsUpdate: (rms) => updateRmsMeter('outbound', rms)
    });

    log('VAD initialized for bidirectional translation (Full Duplex)', 'event');
}

// Debug: count audio process calls and track max RMS seen
let audioDebugCounter = { inbound: 0, outbound: 0 };
let inboundMaxRms = 0;
let lastInboundLogTime = 0;

/**
 * Process inbound audio (remote speaker → translated for you)
 */
function processInboundAudio(e) {
    if (!inboundVad) {
        if (audioDebugCounter.inbound++ < 3) {
            console.log('[INBOUND DEBUG] processInboundAudio called but inboundVad is null');
        }
        return;
    }

    const inputData = e.inputBuffer.getChannelData(0);

    // Calculate RMS (before resampling for accurate debug info)
    const rms = Math.sqrt(inputData.reduce((sum, x) => sum + x * x, 0) / inputData.length);

    // Track max RMS
    if (rms > inboundMaxRms) {
        inboundMaxRms = rms;
    }

    // Log periodically (every 2 seconds) to avoid flooding console
    const now = Date.now();
    if (now - lastInboundLogTime > 2000) {
        lastInboundLogTime = now;
        console.log(`[INBOUND DEBUG] samples: ${inputData.length}, RMS: ${rms.toFixed(6)}, maxRMS: ${inboundMaxRms.toFixed(6)}, first5samples: [${inputData.slice(0,5).map(x => x.toFixed(4)).join(', ')}]`);
    }

    // Resample from browser sample rate (typically 48kHz) to 16kHz for Gemini
    const sourceSampleRate = audioContext ? audioContext.sampleRate : 48000;
    const resampled = resampleTo16k(inputData, sourceSampleRate);

    // Convert Float32 to Int16
    const int16Data = convertFloat32ToInt16(resampled);

    // Process through VAD for RMS metering
    inboundVad.processAudio(int16Data);
}

/**
 * Resample audio from source sample rate to 16kHz
 * Uses simple linear interpolation for quality resampling
 * @param {Float32Array} inputData - Audio samples at source sample rate
 * @param {number} sourceSampleRate - Source sample rate (e.g., 48000)
 * @returns {Float32Array} - Audio samples at 16kHz
 */
function resampleTo16k(inputData, sourceSampleRate) {
    if (sourceSampleRate === 16000) {
        return inputData; // No resampling needed
    }

    const ratio = sourceSampleRate / 16000;
    const outputLength = Math.floor(inputData.length / ratio);
    const output = new Float32Array(outputLength);

    for (let i = 0; i < outputLength; i++) {
        const srcIndex = i * ratio;
        const srcIndexFloor = Math.floor(srcIndex);
        const srcIndexCeil = Math.min(srcIndexFloor + 1, inputData.length - 1);
        const fraction = srcIndex - srcIndexFloor;

        // Linear interpolation for smoother resampling
        output[i] = inputData[srcIndexFloor] * (1 - fraction) + inputData[srcIndexCeil] * fraction;
    }

    return output;
}

/**
 * Process outbound audio (you → translated for remote)
 */
function processOutboundAudio(e) {
    if (!outboundVad) return;

    // Skip processing if operator is muted
    if (operatorMuted) {
        return;
    }

    const inputData = e.inputBuffer.getChannelData(0);

    // Resample from browser sample rate (typically 48kHz) to 16kHz for Gemini
    const sourceSampleRate = audioContext ? audioContext.sampleRate : 48000;
    const resampled = resampleTo16k(inputData, sourceSampleRate);

    // Convert Float32 to Int16
    const int16Data = convertFloat32ToInt16(resampled);

    // Process through VAD for RMS metering and translation
    outboundVad.processAudio(int16Data);
}

/**
 * Convert Float32 audio to Int16 PCM
 */
function convertFloat32ToInt16(float32Array) {
    const int16Data = new Int16Array(float32Array.length);
    for (let i = 0; i < float32Array.length; i++) {
        const s = Math.max(-1, Math.min(1, float32Array[i]));
        int16Data[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
    }
    return int16Data;
}

/**
 * Handle INBOUND translated audio from Gemini (plays locally for you)
 */
function handleInboundTranslatedAudio(audioData) {
    // Audio is played by GeminiClient's built-in playback
    // This is what YOU hear - the translated remote speaker
    log(`[IN] Translated audio: ${audioData.length} samples`, 'audio');
}

/**
 * Handle OUTBOUND translated audio from Gemini (sends to remote peer)
 */
function handleOutboundTranslatedAudio(audioData) {
    // Inject translated audio into WebRTC stream for the remote peer
    // The remote will hear ONLY this translated audio, not your original voice
    if (audioInjector) {
        audioInjector.injectAudio(audioData);
        log(`[OUT] Injected ${audioData.length} samples to WebRTC`, 'audio');
    }
}

/**
 * Cleanup audio processing
 */
function cleanupAudio() {
    // Disconnect Gemini clients first
    if (geminiClientInbound) {
        log('Disconnecting Gemini Inbound...', 'event');
        geminiClientInbound.disconnect();
    }
    if (geminiClientOutbound) {
        log('Disconnecting Gemini Outbound...', 'event');
        geminiClientOutbound.disconnect();
    }

    // Stop audio injector
    if (audioInjector) {
        audioInjector.stop();
    }

    // Disconnect audio processors
    if (inboundProcessor) {
        inboundProcessor.disconnect();
        inboundProcessor = null;
    }
    if (outboundProcessor) {
        outboundProcessor.disconnect();
        outboundProcessor = null;
    }
    if (audioContext) {
        audioContext.close();
        audioContext = null;
    }
    inboundVad = null;
    outboundVad = null;
}

// ============================================
// UI Helpers
// ============================================

/**
 * Set status indicator
 */
function setStatus(elementId, status) {
    const element = elements[elementId];
    if (!element) return;

    element.className = 'status-dot';
    switch (status) {
        case 'connected':
            element.classList.add('connected');
            break;
        case 'connecting':
            element.classList.add('connecting');
            break;
        case 'error':
            element.classList.add('error');
            break;
        case 'active':
            element.classList.add('active');
            break;
    }
}

/**
 * Log message
 */
function log(message, type = '') {
    console.log(`[${type}] ${message}`);

    if (!elements.log) return;

    const entry = document.createElement('div');
    entry.className = 'log-entry ' + type;
    entry.textContent = `[${new Date().toLocaleTimeString()}] ${message}`;
    elements.log.appendChild(entry);
    elements.log.scrollTop = elements.log.scrollHeight;
}

/**
 * Clear log
 */
function clearLog() {
    if (elements.log) {
        elements.log.innerHTML = '';
    }
}

/**
 * Toggle collapsible panel
 */
function togglePanel(contentId) {
    const content = document.getElementById(contentId);
    const panel = content.parentElement;

    if (content.style.display === 'none') {
        content.style.display = 'block';
        panel.classList.add('expanded');
    } else {
        content.style.display = 'none';
        panel.classList.remove('expanded');
    }
}

/**
 * Dial digit
 */
function dialDigit(digit) {
    elements.dialNumber.value += digit;

    // Send DTMF if in call
    if (sipHandler?.inCall) {
        sipHandler.sendDtmf(digit);
    }
}

/**
 * Handle keypress in dial input
 */
function handleDialKeypress(event) {
    if (event.key === 'Enter') {
        makeCall();
    }
}

// Track operator mute state
let operatorMuted = false;

/**
 * Toggle mute (operator)
 */
function toggleMute() {
    operatorMuted = !operatorMuted;

    // Mute WebRTC audio track
    if (sipHandler) {
        sipHandler.toggleMute();
    }

    // Update button appearance
    const btn = document.getElementById('muteBtn');
    btn.classList.toggle('active', operatorMuted);
    btn.querySelector('span:last-child').textContent = operatorMuted ? 'Unmute' : 'Mute';
    btn.querySelector('.icon').textContent = operatorMuted ? '🔇' : '🎤';

    // Update meter to show muted state
    const meterDb = document.getElementById('audioDbOut');
    if (meterDb && operatorMuted) {
        meterDb.textContent = 'MUTED';
        meterDb.style.color = '#ff6b6b';
    } else if (meterDb) {
        meterDb.style.color = '#888';
    }

    log(`Operator ${operatorMuted ? 'muted' : 'unmuted'}`, 'event');
}

/**
 * Check if operator is muted (used by VAD processing)
 */
function isOperatorMuted() {
    return operatorMuted;
}

/**
 * Toggle hold
 */
function toggleHold() {
    // TODO: Implement hold via Janus SIP
    const btn = document.getElementById('holdBtn');
    btn.classList.toggle('active');
}

/**
 * Start call timer
 */
function startCallTimer() {
    callStartTime = Date.now();
    callTimer = setInterval(updateCallTimer, 1000);
    updateCallTimer();
}

/**
 * Stop call timer
 */
function stopCallTimer() {
    if (callTimer) {
        clearInterval(callTimer);
        callTimer = null;
    }
    callStartTime = null;
    if (elements.callTimerDisplay) {
        elements.callTimerDisplay.textContent = '00:00';
    }
}

/**
 * Update call timer display
 */
function updateCallTimer() {
    if (!callStartTime || !elements.callTimerDisplay) return;

    const elapsed = Math.floor((Date.now() - callStartTime) / 1000);
    const minutes = Math.floor(elapsed / 60);
    const seconds = elapsed % 60;

    elements.callTimerDisplay.textContent =
        `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
}

// Peak hold for audio meters
const peakHold = {
    inbound: { value: 0, time: 0 },
    outbound: { value: 0, time: 0 }
};
const PEAK_HOLD_MS = 1500;  // How long to hold the peak indicator

/**
 * Update RMS meter
 */
function updateRmsMeter(direction, rms) {
    const bar = document.getElementById(`${direction}RmsBar`);
    const value = document.getElementById(`${direction}RmsValue`);

    if (bar) {
        const percentage = Math.min(100, rms * 1000);
        bar.style.width = percentage + '%';
    }
    if (value) {
        value.textContent = rms.toFixed(4);
    }

    // Update visible audio meter
    const meterId = direction === 'inbound' ? 'In' : 'Out';
    const meterBar = document.getElementById(`audioMeter${meterId}`);
    const meterPeak = document.getElementById(`audioMeter${meterId}Peak`);
    const meterDb = document.getElementById(`audioDb${meterId}`);

    if (meterBar) {
        // Convert RMS to percentage (0-100) with logarithmic scale for better visualization
        // RMS range is typically 0 to ~0.3 for speech
        const percentage = Math.min(100, Math.max(0, rms * 300));
        meterBar.style.width = percentage + '%';

        // Update peak hold
        const now = Date.now();
        if (percentage > peakHold[direction].value || now - peakHold[direction].time > PEAK_HOLD_MS) {
            peakHold[direction].value = percentage;
            peakHold[direction].time = now;
        }

        if (meterPeak) {
            meterPeak.style.left = peakHold[direction].value + '%';
        }
    }

    if (meterDb) {
        // Convert RMS to dB
        if (rms > 0.0001) {
            const db = 20 * Math.log10(rms);
            meterDb.textContent = db.toFixed(1) + ' dB';
        } else {
            meterDb.textContent = '-∞ dB';
        }
    }
}

/**
 * Update VAD parameter
 */
function updateVadParam(param, value) {
    const numValue = parseFloat(value);

    switch (param) {
        case 'speech':
            document.getElementById('speechThresholdValue').textContent = numValue.toFixed(3);
            if (inboundVad) inboundVad.speechThreshold = numValue;
            if (outboundVad) outboundVad.speechThreshold = numValue;
            break;
        case 'silence':
            document.getElementById('silenceThresholdValue').textContent = numValue.toFixed(3);
            if (inboundVad) inboundVad.silenceThreshold = numValue;
            if (outboundVad) outboundVad.silenceThreshold = numValue;
            break;
        case 'silenceDuration':
            document.getElementById('silenceDurationValue').textContent = value;
            if (inboundVad) inboundVad.silenceDurationMs = parseInt(value);
            if (outboundVad) outboundVad.silenceDurationMs = parseInt(value);
            break;
    }

    log(`VAD param: ${param} = ${value}`, 'event');
}

/**
 * Apply VAD preset
 */
function applyVadPreset(preset) {
    const presets = VAD_PRESETS;
    const p = presets[preset];
    if (!p) return;

    // Update sliders
    document.getElementById('speechThreshold').value = p.speechThreshold;
    document.getElementById('silenceThreshold').value = p.silenceThreshold;
    document.getElementById('silenceDuration').value = p.silenceDurationMs;

    // Update VAD
    updateVadParam('speech', p.speechThreshold);
    updateVadParam('silence', p.silenceThreshold);
    updateVadParam('silenceDuration', p.silenceDurationMs);

    log(`Applied VAD preset: ${preset}`, 'event');
}

// ============================================
// AudioInjector Class
// ============================================

/**
 * AudioInjector - Injects translated audio into WebRTC stream
 *
 * This class creates a synthetic audio stream from PCM16 data
 * that can replace the original microphone track in WebRTC.
 * The remote peer will hear ONLY the translated audio.
 */
class AudioInjector {
    constructor() {
        this.audioContext = new AudioContext({ sampleRate: 16000 });
        this.destination = this.audioContext.createMediaStreamDestination();
        this.outputStream = this.destination.stream;
        this.audioQueue = [];
        this.isPlaying = false;
        this.trackReplaced = false;
    }

    /**
     * Get the output MediaStream for WebRTC
     * Use this to replace the microphone track
     */
    getOutputStream() {
        return this.outputStream;
    }

    /**
     * Get the audio track for replaceTrack()
     */
    getAudioTrack() {
        const tracks = this.outputStream.getAudioTracks();
        return tracks.length > 0 ? tracks[0] : null;
    }

    /**
     * Inject PCM16 audio data into the stream
     * @param {Int16Array} pcm16Data - Audio samples at 16kHz
     */
    injectAudio(pcm16Data) {
        this.audioQueue.push(pcm16Data);
        if (!this.isPlaying) {
            this._playNextChunk();
        }
    }

    /**
     * Play next chunk from queue
     */
    async _playNextChunk() {
        if (this.audioQueue.length === 0) {
            this.isPlaying = false;
            return;
        }

        // Resume context if suspended
        if (this.audioContext.state === 'suspended') {
            await this.audioContext.resume();
        }

        this.isPlaying = true;
        const int16Data = this.audioQueue.shift();

        // Convert Int16 to Float32
        const float32Data = new Float32Array(int16Data.length);
        for (let i = 0; i < int16Data.length; i++) {
            float32Data[i] = int16Data[i] / 32768;
        }

        // Create audio buffer
        const buffer = this.audioContext.createBuffer(1, float32Data.length, 16000);
        buffer.getChannelData(0).set(float32Data);

        // Play through destination (which feeds the MediaStream)
        const source = this.audioContext.createBufferSource();
        source.buffer = buffer;
        source.connect(this.destination);
        source.onended = () => this._playNextChunk();
        source.start();
    }

    /**
     * Replace WebRTC sender track with our synthetic audio
     * @param {RTCRtpSender} sender - The audio sender from peer connection
     */
    async replaceTrack(sender) {
        if (!sender) {
            console.error('AudioInjector: No sender provided');
            return false;
        }

        const audioTrack = this.getAudioTrack();
        if (!audioTrack) {
            console.error('AudioInjector: No audio track available');
            return false;
        }

        try {
            await sender.replaceTrack(audioTrack);
            this.trackReplaced = true;
            console.log('AudioInjector: Track replaced successfully');
            return true;
        } catch (err) {
            console.error('AudioInjector: Failed to replace track:', err);
            return false;
        }
    }

    /**
     * Clear audio queue
     */
    clearQueue() {
        this.audioQueue = [];
        this.isPlaying = false;
    }

    /**
     * Close and cleanup
     */
    close() {
        this.clearQueue();
        if (this.audioContext && this.audioContext.state !== 'closed') {
            this.audioContext.close();
        }
        this.audioContext = null;
        this.destination = null;
        this.outputStream = null;
    }
}
