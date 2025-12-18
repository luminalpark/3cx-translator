"""
OpenAI Realtime API Translation Server - Streaming Mode
Real-time speech-to-speech translation using GPT-4o Realtime with Server VAD

Architecture:
  Client (WebSocket) -> This Server -> OpenAI Realtime API (WebSocket)
                    <- Streaming audio <-

Features:
- Real-time bidirectional audio streaming
- Server VAD for automatic speech detection
- Low latency streaming translation
- 5 language pairs: DE, ES, EN, FR <-> IT

Usage:
    OPENAI_API_KEY=your_key python openai_server.py
"""

import asyncio
import base64
import json
import logging
import os
import sys
import time
import uuid
from dataclasses import dataclass, field
from typing import Optional, Dict, Any
from contextlib import asynccontextmanager

import numpy as np
from scipy import signal
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from dotenv import load_dotenv
import uvicorn
import websockets

# Load environment variables
load_dotenv()

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[logging.StreamHandler(sys.stdout)]
)
logger = logging.getLogger("openai_server")


# =============================================================================
# Configuration
# =============================================================================

OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview"


# Translation modes
TRANSLATION_MODE_VAD = "vad"           # Server VAD - translate when silence detected
TRANSLATION_MODE_PERIODIC = "periodic"  # Periodic - translate every N seconds while speaking


@dataclass
class ServerConfig:
    """Server configuration from environment variables"""
    openai_api_key: str
    openai_voice: str = "alloy"
    server_host: str = "0.0.0.0"
    server_port: int = 8001
    default_source_lang: str = "auto"
    default_target_lang: str = "it"
    # VAD settings (for VAD mode)
    vad_threshold: float = 0.5
    vad_prefix_padding_ms: int = 300
    vad_silence_duration_ms: int = 500
    # Periodic settings (for periodic mode)
    periodic_interval_ms: int = 3000  # Translate every 3 seconds
    # Default translation mode
    default_translation_mode: str = TRANSLATION_MODE_VAD

    @classmethod
    def from_env(cls) -> "ServerConfig":
        api_key = os.environ.get("OPENAI_API_KEY")
        if not api_key:
            raise ValueError("OPENAI_API_KEY environment variable is required")

        return cls(
            openai_api_key=api_key,
            openai_voice=os.environ.get("OPENAI_VOICE", "alloy"),
            server_host=os.environ.get("SERVER_HOST", "0.0.0.0"),
            server_port=int(os.environ.get("SERVER_PORT", "8001")),
            default_source_lang=os.environ.get("DEFAULT_SOURCE_LANG", "auto"),
            default_target_lang=os.environ.get("DEFAULT_TARGET_LANG", "it"),
            vad_threshold=float(os.environ.get("VAD_THRESHOLD", "0.5")),
            vad_prefix_padding_ms=int(os.environ.get("VAD_PREFIX_PADDING_MS", "300")),
            vad_silence_duration_ms=int(os.environ.get("VAD_SILENCE_DURATION_MS", "500")),
            periodic_interval_ms=int(os.environ.get("PERIODIC_INTERVAL_MS", "3000")),
            default_translation_mode=os.environ.get("TRANSLATION_MODE", TRANSLATION_MODE_VAD),
        )


# =============================================================================
# Language Mappings
# =============================================================================

LANGUAGE_NAMES = {
    "de": "German", "deu": "German",
    "es": "Spanish", "spa": "Spanish",
    "en": "English", "eng": "English",
    "fr": "French", "fra": "French",
    "it": "Italian", "ita": "Italian",
}

SUPPORTED_LANGUAGES = ["de", "es", "en", "fr", "it"]


# =============================================================================
# Audio Processing
# =============================================================================

def resample_audio(audio: np.ndarray, orig_sr: int, target_sr: int) -> np.ndarray:
    """Resample audio to target sample rate"""
    if orig_sr == target_sr:
        return audio
    num_samples = int(len(audio) * target_sr / orig_sr)
    resampled = signal.resample(audio, num_samples)
    return resampled.astype(np.int16)


def pcm16_to_base64(audio_bytes: bytes) -> str:
    """Convert PCM16 bytes to base64 string"""
    return base64.b64encode(audio_bytes).decode('utf-8')


