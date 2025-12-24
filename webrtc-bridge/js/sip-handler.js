/**
 * Janus SIP Handler
 * Manages SIP registration and call control via Janus Gateway
 */

class SIPHandler {
    constructor(options = {}) {
        this.janusUrl = options.janusUrl || 'ws://localhost:8188';
        this.sipServer = options.sipServer || '';
        this.sipExtension = options.sipExtension || '900';
        this.sipAuthUser = options.sipAuthUser || '';  // 3CX Auth ID (if different from extension)
        this.sipPassword = options.sipPassword || '';
        this.displayName = options.displayName || '3CX Translation Bridge';

        this.janus = null;
        this.sipPlugin = null;
        this.registered = false;
        this.inCall = false;
        this.callId = null;

        // Media streams
        this.localStream = null;
        this.remoteStream = null;

        // Callbacks
        this.onJanusConnected = options.onJanusConnected || (() => {});
        this.onJanusDisconnected = options.onJanusDisconnected || (() => {});
        this.onSipRegistered = options.onSipRegistered || (() => {});
        this.onSipUnregistered = options.onSipUnregistered || (() => {});
        this.onIncomingCall = options.onIncomingCall || (() => {});
        this.onCallAccepted = options.onCallAccepted || (() => {});
        this.onCallEnded = options.onCallEnded || (() => {});
        this.onRemoteStream = options.onRemoteStream || (() => {});
        this.onError = options.onError || (() => {});
        this.onLog = options.onLog || (() => {});

        // Current JSEP offer (for incoming calls)
        this.currentJsep = null;
    }

    /**
     * Initialize Janus connection
     */
    async connect() {
        return new Promise((resolve, reject) => {
            if (typeof Janus === 'undefined') {
                reject(new Error('Janus library not loaded. Please include janus.js'));
                return;
            }

            // Initialize Janus library
            Janus.init({
                debug: 'all',
                callback: () => {
                    this._createSession(resolve, reject);
                }
            });
        });
    }

    _createSession(resolve, reject) {
        this.janus = new Janus({
            server: this.janusUrl,
            success: () => {
                this.onLog('Janus session created', 'event');
                this.onJanusConnected();
                this._attachSipPlugin(resolve, reject);
            },
            error: (error) => {
                this.onLog(`Janus error: ${error}`, 'error');
                this.onError(new Error(error));
                reject(new Error(error));
            },
            destroyed: () => {
                this.onLog('Janus session destroyed', 'event');
                this.onJanusDisconnected();
            }
        });
    }

    _attachSipPlugin(resolve, reject) {
        this.janus.attach({
            plugin: 'janus.plugin.sip',
            opaqueId: `sip-bridge-${Janus.randomString(12)}`,

            success: (pluginHandle) => {
                this.sipPlugin = pluginHandle;
                this.onLog('SIP plugin attached', 'event');
                resolve();
            },

            error: (error) => {
                this.onLog(`SIP plugin error: ${error}`, 'error');
                this.onError(new Error(error));
                reject(new Error(error));
            },

            consentDialog: (on) => {
                this.onLog(`Consent dialog: ${on}`, 'event');
                // Hook into track events early when WebRTC is being set up
                if (on && this.sipPlugin && !this._trackEventHooked) {
                    this._hookTrackEvents();
                }
            },

            iceState: (state) => {
                this.onLog(`ICE state: ${state}`, 'event');
                // Also try to hook here in case we missed the consent dialog
                if (state === 'checking' && this.sipPlugin && !this._trackEventHooked) {
                    this._hookTrackEvents();
                }
            },

            mediaState: (medium, on, mid) => {
                this.onLog(`Media state: ${medium} ${on ? 'on' : 'off'}`, 'event');
            },

            webrtcState: (on) => {
                this.onLog(`WebRTC ${on ? 'up' : 'down'}`, 'event');

                if (on && this.sipPlugin) {
                    // Try to hook if not already done
                    if (!this._trackEventHooked) {
                        this._hookTrackEvents();
                    }

                    // Fallback after a delay if stream wasn't received
                    setTimeout(() => {
                        this._checkAndGetStreams();
                    }, 500);
                }
            },

            slowLink: (uplink, lost) => {
                this.onLog(`Slow link: ${uplink ? 'uplink' : 'downlink'}, ${lost} packets lost`, 'event');
            },

            onmessage: (msg, jsep) => {
                this._handleSipMessage(msg, jsep);
            },

            onlocalstream: (stream) => {
                this.localStream = stream;
                this.onLog('Local stream received', 'event');
            },

            onremotestream: (stream) => {
                this.remoteStream = stream;
                this.onLog('Remote stream received', 'event');
                this.onRemoteStream(stream);
            },

            oncleanup: () => {
                this.onLog('SIP plugin cleanup', 'event');
                this.localStream = null;
                this.remoteStream = null;
            }
        });
    }