def base64_to_pcm16(audio_base64: str) -> bytes:
    """Convert base64 string to PCM16 bytes"""
    return base64.b64decode(audio_base64)


# =============================================================================
# Client Session
# =============================================================================

@dataclass
class ClientSession:
    """Per-client session state"""
    session_id: str
    source_lang: str = "auto"
    target_lang: str = "it"
    openai_ws: Any = None
    client_ws: Any = None
    last_detected_lang: Optional[str] = None
    created_at: float = field(default_factory=time.time)
    is_speaking: bool = False
    current_response_id: Optional[str] = None
    source_text: str = ""
    translated_text: str = ""
    openai_listener_task: Any = None
    periodic_task: Any = None  # Periodic commit task (for periodic mode)
    active: bool = True
    # Streaming mode: True = send audio chunks immediately, False = buffer and send at end
    stream_audio: bool = False
    # Translation mode: "vad" or "periodic"
    translation_mode: str = TRANSLATION_MODE_VAD
    # Periodic mode state
    last_commit_time: float = 0.0
    audio_received_since_commit: bool = False
    is_responding: bool = False  # True while OpenAI is generating response
    # Track conversation items for cleanup in periodic mode
    pending_item_ids: list = field(default_factory=list)


# =============================================================================
# OpenAI Realtime Server - Streaming Mode
# =============================================================================

class OpenAITranslationServer:
    """Main server class managing client sessions and OpenAI connections"""

    def __init__(self, config: ServerConfig):
        self.config = config
        self.sessions: Dict[str, ClientSession] = {}

    def _build_system_instruction(self, source_lang: str, target_lang: str, translation_mode: str = TRANSLATION_MODE_VAD) -> str:
        """Build translation system instruction based on mode"""
        target_name = LANGUAGE_NAMES.get(target_lang, "Italian")

        if source_lang == "auto":
            source_instruction = "Listen to the audio and detect the language (German, Spanish, English, French, or Italian)"
        else:
            source_name = LANGUAGE_NAMES.get(source_lang, source_lang)
            source_instruction = f"The input audio is in {source_name}"

        if translation_mode == TRANSLATION_MODE_PERIODIC:
            # Periodic mode: translate fragments literally without completion
            return f"""You are a real-time SIMULTANEOUS INTERPRETER providing live translation.

{source_instruction}. Translate to {target_name} IN REAL-TIME.

CRITICAL - SIMULTANEOUS INTERPRETATION RULES:
1. Translate ONLY the exact words you hear - NEVER add, complete, or guess what comes next
2. If a sentence is incomplete, translate just the fragment you heard
3. Do NOT finish incomplete sentences or add missing words
4. Do NOT interpret intent or add context - translate literally
5. If you hear "I would like to..." translate exactly "Vorrei..." and STOP
6. Output ONLY the translated fragment in {target_name}
7. NO explanations, NO questions, NO completions
8. If unclear, remain SILENT - never guess

You are translating a LIVE speaker. The audio will arrive in chunks. Translate each chunk literally without anticipating what comes next."""

        else:
            # VAD mode: standard translation of complete utterances
            return f"""You are a real-time speech translator.

{source_instruction}. Translate the speech to {target_name}.

CRITICAL RULES:
1. Output ONLY the translated speech in {target_name}
2. Do NOT add any explanations, comments, or questions
3. Do NOT repeat the original text
4. Preserve the tone, emotion, and pacing
5. If the input is already in {target_name}, repeat it exactly
6. If you cannot understand, remain silent

Your response must be ONLY the spoken translation in {target_name}."""

    async def connect_to_openai(self, session: ClientSession) -> bool:
        """Establish WebSocket connection to OpenAI Realtime API"""
        try:
            headers = {
                "Authorization": f"Bearer {self.config.openai_api_key}",
                "OpenAI-Beta": "realtime=v1"
            }

            logger.info(f"[{session.session_id}] Connecting to OpenAI Realtime API...")

            session.openai_ws = await websockets.connect(
                OPENAI_REALTIME_URL,
                additional_headers=headers,
                ping_interval=20,
                ping_timeout=20
            )

            # Wait for session.created event
            msg = await asyncio.wait_for(session.openai_ws.recv(), timeout=10.0)
            event = json.loads(msg)
            if event.get("type") == "session.created":
                logger.info(f"[{session.session_id}] Connected to OpenAI (session created)")
            else:
                logger.warning(f"[{session.session_id}] Unexpected first event: {event.get('type')}")

            return True

        except Exception as e:
            logger.error(f"[{session.session_id}] Failed to connect to OpenAI: {e}")
            return False

    async def configure_openai_session(self, session: ClientSession, wait_for_response: bool = True):
        """Configure the OpenAI session based on translation mode (VAD or periodic)"""
        if not session.openai_ws:
            return

        instructions = self._build_system_instruction(
            session.source_lang,
            session.target_lang,
            session.translation_mode
        )

        # Build turn_detection based on mode
        if session.translation_mode == TRANSLATION_MODE_VAD:
            # VAD mode: OpenAI detects speech boundaries automatically
            turn_detection = {
                "type": "server_vad",
                "threshold": self.config.vad_threshold,
                "prefix_padding_ms": self.config.vad_prefix_padding_ms,
                "silence_duration_ms": self.config.vad_silence_duration_ms
            }
        else:
            # Periodic mode: disable VAD, we control commits manually
            turn_detection = None

        config_message = {
            "type": "session.update",
            "session": {
                "modalities": ["text", "audio"],
                "instructions": instructions,
                "voice": self.config.openai_voice,
                "input_audio_format": "pcm16",
                "output_audio_format": "pcm16",
                "input_audio_transcription": {
                    "model": "whisper-1"
                },
                "turn_detection": turn_detection
            }
        }

        await session.openai_ws.send(json.dumps(config_message))

        # Only wait for response if listener is not running yet
        if wait_for_response:
            try:
                msg = await asyncio.wait_for(session.openai_ws.recv(), timeout=5.0)
                event = json.loads(msg)
                if event.get("type") == "session.updated":
                    mode_str = "VAD" if session.translation_mode == TRANSLATION_MODE_VAD else f"Periodic ({self.config.periodic_interval_ms}ms)"
                    logger.info(f"[{session.session_id}] OpenAI session configured [{mode_str}]: {session.source_lang} -> {session.target_lang}")
                else:
                    logger.warning(f"[{session.session_id}] Expected session.updated, got: {event.get('type')}")
            except asyncio.TimeoutError:
                logger.warning(f"[{session.session_id}] Timeout waiting for session.updated")
        else:
            # Listener will handle session.updated
            logger.info(f"[{session.session_id}] Session update sent: {session.source_lang} -> {session.target_lang}")

    async def periodic_commit_task(self, session: ClientSession):
        """Background task that periodically commits audio buffer and requests translation"""
        interval_sec = self.config.periodic_interval_ms / 1000.0
        logger.info(f"[{session.session_id}] Periodic commit task started (interval: {self.config.periodic_interval_ms}ms)")

        session.last_commit_time = time.time()

        try:
            while session.active and session.translation_mode == TRANSLATION_MODE_PERIODIC:
                await asyncio.sleep(0.1)  # Check every 100ms

                # Skip if currently responding or no audio since last commit
                if session.is_responding:
                    continue

                if not session.audio_received_since_commit:
                    continue

                # Check if interval has passed
                elapsed = time.time() - session.last_commit_time
                if elapsed < interval_sec:
                    continue

                # Time to commit and translate
                if session.openai_ws:
                    try:
                        logger.info(f"[{session.session_id}] Periodic commit triggered")

                        # Commit audio buffer
                        await session.openai_ws.send(json.dumps({
                            "type": "input_audio_buffer.commit"
                        }))

                        # Request response
                        await session.openai_ws.send(json.dumps({
                            "type": "response.create"
                        }))

                        session.last_commit_time = time.time()
                        session.audio_received_since_commit = False

                    except Exception as e:
                        logger.error(f"[{session.session_id}] Periodic commit error: {e}")

        except asyncio.CancelledError:
            logger.info(f"[{session.session_id}] Periodic commit task cancelled")
        except Exception as e:
            logger.error(f"[{session.session_id}] Periodic commit task error: {e}")
        finally:
            logger.info(f"[{session.session_id}] Periodic commit task stopped")

    async def start_periodic_task(self, session: ClientSession):
        """Start the periodic commit task"""
        if session.periodic_task:
            session.periodic_task.cancel()
            try:
                await session.periodic_task
            except asyncio.CancelledError:
                pass

        session.periodic_task = asyncio.create_task(self.periodic_commit_task(session))

    async def stop_periodic_task(self, session: ClientSession):
        """Stop the periodic commit task"""
        if session.periodic_task:
            session.periodic_task.cancel()
            try:
                await session.periodic_task
            except asyncio.CancelledError:
                pass
            session.periodic_task = None

    async def close_openai_session(self, session: ClientSession):
        """Close OpenAI WebSocket connection"""
        session.active = False

        # Stop periodic task
        await self.stop_periodic_task(session)

        if session.openai_listener_task:
            session.openai_listener_task.cancel()
            try:
                await session.openai_listener_task
            except asyncio.CancelledError:
                pass
        if session.openai_ws:
            try:
                await session.openai_ws.close()
                logger.info(f"[{session.session_id}] OpenAI connection closed")
            except:
                pass
            session.openai_ws = None

    async def send_audio_chunk(self, session: ClientSession, audio_data: bytes):
        """Send audio chunk to OpenAI (streaming mode)"""
        if not session.openai_ws or not session.active:
            return

        # Track audio received (for periodic mode)
        session.audio_received_since_commit = True

        # Track total audio sent
        if not hasattr(session, 'total_audio_sent'):
            session.total_audio_sent = 0
        session.total_audio_sent += len(audio_data)

        # Log every ~5 seconds of audio (16kHz * 2 bytes * 5 sec = 160000)
        if session.total_audio_sent % 160000 < len(audio_data):
            logger.info(f"[{session.session_id}] Audio sent: {session.total_audio_sent // 1000}KB total")

        # Resample from 16kHz to 24kHz for OpenAI
        audio_16k = np.frombuffer(audio_data, dtype=np.int16)
        audio_24k = resample_audio(audio_16k, 16000, 24000)
        audio_base64 = pcm16_to_base64(audio_24k.tobytes())

        # Send audio chunk to OpenAI
        try:
            await session.openai_ws.send(json.dumps({
                "type": "input_audio_buffer.append",
                "audio": audio_base64
            }))
        except Exception as e:
            logger.error(f"[{session.session_id}] Error sending audio: {e}")

    async def openai_event_listener(self, session: ClientSession):
        """Background task to listen for OpenAI events and forward to client"""
        logger.info(f"[{session.session_id}] Starting OpenAI event listener")

        audio_buffer = bytearray()

        try:
            while session.active and session.openai_ws:
                try:
                    msg = await asyncio.wait_for(session.openai_ws.recv(), timeout=30.0)
                    event = json.loads(msg)
                    event_type = event.get("type", "")

                    # Speech started - notify client
                    if event_type == "input_audio_buffer.speech_started":
                        session.is_speaking = True
                        logger.info(f"[{session.session_id}] Speech started")
                        if session.client_ws:
                            await session.client_ws.send_json({
                                "type": "speech_started"
                            })

                    # Speech stopped - VAD detected end of speech
                    elif event_type == "input_audio_buffer.speech_stopped":
                        session.is_speaking = False
                        logger.info(f"[{session.session_id}] Speech stopped")
                        if session.client_ws:
                            await session.client_ws.send_json({
                                "type": "speech_stopped"
                            })

                    # Audio buffer committed (after speech_stopped)
                    elif event_type == "input_audio_buffer.committed":
                        logger.info(f"[{session.session_id}] Audio committed")

                    # Conversation item created
                    elif event_type == "conversation.item.created":
                        item = event.get("item", {})
                        item_id = item.get("id")
                        item_type = item.get("type", "unknown")
                        item_role = item.get("role", "unknown")
                        logger.info(f"[{session.session_id}] Item created: type={item_type}, role={item_role}, id={item_id}")

                        # Track item IDs for cleanup in periodic mode
                        if session.translation_mode == TRANSLATION_MODE_PERIODIC and item_id:
                            session.pending_item_ids.append(item_id)

                    # Response started
                    elif event_type == "response.created":
                        session.current_response_id = event.get("response", {}).get("id")
                        session.source_text = ""
                        session.translated_text = ""
                        session.is_responding = True  # Mark as responding (for periodic mode)
                        audio_buffer.clear()
                        logger.info(f"[{session.session_id}] Response started")
                        if session.client_ws:
                            await session.client_ws.send_json({
                                "type": "translation_started",
                                "mute_input": True  # Tell client to pause audio capture
                            })

                    # Audio delta - stream to client or buffer
                    elif event_type == "response.audio.delta":
                        audio_base64 = event.get("delta", "")
                        if audio_base64:
                            audio_bytes = base64_to_pcm16(audio_base64)

                            # Resample from 24kHz to 16kHz for client
                            audio_24k = np.frombuffer(audio_bytes, dtype=np.int16)
                            audio_16k = resample_audio(audio_24k, 24000, 16000)
                            resampled_bytes = audio_16k.tobytes()

                            # Buffer audio
                            audio_buffer.extend(resampled_bytes)

                            # Stream audio to client immediately if streaming mode
                            if session.stream_audio and session.client_ws:
                                await session.client_ws.send_bytes(resampled_bytes)

                    # Output transcript delta
                    elif event_type == "response.audio_transcript.delta":
                        delta = event.get("delta", "")
                        session.translated_text += delta

                    # Input transcript completed
                    elif event_type == "conversation.item.input_audio_transcription.completed":
                        session.source_text = event.get("transcript", "")
                        logger.info(f"[{session.session_id}] Source: '{session.source_text}'")

                    # Response done
                    elif event_type == "response.done":
                        session.is_responding = False  # Mark as not responding (for periodic mode)
                        response_obj = event.get("response", {})
                        status = response_obj.get("status", "unknown")

                        logger.info(f"[{session.session_id}] Response done: status={status}, audio={len(audio_buffer)} bytes")
                        logger.info(f"[{session.session_id}] '{session.source_text}' -> '{session.translated_text}'")

                        if session.client_ws:
                            # Send translation result (compatible with old protocol)
                            await session.client_ws.send_json({
                                "type": "translation",
                                "source_text": session.source_text,
                                "translated_text": session.translated_text,
                                "detected_language": session.last_detected_lang,
                                "audio_sample_rate": 16000,
                                "pipeline": "openai_realtime_vad",
                                "status": status,
                                "audio_duration_ms": len(audio_buffer) // 32  # 16kHz * 2 bytes = 32 bytes/ms
                            })

                            # Send complete audio buffer (if not streaming or always for compatibility)
                            if not session.stream_audio and len(audio_buffer) > 0:
                                await session.client_ws.send_bytes(bytes(audio_buffer))
                                logger.info(f"[{session.session_id}] Sent {len(audio_buffer)} bytes audio to client")

                            # Tell client to resume audio capture after playback
                            # Add delay estimate based on audio length
                            await session.client_ws.send_json({
                                "type": "unmute_input",
                                "delay_ms": len(audio_buffer) // 32 + 500  # Audio duration + 500ms buffer
                            })

                        # Handle failed responses
                        if status == "failed":
                            status_details = response_obj.get("status_details", {})
                            error = status_details.get("error", {})
                            logger.error(f"[{session.session_id}] Response failed: {error.get('message', 'Unknown')}")

                        # In periodic mode, delete ALL conversation items to prevent context accumulation
                        # This gives us a "fresh start" for each chunk instead of continuing a conversation
                        if session.translation_mode == TRANSLATION_MODE_PERIODIC and session.openai_ws:
                            try:
                                # Delete all tracked conversation items (both user input and assistant output)
                                items_to_delete = session.pending_item_ids.copy()
                                for item_id in items_to_delete:
                                    await session.openai_ws.send(json.dumps({
                                        "type": "conversation.item.delete",
                                        "item_id": item_id
                                    }))

                                # Clear the tracking list
                                session.pending_item_ids.clear()

                                # Also clear any remaining audio in the buffer
                                await session.openai_ws.send(json.dumps({
                                    "type": "input_audio_buffer.clear"
                                }))

                                logger.info(f"[{session.session_id}] Conversation cleared for periodic mode ({len(items_to_delete)} items deleted)")
                            except Exception as e:
                                logger.warning(f"[{session.session_id}] Failed to clear conversation: {e}")

                    # Error handling
                    elif event_type == "error":
                        error_info = event.get("error", {})
                        error_msg = error_info.get("message", "Unknown error")
                        error_code = error_info.get("code", "unknown")
                        logger.error(f"[{session.session_id}] OpenAI error [{error_code}]: {error_msg}")

                        if session.client_ws:
                            await session.client_ws.send_json({
                                "type": "error",
                                "message": error_msg,
                                "code": error_code
                            })

                    # Session updated (after reconfigure)
                    elif event_type == "session.updated":
                        logger.info(f"[{session.session_id}] Session updated")

                    # Item deleted (periodic mode cleanup)
                    elif event_type == "conversation.item.deleted":
                        pass  # Silently acknowledge deletion

                    # Audio buffer cleared
                    elif event_type == "input_audio_buffer.cleared":
                        logger.debug(f"[{session.session_id}] Audio buffer cleared")

                    # Rate limits
                    elif event_type == "rate_limits.updated":
                        pass  # Ignore rate limit updates

                except asyncio.TimeoutError:
                    # Send ping to keep connection alive
                    continue
                except websockets.exceptions.ConnectionClosed:
                    logger.warning(f"[{session.session_id}] OpenAI connection closed")
                    break
                except Exception as e:
                    logger.error(f"[{session.session_id}] Error processing OpenAI event: {e}")
                    continue

        except asyncio.CancelledError:
            logger.info(f"[{session.session_id}] OpenAI listener cancelled")
        except Exception as e:
            logger.error(f"[{session.session_id}] OpenAI listener error: {e}")
        finally:
            logger.info(f"[{session.session_id}] OpenAI listener stopped")