    /**
     * Register with SIP server
     */
    async register() {
        if (!this.sipPlugin) {
            throw new Error('SIP plugin not attached');
        }

        const sipUri = `sip:${this.sipExtension}@${this.sipServer}`;
        const proxy = `sip:${this.sipServer}`;

        const register = {
            request: 'register',
            username: sipUri,
            display_name: this.displayName,
            secret: this.sipPassword,
            proxy: proxy,
            // Auto-answer incoming calls
            autoaccept_reinvites: true
        };

        // 3CX uses separate Auth ID for authentication
        if (this.sipAuthUser) {
            register.authuser = this.sipAuthUser;
            this.onLog(`Registering as ${sipUri} (auth: ${this.sipAuthUser})...`, 'sip');
        } else {
            this.onLog(`Registering as ${sipUri}...`, 'sip');
        }

        this.sipPlugin.send({ message: register });
    }

    /**
     * Unregister from SIP server
     */
    unregister() {
        if (!this.sipPlugin || !this.registered) return;

        this.sipPlugin.send({
            message: { request: 'unregister' }
        });
    }

    /**
     * Make outbound call
     */
    async call(number) {
        if (!this.sipPlugin || !this.registered) {
            throw new Error('Not registered');
        }
        if (this.inCall) {
            throw new Error('Already in call');
        }

        const uri = number.includes('@')
            ? `sip:${number}`
            : `sip:${number}@${this.sipServer}`;

        this.onLog(`Calling ${uri}...`, 'sip');

        // Create offer
        this.sipPlugin.createOffer({
            media: {
                audioSend: true,
                audioRecv: true,
                videoSend: false,
                videoRecv: false
            },
            success: (jsep) => {
                this.sipPlugin.send({
                    message: {
                        request: 'call',
                        uri: uri
                    },
                    jsep: jsep
                });
            },
            error: (error) => {
                this.onLog(`Create offer error: ${error}`, 'error');
                this.onError(new Error(error));
            }
        });
    }

    /**
     * Answer incoming call
     */
    async answer() {
        if (!this.sipPlugin || !this.currentJsep) {
            throw new Error('No incoming call to answer');
        }

        this.onLog('Answering call...', 'sip');

        // Create answer
        this.sipPlugin.createAnswer({
            jsep: this.currentJsep,
            media: {
                audioSend: true,
                audioRecv: true,
                videoSend: false,
                videoRecv: false
            },
            success: (jsep) => {
                this.sipPlugin.send({
                    message: { request: 'accept' },
                    jsep: jsep
                });
            },
            error: (error) => {
                this.onLog(`Create answer error: ${error}`, 'error');
                this.onError(new Error(error));
            }
        });
    }

    /**
     * Decline incoming call
     */
    decline() {
        if (!this.sipPlugin) return;

        this.sipPlugin.send({
            message: { request: 'decline', code: 486 }
        });
        this.currentJsep = null;
    }

    /**
     * Hang up current call
     */
    hangup() {
        if (!this.sipPlugin || !this.inCall) return;

        this.onLog('Hanging up...', 'sip');
        this.sipPlugin.send({
            message: { request: 'hangup' }
        });
    }

    /**
     * Send DTMF digit
     */
    sendDtmf(digit) {
        if (!this.sipPlugin || !this.inCall) return;

        this.sipPlugin.dtmf({
            dtmf: { tones: digit }
        });
    }

    /**
     * Toggle mute
     */
    toggleMute() {
        if (!this.sipPlugin) return false;

        const muted = this.sipPlugin.isAudioMuted();
        if (muted) {
            this.sipPlugin.unmuteAudio();
        } else {
            this.sipPlugin.muteAudio();
        }
        return !muted;
    }

    /**
     * Check if muted
     */
    isMuted() {
        return this.sipPlugin ? this.sipPlugin.isAudioMuted() : false;
    }

    /**
     * Get the audio sender for track replacement
     * Used for injecting translated audio back to remote peer
     */
    getAudioSender() {
        if (!this.sipPlugin) return null;

        // Access the underlying peer connection via Janus plugin
        const pc = this.sipPlugin.webrtcStuff?.pc;
        if (!pc) return null;

        // Find the audio sender
        const senders = pc.getSenders();
        for (const sender of senders) {
            if (sender.track && sender.track.kind === 'audio') {
                return sender;
            }
        }
        return null;
    }

    /**
     * Replace audio track with injected audio
     * @param {MediaStreamTrack} newTrack - The new audio track
     */
    async replaceAudioTrack(newTrack) {
        const sender = this.getAudioSender();
        if (!sender) {
            this.onLog('No audio sender found for track replacement', 'error');
            return false;
        }

        try {
            await sender.replaceTrack(newTrack);
            this.onLog('Audio track replaced successfully', 'event');
            return true;
        } catch (err) {
            this.onLog(`Failed to replace audio track: ${err.message}`, 'error');
            return false;
        }
    }

    /**
     * Handle SIP messages from Janus
     */
    _handleSipMessage(msg, jsep) {
        // Janus SIP plugin sends events in two formats:
        // 1. { sip: "event", result: { event: "registered", ... } }
        // 2. { sip: "registered", ... }
        const event = (msg.sip === 'event') ? msg.result?.event : msg.sip;
        const data = msg.result || msg;

        this.onLog(`SIP event: ${event}`, 'sip');

        switch (event) {
            case 'registering':
                this.onLog('Registering...', 'sip');
                break;

            case 'registered':
                this.registered = true;
                this.onLog(`Registered as ${data.username || this.sipExtension}`, 'sip');
                this.onSipRegistered(data.username || this.sipExtension);
                break;

            case 'registration_failed':
                this.registered = false;
                this.onLog(`Registration failed: ${data.cause}`, 'error');
                this.onError(new Error(`Registration failed: ${data.cause}`));
                break;

            case 'unregistered':
                this.registered = false;
                this.onLog('Unregistered', 'sip');
                this.onSipUnregistered();
                break;

            case 'calling':
                this.onLog('Calling...', 'sip');
                break;

            case 'ringing':
                this.onLog('Ringing...', 'sip');
                break;

            case 'progress':
                this.onLog(`Progress: ${data.code}`, 'sip');
                if (jsep) {
                    this.sipPlugin.handleRemoteJsep({ jsep: jsep });
                }
                break;

            case 'proceeding':
                this.onLog(`Proceeding: ${data.code}`, 'sip');
                break;

            case 'incomingcall':
                this.onLog(`Incoming call from ${data.username}`, 'sip');
                this.currentJsep = jsep;
                this.onIncomingCall({
                    caller: data.username,
                    displayName: data.displayname || data.username
                });
                break;

            case 'accepting':
                this.onLog('Accepting call...', 'sip');
                break;

            case 'accepted':
                this.inCall = true;
                this.currentJsep = null;
                this.onLog('Call accepted', 'sip');

                if (jsep) {
                    this.sipPlugin.handleRemoteJsep({ jsep: jsep });
                }

                this.onCallAccepted({
                    peer: data.username
                });
                break;

            case 'hangup':
                this.inCall = false;
                this.currentJsep = null;
                this.onLog(`Call ended: ${data.code} ${data.reason}`, 'sip');
                this.onCallEnded({
                    code: data.code,
                    reason: data.reason
                });
                break;

            case 'declining':
                this.currentJsep = null;
                this.onLog('Declining call...', 'sip');
                break;

            case 'missed_call':
                this.currentJsep = null;
                this.onLog(`Missed call from ${data.caller}`, 'sip');
                break;

            case 'info':
                this.onLog(`Info: ${data.type}`, 'sip');
                break;

            case 'notify':
                this.onLog(`Notify: ${data.notify}`, 'sip');
                break;

            case 'transfer':
                this.onLog(`Transfer: ${data.refer_to}`, 'sip');
                break;

            case 'hold':
                this.onLog('Call on hold', 'sip');
                if (jsep) {
                    this.sipPlugin.handleRemoteJsep({ jsep: jsep });
                }
                break;

            case 'unhold':
                this.onLog('Call resumed', 'sip');
                if (jsep) {
                    this.sipPlugin.handleRemoteJsep({ jsep: jsep });
                }
                break;

            default:
                if (data.error || msg.error) {
                    const error = data.error || msg.error;
                    this.onLog(`Error: ${error}`, 'error');
                    this.onError(new Error(error));
                } else {
                    this.onLog(`Unknown event: ${JSON.stringify(msg)}`, 'event');
                }
        }
    }