# =============================================================================
# FastAPI Application
# =============================================================================

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup
    global server
    try:
        config = ServerConfig.from_env()
        server = OpenAITranslationServer(config)
        logger.info(f"Server initialized")
        logger.info(f"Voice: {config.openai_voice}")
        logger.info(f"VAD: threshold={config.vad_threshold}, silence={config.vad_silence_duration_ms}ms")
        logger.info(f"Listening on {config.server_host}:{config.server_port}")
    except Exception as e:
        logger.error(f"Failed to initialize: {e}")
        raise
    yield
    # Shutdown
    logger.info("Server shutting down")


app = FastAPI(
    title="OpenAI Realtime Translation Server (Streaming)",
    description="Real-time speech-to-speech translation via OpenAI GPT-4o with Server VAD",
    version="2.0.0",
    lifespan=lifespan
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

server: Optional[OpenAITranslationServer] = None


@app.get("/health")
async def health_check():
    return {
        "status": "healthy",
        "server": "openai_realtime",
        "model": "gpt-4o-realtime-preview",
        "voice": server.config.openai_voice if server else None,
        "languages": SUPPORTED_LANGUAGES,
        "default_translation_mode": server.config.default_translation_mode if server else TRANSLATION_MODE_VAD,
        "translation_modes": [TRANSLATION_MODE_VAD, TRANSLATION_MODE_PERIODIC],
        "periodic_interval_ms": server.config.periodic_interval_ms if server else 3000,
        "vad_settings": {
            "threshold": server.config.vad_threshold,
            "silence_duration_ms": server.config.vad_silence_duration_ms
        } if server else None
    }


@app.get("/languages")
async def get_languages():
    return {
        "supported": SUPPORTED_LANGUAGES,
        "names": LANGUAGE_NAMES,
        "default_source": "auto",
        "default_target": server.config.default_target_lang if server else "it",
    }


@app.websocket("/ws/translate")
async def websocket_translate(websocket: WebSocket):
    await websocket.accept()

    session_id = str(uuid.uuid4())[:8]
    session = ClientSession(
        session_id=session_id,
        source_lang=server.config.default_source_lang,
        target_lang=server.config.default_target_lang,
        client_ws=websocket,
        translation_mode=server.config.default_translation_mode
    )
    server.sessions[session_id] = session

    logger.info(f"[{session_id}] Client connected (mode: {session.translation_mode})")

    # Send welcome
    await websocket.send_json({
        "type": "connected",
        "server": "openai_realtime",
        "model": "gpt-4o-realtime-preview",
        "voice": server.config.openai_voice,
        "auto_detect": session.source_lang == "auto",
        "languages": SUPPORTED_LANGUAGES,
        "translation_mode": session.translation_mode,
        "translation_modes": [TRANSLATION_MODE_VAD, TRANSLATION_MODE_PERIODIC],
        "periodic_interval_ms": server.config.periodic_interval_ms,
        "stream_audio": session.stream_audio
    })

    try:
        # Connect to OpenAI
        if not await server.connect_to_openai(session):
            await websocket.send_json({
                "type": "error",
                "message": "Failed to connect to OpenAI",
                "code": "OPENAI_CONNECTION_ERROR"
            })
            return

        # Configure session based on translation mode
        await server.configure_openai_session(session)

        # Start OpenAI event listener
        session.openai_listener_task = asyncio.create_task(
            server.openai_event_listener(session)
        )

        # Start periodic task if in periodic mode
        if session.translation_mode == TRANSLATION_MODE_PERIODIC:
            await server.start_periodic_task(session)

        # Main loop - receive from client
        last_audio_time = time.time()
        while session.active:
            try:
                message = await asyncio.wait_for(websocket.receive(), timeout=5.0)
            except asyncio.TimeoutError:
                # No message received for 5 seconds
                idle_time = time.time() - last_audio_time
                if idle_time > 10:
                    logger.warning(f"[{session_id}] No audio for {idle_time:.0f}s")
                continue

            if "bytes" in message:
                last_audio_time = time.time()
                # Forward audio to OpenAI immediately (streaming mode)
                await server.send_audio_chunk(session, message["bytes"])

            elif "text" in message:
                try:
                    data = json.loads(message["text"])
                    await handle_json_message(websocket, session, data)
                except json.JSONDecodeError as e:
                    logger.warning(f"[{session_id}] Invalid JSON: {e}")

    except WebSocketDisconnect:
        logger.info(f"[{session_id}] Client disconnected")
    except Exception as e:
        logger.error(f"[{session_id}] Error: {e}")
        try:
            await websocket.send_json({
                "type": "error",
                "message": str(e),
                "code": "SERVER_ERROR"
            })
        except:
            pass
    finally:
        await server.close_openai_session(session)
        if session_id in server.sessions:
            del server.sessions[session_id]
        logger.info(f"[{session_id}] Cleaned up")


async def handle_json_message(
    websocket: WebSocket,
    session: ClientSession,
    data: dict
):
    msg_type = data.get("type", "")

    if msg_type in ("configure", "config"):
        new_source = data.get("source_lang", session.source_lang)
        new_target = data.get("target_lang", session.target_lang)

        if new_source != "auto" and new_source not in SUPPORTED_LANGUAGES:
            await websocket.send_json({
                "type": "error",
                "message": f"Unsupported source language: {new_source}",
                "code": "INVALID_LANGUAGE"
            })
            return

        if new_target not in SUPPORTED_LANGUAGES:
            await websocket.send_json({
                "type": "error",
                "message": f"Unsupported target language: {new_target}",
                "code": "INVALID_LANGUAGE"
            })
            return

        session.source_lang = new_source
        session.target_lang = new_target

        # Reconfigure OpenAI session (listener is running, don't wait)
        await server.configure_openai_session(session, wait_for_response=False)

        logger.info(f"[{session.session_id}] Configured: {new_source} -> {new_target}")

        await websocket.send_json({
            "type": "configured",
            "source_lang": session.source_lang,
            "target_lang": session.target_lang,
            "auto_detect": session.source_lang == "auto",
            "streaming": True
        })

    elif msg_type == "cancel":
        # Cancel current response
        if session.openai_ws and session.current_response_id:
            try:
                await session.openai_ws.send(json.dumps({
                    "type": "response.cancel"
                }))
                logger.info(f"[{session.session_id}] Response cancelled")
            except:
                pass

    elif msg_type == "clear":
        # Clear audio buffer
        if session.openai_ws:
            try:
                await session.openai_ws.send(json.dumps({
                    "type": "input_audio_buffer.clear"
                }))
                logger.info(f"[{session.session_id}] Audio buffer cleared")
            except:
                pass
        await websocket.send_json({"type": "cleared"})

    elif msg_type == "translate":
        # Compatibility mode: client sends batch audio then "translate" command
        # In VAD mode, we need to manually commit the buffer and trigger response
        if session.openai_ws:
            try:
                logger.info(f"[{session.session_id}] Manual translate trigger (batch mode)")
                # Commit the audio buffer
                await session.openai_ws.send(json.dumps({
                    "type": "input_audio_buffer.commit"
                }))
                # Request response
                await session.openai_ws.send(json.dumps({
                    "type": "response.create"
                }))
            except Exception as e:
                logger.error(f"[{session.session_id}] Error triggering translation: {e}")
                await websocket.send_json({
                    "type": "error",
                    "message": f"Translation failed: {str(e)}",
                    "code": "TRANSLATION_ERROR"
                })

    elif msg_type == "set_streaming":
        # Enable/disable audio streaming mode
        session.stream_audio = data.get("enabled", False)
        logger.info(f"[{session.session_id}] Audio streaming mode: {session.stream_audio}")
        await websocket.send_json({
            "type": "streaming_mode",
            "enabled": session.stream_audio
        })

    elif msg_type == "set_translation_mode":
        # Switch translation mode (vad or periodic)
        new_mode = data.get("mode", TRANSLATION_MODE_VAD)
        if new_mode not in [TRANSLATION_MODE_VAD, TRANSLATION_MODE_PERIODIC]:
            await websocket.send_json({
                "type": "error",
                "message": f"Invalid translation mode: {new_mode}. Use 'vad' or 'periodic'",
                "code": "INVALID_MODE"
            })
            return

        old_mode = session.translation_mode
        session.translation_mode = new_mode

        # Stop/start periodic task as needed
        if old_mode == TRANSLATION_MODE_PERIODIC and new_mode == TRANSLATION_MODE_VAD:
            await server.stop_periodic_task(session)
            session.pending_item_ids.clear()  # Clear tracked items when leaving periodic mode
        elif old_mode == TRANSLATION_MODE_VAD and new_mode == TRANSLATION_MODE_PERIODIC:
            await server.start_periodic_task(session)

        # Reconfigure OpenAI session for new mode
        await server.configure_openai_session(session, wait_for_response=False)

        logger.info(f"[{session.session_id}] Translation mode changed: {old_mode} -> {new_mode}")
        await websocket.send_json({
            "type": "translation_mode_changed",
            "mode": session.translation_mode,
            "periodic_interval_ms": server.config.periodic_interval_ms if new_mode == TRANSLATION_MODE_PERIODIC else None
        })

    elif msg_type == "ping":
        await websocket.send_json({
            "type": "pong",
            "is_speaking": session.is_speaking,
            "is_responding": session.is_responding,
            "last_detected_language": session.last_detected_lang,
            "translation_mode": session.translation_mode,
            "stream_audio": session.stream_audio
        })


# =============================================================================
# Main
# =============================================================================

if __name__ == "__main__":
    try:
        config = ServerConfig.from_env()
    except ValueError as e:
        logger.error(f"Configuration error: {e}")
        sys.exit(1)

    logger.info("=" * 60)
    logger.info("OpenAI Realtime Translation Server")
    logger.info("=" * 60)
    logger.info(f"Model: gpt-4o-realtime-preview")
    logger.info(f"Voice: {config.openai_voice}")
    logger.info(f"Languages: {', '.join(SUPPORTED_LANGUAGES)}")
    logger.info(f"Default: {config.default_source_lang} -> {config.default_target_lang}")
    logger.info(f"Translation mode: {config.default_translation_mode}")
    logger.info(f"VAD: threshold={config.vad_threshold}, silence={config.vad_silence_duration_ms}ms")
    logger.info(f"Periodic: interval={config.periodic_interval_ms}ms")
    logger.info("=" * 60)

    uvicorn.run(
        "openai_server:app",
        host=config.server_host,
        port=config.server_port,
        reload=False,
        log_level="info"
    )