    /**
     * Disconnect from Janus
     */
    disconnect() {
        if (this.inCall) {
            this.hangup();
        }
        if (this.registered) {
            this.unregister();
        }
        if (this.janus) {
            this.janus.destroy();
            this.janus = null;
        }
        this.sipPlugin = null;
        this.registered = false;
        this.inCall = false;
        this._trackEventHooked = false;
    }

    /**
     * Hook into PeerConnection track events to capture remote streams properly
     */
    _hookTrackEvents() {
        const pc = this.sipPlugin?.webrtcStuff?.pc;
        if (!pc) {
            this.onLog('[TRACK HOOK] No PeerConnection available yet', 'event');
            return;
        }

        this._trackEventHooked = true;
        this.onLog('[TRACK HOOK] Hooking into PeerConnection track events', 'event');

        pc.addEventListener('track', (event) => {
            this.onLog(`[TRACK EVENT] Received: kind=${event.track.kind}, id=${event.track.id}, label=${event.track.label}, streams=${event.streams.length}`, 'event');

            if (event.track.kind === 'audio') {
                // Log track state
                this.onLog(`[TRACK EVENT] Track state: enabled=${event.track.enabled}, muted=${event.track.muted}, readyState=${event.track.readyState}`, 'event');

                if (event.streams.length > 0) {
                    // Use the stream from the track event - this is the actual audio stream from WebRTC
                    const stream = event.streams[0];
                    this.onLog(`[TRACK EVENT] Stream: id=${stream.id}, active=${stream.active}, tracks=${stream.getTracks().length}`, 'event');

                    if (!this.remoteStream) {
                        this.remoteStream = stream;
                        this.onLog('[TRACK EVENT] Calling onRemoteStream with track event stream', 'event');
                        this.onRemoteStream(stream);
                    } else {
                        this.onLog('[TRACK EVENT] remoteStream already set, skipping', 'event');
                    }
                } else {
                    this.onLog('[TRACK EVENT] WARNING: No streams associated with track!', 'error');
                }
            }
        });
    }

    /**
     * Fallback method to get streams from peer connection
     * Called when WebRTC is up but onremotestream/onlocalstream weren't triggered
     */
    _checkAndGetStreams() {
        if (!this.sipPlugin) return;

        const pc = this.sipPlugin.webrtcStuff?.pc;
        if (!pc) {
            this.onLog('No peer connection found for stream fallback', 'error');
            return;
        }

        // Check for remote stream via receivers
        if (!this.remoteStream) {
            try {
                const receivers = pc.getReceivers();
                const audioReceiver = receivers.find(r => r.track && r.track.kind === 'audio');

                if (audioReceiver && audioReceiver.track) {
                    // Create a MediaStream from the remote audio track
                    const remoteStream = new MediaStream([audioReceiver.track]);
                    this.remoteStream = remoteStream;
                    this.onLog('Remote stream obtained via fallback (receiver)', 'event');
                    this.onRemoteStream(remoteStream);
                } else {
                    // Try getRemoteStreams (deprecated but might work)
                    const remoteStreams = pc.getRemoteStreams?.();
                    if (remoteStreams && remoteStreams.length > 0) {
                        this.remoteStream = remoteStreams[0];
                        this.onLog('Remote stream obtained via fallback (getRemoteStreams)', 'event');
                        this.onRemoteStream(remoteStreams[0]);
                    } else {
                        this.onLog('No remote stream available yet', 'event');
                    }
                }
            } catch (err) {
                this.onLog(`Error getting remote stream: ${err.message}`, 'error');
            }
        }

        // Check for local stream via senders
        if (!this.localStream) {
            try {
                const senders = pc.getSenders();
                const audioSender = senders.find(s => s.track && s.track.kind === 'audio');

                if (audioSender && audioSender.track) {
                    // Create a MediaStream from the local audio track
                    const localStream = new MediaStream([audioSender.track]);
                    this.localStream = localStream;
                    this.onLog('Local stream obtained via fallback (sender)', 'event');
                } else {
                    // Try getLocalStreams (deprecated but might work)
                    const localStreams = pc.getLocalStreams?.();
                    if (localStreams && localStreams.length > 0) {
                        this.localStream = localStreams[0];
                        this.onLog('Local stream obtained via fallback (getLocalStreams)', 'event');
                    }
                }
            } catch (err) {
                this.onLog(`Error getting local stream: ${err.message}`, 'error');
            }
        }
    }
}

// Export for use in other modules
if (typeof module !== 'undefined' && module.exports) {
    module.exports = { SIPHandler };
}
