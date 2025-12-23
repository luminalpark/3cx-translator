"""
Gemini Live API Translation Server (Vertex AI)
Real-time speech-to-speech translation using Google Gemini on Vertex AI

Proxy architecture:
  Client (WebSocket) -> This Server -> Vertex AI Gemini Live API (WebSocket)

Features:
- Real-time bidirectional audio streaming
- Automatic language detection
- 5 language pairs: DE, ES, EN, FR <-> IT
- Low latency via regional endpoint (europe-west4)

Usage:
    GOOGLE_CLOUD_PROJECT=your-project-id python gemini_server.py

Requires:
- Service Account with roles/aiplatform.user
- GOOGLE_APPLICATION_CREDENTIALS pointing to credentials.json
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
from typing import Optional, Dict, Any, List

import numpy as np
from scipy import signal
from fastapi import FastAPI, WebSocket, WebSocketDisconnect, Query, HTTPException, status
from fastapi.middleware.cors import CORSMiddleware
from starlette.websockets import WebSocketClose
from dotenv import load_dotenv
import uvicorn

# Load environment variables
load_dotenv()

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout)
    ]
)
logger = logging.getLogger("gemini_server")


# =============================================================================
# Configuration
# =============================================================================

@dataclass
class ServerConfig:
    """Server configuration from environment variables"""
    # Vertex AI configuration
    google_cloud_project: str
    google_cloud_location: str = "europe-west4"
    # Gemini model and voice
    # NOTE: gemini-live-2.5-flash-native-audio is required for Manual VAD with native audio I/O
    gemini_model: str = "gemini-live-2.5-flash-native-audio"
    gemini_voice: str = "Kore"
    server_host: str = "0.0.0.0"
    server_port: int = 8001
    default_source_lang: str = "auto"
    default_target_lang: str = "it"
    max_audio_duration_sec: int = 30
    input_sample_rate: int = 16000
    output_sample_rate: int = 16000
    # Client API key for authentication (optional, but recommended for production)
    client_api_key: str = ""

    @classmethod
    def from_env(cls) -> "ServerConfig":
        project = os.environ.get("GOOGLE_CLOUD_PROJECT")
        if not project:
            raise ValueError("GOOGLE_CLOUD_PROJECT environment variable is required")

        return cls(
            google_cloud_project=project,
            google_cloud_location=os.environ.get("GOOGLE_CLOUD_LOCATION", "europe-west4"),
            gemini_model=os.environ.get("GEMINI_MODEL", "gemini-live-2.5-flash-native-audio"),
            gemini_voice=os.environ.get("GEMINI_VOICE", "Kore"),
            server_host=os.environ.get("SERVER_HOST", "0.0.0.0"),
            server_port=int(os.environ.get("SERVER_PORT", "8001")),
            default_source_lang=os.environ.get("DEFAULT_SOURCE_LANG", "auto"),
            default_target_lang=os.environ.get("DEFAULT_TARGET_LANG", "it"),
            client_api_key=os.environ.get("CLIENT_API_KEY", ""),
        )


# =============================================================================
# Language Mappings
# =============================================================================

# Gemini native audio model only accepts simple 2-letter language codes
# NOT BCP-47 codes like "es-ES" - those cause "Unsupported language code" errors
GEMINI_LANG_CODES = {
    "de": "de", "german": "de", "deu": "de", "de-de": "de", "de-at": "de", "de-ch": "de",
    "es": "es", "spanish": "es", "spa": "es", "es-es": "es", "es-mx": "es", "es-ar": "es",
    "en": "en", "english": "en", "eng": "en", "en-us": "en", "en-gb": "en", "en-au": "en",
    "fr": "fr", "french": "fr", "fra": "fr", "fr-fr": "fr", "fr-ca": "fr", "fr-be": "fr",
    "it": "it", "italian": "it", "ita": "it", "it-it": "it", "it-ch": "it",
}

LANGUAGE_NAMES = {
    "de": "German", "deu": "German",
    "es": "Spanish", "spa": "Spanish",
    "en": "English", "eng": "English",
    "fr": "French", "fra": "French",
    "it": "Italian", "ita": "Italian",
}

SUPPORTED_LANGUAGES = ["de", "es", "en", "fr", "it"]


def normalize_language_code(code: str) -> str:
    """
    Normalize language codes to simple 2-letter format.
    Gemini native audio model doesn't accept BCP-47 codes like 'es-ES'.

    Examples:
        'es-ES' -> 'es'
        'de-DE' -> 'de'
        'en-US' -> 'en'
        'auto' -> 'auto'
    """
    if not code or code == "auto":
        return code

    # First check if it's already in our mapping
    code_lower = code.lower()
    if code_lower in GEMINI_LANG_CODES:
        return GEMINI_LANG_CODES[code_lower]

    # Otherwise, take the primary subtag (before any dash or underscore)
    return code.split('-')[0].split('_')[0].lower()


# =============================================================================
# Client Session
# =============================================================================

@dataclass
class ClientSession:
    """Per-client session state"""
    session_id: str
    source_lang: str = "auto"
    target_lang: str = "it"
    audio_buffer: bytearray = field(default_factory=bytearray)
    last_detected_lang: Optional[str] = None
    created_at: float = field(default_factory=time.time)
    # Streaming mode fields
    streaming_enabled: bool = False
    streaming_ready: bool = False  # True when Gemini session is connected
    gemini_session: Any = None  # Persistent Gemini Live session
    receive_task: Optional[asyncio.Task] = None
    session_task: Optional[asyncio.Task] = None  # Main session task
    audio_queue: Optional[asyncio.Queue] = None  # Queue for audio chunks
    websocket: Any = None  # Reference to client WebSocket
    is_closing: bool = False
    # Full duplex support: buffer audio during model turns
    in_model_turn: bool = False  # True when Gemini is generating response
    pending_audio: List[bytes] = field(default_factory=list)  # Audio received during model turn
    # Audio chunk counters for diagnostics
    chunks_received: int = 0
    chunks_sent: int = 0
    last_chunk_time: float = 0.0
    # Manual VAD turn state
    turn_active: bool = False  # True after ActivityStart, False after ActivityEnd
    waiting_for_turn_complete: bool = False  # True after ActivityEnd until turn_complete


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
# Gemini Translation Server
# =============================================================================

class GeminiTranslationServer:
    """Main server class managing client sessions and Gemini connections"""

    def __init__(self, config: ServerConfig):
        self.config = config
        self.sessions: Dict[str, ClientSession] = {}
        self._genai_client = None

    def _get_genai_client(self):
        """Lazy initialization of Google GenAI client for Vertex AI"""
        if self._genai_client is None:
            from google import genai
            self._genai_client = genai.Client(
                vertexai=True,
                project=self.config.google_cloud_project,
                location=self.config.google_cloud_location
            )
        return self._genai_client

    def _build_system_instruction(self, source_lang: str, target_lang: str) -> str:
        """Build translation system instruction for Gemini"""
        source_name = LANGUAGE_NAMES.get(source_lang, "the detected language")
        target_name = LANGUAGE_NAMES.get(target_lang, "Italian")

        if source_lang == "auto":
            source_instruction = "Detect the input language (German, Spanish, English, or French)"
        else:
            source_instruction = f"The input is in {source_name}"

        return f"""You are a real-time speech translator.

TASK: {source_instruction} and translate it to {target_name}.

CRITICAL RULES:
1. Output ONLY the translated speech in {target_name} - absolutely no explanations, comments, or original text
2. Preserve the tone, emotion, emphasis, and pacing of the original speech
3. Maintain natural speech flow - translate complete thoughts, not word-by-word
4. If you cannot understand the audio clearly, say nothing rather than guessing
5. Never repeat the original text, never add commentary, never ask questions

You are translating TO: {target_name}

Remember: Your output should ONLY be the translated speech in {target_name}."""

    async def process_audio(
        self,
        session: ClientSession,
        audio_data: bytes
    ) -> Dict[str, Any]:
        """Send audio to Gemini and get translated audio back"""
        from google.genai import types

        client = self._get_genai_client()

        # Build system instruction
        system_instruction = self._build_system_instruction(
            session.source_lang,
            session.target_lang
        )

        # Configure Gemini Live session
        # NOTE: Native audio models automatically choose the output language
        # based on the system instruction - language_code is not supported
        config = types.LiveConnectConfig(
            response_modalities=["AUDIO"],
            speech_config=types.SpeechConfig(
                voice_config=types.VoiceConfig(
                    prebuilt_voice_config=types.PrebuiltVoiceConfig(
                        voice_name=self.config.gemini_voice
                    )
                )
            ),
            system_instruction=system_instruction,
            input_audio_transcription=types.AudioTranscriptionConfig(),
            output_audio_transcription=types.AudioTranscriptionConfig()
        )

        start_time = time.time()

        logger.info(f"[{session.session_id}] Sending {len(audio_data)} bytes to Gemini ({session.source_lang} -> {session.target_lang})")

        # Collect response
        translated_audio = bytearray()
        source_text = ""
        translated_text = ""
        detected_lang = session.last_detected_lang

        try:
            # Use async context manager for Gemini Live session
            logger.info(f"[{session.session_id}] Opening Gemini Live session...")
            async with client.aio.live.connect(
                model=self.config.gemini_model,
                config=config
            ) as gemini_session:
                logger.info(f"[{session.session_id}] Gemini session opened, sending audio...")

                # Send audio to Gemini (PCM16 @ 16kHz, little-endian)
                # MIME type must be exactly "audio/pcm" for native audio model
                await gemini_session.send(
                    input=types.LiveClientRealtimeInput(
                        media_chunks=[
                            types.Blob(
                                mime_type="audio/pcm",
                                data=audio_data
                            )
                        ]
                    ),
                    end_of_turn=True
                )
                logger.info(f"[{session.session_id}] Audio sent, waiting for response...")

                # Receive response
                response_count = 0
                async for response in gemini_session.receive():
                    response_count += 1
                    logger.info(f"[{session.session_id}] Response #{response_count}: {type(response).__name__}")
                    # Handle server content
                    if hasattr(response, 'server_content') and response.server_content:
                        content = response.server_content

                        # Extract audio from model turn
                        if hasattr(content, 'model_turn') and content.model_turn:
                            for part in content.model_turn.parts:
                                if hasattr(part, 'inline_data') and part.inline_data:
                                    data = part.inline_data.data
                                    mime = getattr(part.inline_data, 'mime_type', 'unknown')
                                    # Handle both bytes and base64 string
                                    if isinstance(data, bytes):
                                        logger.debug(f"[{session.session_id}] Audio chunk: {len(data)} bytes, mime: {mime}")
                                        translated_audio.extend(data)
                                    elif isinstance(data, str):
                                        # Fix base64 padding if needed
                                        padding = 4 - len(data) % 4
                                        if padding != 4:
                                            data += '=' * padding
                                        try:
                                            audio_bytes = base64.b64decode(data)
                                            translated_audio.extend(audio_bytes)
                                        except Exception as e:
                                            logger.warning(f"Failed to decode audio: {e}")

                        # Extract input transcription (source text)
                        if hasattr(content, 'input_transcription') and content.input_transcription:
                            if hasattr(content.input_transcription, 'text'):
                                source_text = content.input_transcription.text
                            else:
                                source_text = str(content.input_transcription)

                        # Extract output transcription (translated text)
                        if hasattr(content, 'output_transcription') and content.output_transcription:
                            if hasattr(content.output_transcription, 'text'):
                                translated_text = content.output_transcription.text
                            else:
                                translated_text = str(content.output_transcription)

                        # Check if turn is complete
                        if getattr(content, 'turn_complete', False):
                            break

        except Exception as e:
            logger.error(f"[{session.session_id}] Gemini error: {e}")
            raise

        elapsed_ms = (time.time() - start_time) * 1000

        # Resample from 24kHz to 16kHz if we got audio
        output_audio = bytes()
        if len(translated_audio) > 0:
            # Gemini outputs 24kHz audio
            audio_24k = np.frombuffer(bytes(translated_audio), dtype=np.int16)
            audio_16k = resample_audio(audio_24k, 24000, 16000)
            output_audio = audio_16k.tobytes()

        logger.info(f"[{session.session_id}] Translation complete in {elapsed_ms:.0f}ms")
        if source_text or translated_text:
            logger.info(f"[{session.session_id}] '{source_text}' -> '{translated_text}'")

        return {
            "source_text": source_text,
            "translated_text": translated_text,
            "detected_language": detected_lang,
            "audio": output_audio,
            "sample_rate": 16000,
            "latency_ms": elapsed_ms,
            "pipeline": "gemini_live"
        }

    async def create_streaming_session(self, session: ClientSession) -> bool:
        """Create a persistent Gemini Live session for streaming"""
        try:
            # Create audio queue for sending chunks
            session.audio_queue = asyncio.Queue()
            session.streaming_ready = False
            session.in_model_turn = False

            # Start the main session task that runs within context manager
            session.session_task = asyncio.create_task(
                self._run_streaming_session(session)
            )

            # Wait for session to be ready (with timeout)
            for _ in range(20):  # 2 second timeout
                await asyncio.sleep(0.1)
                if session.streaming_ready:
                    logger.info(f"[{session.session_id}] Streaming session ready ({session.source_lang} -> {session.target_lang})")
                    return True
                if session.is_closing:
                    return False

            logger.error(f"[{session.session_id}] Timeout waiting for Gemini session")
            return False

        except Exception as e:
            logger.error(f"[{session.session_id}] Failed to create streaming session: {e}")
            return False

    async def _run_streaming_session(self, session: ClientSession):
        """Run the Gemini session within proper context manager"""
        from google.genai import types

        client = self._get_genai_client()

        # Build system instruction
        system_instruction = self._build_system_instruction(
            session.source_lang,
            session.target_lang
        )

        # Configure Gemini Live session
        # NOTE: Native audio models automatically choose the output language
        # based on the system instruction - language_code is not supported
        #
        # MANUAL VAD: Client controls turn boundaries explicitly.
        # - Client sends activity_start before speech
        # - Client sends activity_end after speech (triggers translation)
        # - For continuous audio like file playback, client sends periodic
        #   activity_end signals to trigger translations at regular intervals
        # MANUAL VAD Configuration for gemini-live-2.5-flash-native-audio
        # Reference: https://cloud.google.com/vertex-ai/generative-ai/docs/live-api
        #
        # With Manual VAD (automatic_activity_detection.disabled = True):
        # - Client MUST send ActivityStart before sending audio
        # - Client sends audio chunks with mime_type="audio/pcm" (PCM16@16kHz)
        # - Client sends ActivityEnd when speech is done (triggers response)
        # - Server responds with audio at 24kHz PCM16
        #
        # This enables SIMULTANEOUS translation (output while input continues)
        config = types.LiveConnectConfig(
            # Native audio model (gemini-live-2.5-flash-native-audio) only supports ONE modality
            # Cannot use ["AUDIO", "TEXT"] - causes error:
            # "At most one response modality can be specified in the setup request"
            response_modalities=["AUDIO"],
            speech_config=types.SpeechConfig(
                voice_config=types.VoiceConfig(
                    prebuilt_voice_config=types.PrebuiltVoiceConfig(
                        voice_name=self.config.gemini_voice
                    )
                )
            ),
            system_instruction=system_instruction,
            input_audio_transcription=types.AudioTranscriptionConfig(),
            output_audio_transcription=types.AudioTranscriptionConfig(),
            # Manual VAD - client controls turn boundaries with ActivityStart/ActivityEnd
            realtime_input_config=types.RealtimeInputConfig(
                automatic_activity_detection=types.AutomaticActivityDetection(
                    disabled=True  # MANUAL VAD - client sends ActivityStart/ActivityEnd
                )
            )
        )

        try:
            async with client.aio.live.connect(
                model=self.config.gemini_model,
                config=config
            ) as gemini_session:
                session.gemini_session = gemini_session
                session.streaming_ready = True
                logger.info(f"[{session.session_id}] Gemini Live session connected (model: {self.config.gemini_model})")

                # Run send and receive loops concurrently
                async with asyncio.TaskGroup() as tg:
                    tg.create_task(self._send_loop(session, gemini_session))
                    tg.create_task(self._receive_loop(session, gemini_session))

        except* asyncio.CancelledError:
            logger.info(f"[{session.session_id}] Session tasks cancelled")
        except* Exception as eg:
            for e in eg.exceptions:
                logger.error(f"[{session.session_id}] Session error: {e}")
        finally:
            session.gemini_session = None
            logger.info(f"[{session.session_id}] Gemini session closed")

    async def _send_loop(self, session: ClientSession, gemini_session):
        """Send audio chunks to Gemini with Manual VAD.

        Manual VAD sequence (client-controlled):
        1. Client sends activity_start message -> server sends ActivityStart to Gemini
        2. Client sends audio chunks -> server forwards to Gemini
        3. Client sends activity_end message -> server sends ActivityEnd to Gemini
        4. Wait for turn_complete from Gemini
        5. Repeat

        NOTE: ActivityStart/ActivityEnd are controlled ONLY by client messages.
        This loop just forwards audio chunks when turn_active is True.
        Audio received during WAIT_COMPLETE is dropped (client shouldn't send any).
        """
        from google.genai import types

        logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
        logger.info(f"[{session.session_id}] 🚀 SEND LOOP STARTED (Manual VAD - client-controlled)")
        logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")

        turn_audio_bytes = 0
        turn_start_time = None

        try:
            while not session.is_closing:
                try:
                    audio_data = await asyncio.wait_for(
                        session.audio_queue.get(),
                        timeout=0.5
                    )

                    if audio_data is None:
                        logger.info(f"[{session.session_id}] 🛑 Poison pill received, exiting send loop")
                        break

                    # Drop audio if not in active turn or waiting for turn_complete
                    # With the new client, this shouldn't happen, but be defensive
                    if session.waiting_for_turn_complete:
                        logger.warning(f"[{session.session_id}] ⚠️  DROPPING audio (waiting_for_turn_complete=True)")
                        continue
                    if not session.turn_active:
                        logger.warning(f"[{session.session_id}] ⚠️  DROPPING audio (turn_active=False)")
                        continue

                    # Track turn timing
                    if turn_start_time is None:
                        turn_start_time = time.time()

                    # Send audio chunk (PCM16 @ 16kHz, little-endian)
                    audio_array = np.frombuffer(audio_data, dtype=np.int16)
                    rms = np.sqrt(np.mean(audio_array.astype(np.float32) ** 2))
                    turn_audio_bytes += len(audio_data)

                    # Log every 25 chunks for better visibility
                    if session.chunks_sent % 25 == 0:
                        elapsed = time.time() - turn_start_time if turn_start_time else 0
                        logger.info(f"[{session.session_id}] 🔊 AUDIO #{session.chunks_sent}: "
                                   f"{len(audio_data)}B, rms={rms:.0f}, turn_total={turn_audio_bytes}B, elapsed={elapsed:.2f}s")

                    await gemini_session.send_realtime_input(
                        audio=types.Blob(data=audio_data, mime_type="audio/pcm")
                    )
                    session.chunks_sent += 1

                except asyncio.TimeoutError:
                    pass

                if session.is_closing:
                    break

        except asyncio.CancelledError:
            logger.info(f"[{session.session_id}] Send loop cancelled")
        except Exception as e:
            logger.error(f"[{session.session_id}] ❌ SEND LOOP ERROR: {e}")

        logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
        logger.info(f"[{session.session_id}] 🏁 SEND LOOP ENDED (total_sent={session.chunks_sent}, turn_bytes={turn_audio_bytes})")
        logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")

    async def send_audio_chunk(self, session: ClientSession, audio_data: bytes):
        """Queue audio chunk for sending to Gemini.

        With Manual VAD, the client controls when to send audio:
        - Only send when turn_active is True (after activity_start)
        - Stop sending after activity_end (WAIT_COMPLETE state)
        - Audio sent in wrong state will be dropped by _send_loop
        """
        if session.audio_queue and not session.is_closing:
            try:
                session.audio_queue.put_nowait(audio_data)
                session.chunks_received += 1
                session.last_chunk_time = time.time()
            except asyncio.QueueFull:
                logger.warning(f"[{session.session_id}] Audio queue full, dropping chunk")

    async def send_end_of_turn(self, session: ClientSession):
        """Signal end of audio stream to Gemini.

        NOTE: With Manual VAD enabled, use send_activity_end() instead.
        This method is kept for backward compatibility but should not be used
        when Manual VAD is active.
        """
        if session.gemini_session and session.streaming_ready and not session.is_closing:
            try:
                logger.info(f"[{session.session_id}] Sending audio_stream_end signal to Gemini")
                await session.gemini_session.send_realtime_input(audio_stream_end=True)
            except Exception as e:
                logger.warning(f"[{session.session_id}] Failed to send audio_stream_end: {e}")

    async def send_activity_start(self, session: ClientSession):
        """Signal start of user speech (Manual VAD).

        Call this before sending audio chunks to indicate the user started speaking.
        Required when automatic_activity_detection is disabled.
        Sets session.turn_active to prevent duplicate ActivityStart in _send_loop.
        """
        from google.genai import types

        if session.gemini_session and session.streaming_ready and not session.is_closing:
            try:
                # Set flag BEFORE sending to prevent race with _send_loop
                session.turn_active = True
                session.chunks_sent = 0  # Reset for new turn
                logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
                logger.info(f"[{session.session_id}] 🎙️  >>> ACTIVITY_START >>> (Manual VAD)")
                logger.info(f"[{session.session_id}]     turn_active={session.turn_active}")
                logger.info(f"[{session.session_id}]     waiting_for_turn_complete={session.waiting_for_turn_complete}")
                logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
                await session.gemini_session.send_realtime_input(
                    activity_start=types.ActivityStart()
                )
                logger.info(f"[{session.session_id}] ✅ ActivityStart SENT to Gemini")
            except Exception as e:
                logger.error(f"[{session.session_id}] ❌ FAILED to send ActivityStart: {e}")
                session.turn_active = False  # Reset on error
        else:
            logger.warning(f"[{session.session_id}] ⚠️  Cannot send ActivityStart: "
                          f"gemini_session={session.gemini_session is not None}, "
                          f"streaming_ready={session.streaming_ready}, "
                          f"is_closing={session.is_closing}")

    async def send_activity_end(self, session: ClientSession):
        """Signal end of user speech (Manual VAD).

        Call this after sending audio chunks to indicate the user stopped speaking.
        This triggers Gemini to generate the translation response.
        Required when automatic_activity_detection is disabled.

        CRITICAL: After this, no more audio chunks should be sent until turn_complete!
        """
        from google.genai import types

        if session.gemini_session and session.streaming_ready and not session.is_closing:
            try:
                # Mark turn as closed - send_loop will drop audio until turn_complete
                session.turn_active = False
                session.waiting_for_turn_complete = True

                logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
                logger.info(f"[{session.session_id}] 🛑 >>> ACTIVITY_END >>> (Manual VAD)")
                logger.info(f"[{session.session_id}]     chunks_sent={session.chunks_sent}")
                logger.info(f"[{session.session_id}]     turn_active={session.turn_active}")
                logger.info(f"[{session.session_id}]     waiting_for_turn_complete={session.waiting_for_turn_complete}")
                logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
                await session.gemini_session.send_realtime_input(
                    activity_end=types.ActivityEnd()
                )
                logger.info(f"[{session.session_id}] ✅ ActivityEnd SENT - waiting for Gemini response...")
            except Exception as e:
                logger.error(f"[{session.session_id}] ❌ FAILED to send ActivityEnd: {e}")
                # Reset state on error
                session.turn_active = False
                session.waiting_for_turn_complete = False
        else:
            logger.warning(f"[{session.session_id}] ⚠️  Cannot send ActivityEnd: "
                          f"gemini_session={session.gemini_session is not None}, "
                          f"streaming_ready={session.streaming_ready}, "
                          f"is_closing={session.is_closing}")

    async def _receive_loop(self, session: ClientSession, gemini_session):
        """Receive responses from Gemini and forward to client"""
        from google.genai import types

        logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
        logger.info(f"[{session.session_id}] 👂 RECEIVE LOOP STARTED")
        logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")

        try:
            # Loop to handle multiple turns - receive() completes after each turn
            turn_count = 0
            while not session.is_closing:
                try:
                    async for response in gemini_session.receive():
                        if session.is_closing:
                            logger.info(f"[{session.session_id}] Receive loop: session is closing")
                            return

                        if not session.websocket:
                            continue

                        try:
                            if hasattr(response, 'server_content') and response.server_content:
                                content = response.server_content

                                # Input transcription (source text)
                                if hasattr(content, 'input_transcription') and content.input_transcription:
                                    text = ""
                                    if hasattr(content.input_transcription, 'text'):
                                        text = content.input_transcription.text
                                    else:
                                        text = str(content.input_transcription)

                                    if text:
                                        await session.websocket.send_json({
                                            "type": "source_text",
                                            "text": text
                                        })
                                        logger.info(f"[{session.session_id}] Source: {text}")

                                # Content from model turn (audio and/or text)
                                if hasattr(content, 'model_turn') and content.model_turn:
                                    # Mark that we're in a model turn (Gemini is speaking)
                                    if not session.in_model_turn:
                                        session.in_model_turn = True
                                        logger.info(f"[{session.session_id}] ────────────────────────────────────────")
                                        logger.info(f"[{session.session_id}] 🤖 MODEL TURN STARTED")
                                        logger.info(f"[{session.session_id}] ────────────────────────────────────────")
                                        await session.websocket.send_json({
                                            "type": "model_turn_started"
                                        })

                                    parts = content.model_turn.parts if hasattr(content.model_turn, 'parts') else []
                                    for part in parts:
                                        # Handle TEXT response (for debugging)
                                        if hasattr(part, 'text') and part.text:
                                            logger.info(f"[{session.session_id}] 📝 MODEL TEXT: {part.text}")
                                            await session.websocket.send_json({
                                                "type": "model_text",
                                                "text": part.text
                                            })

                                        # Handle AUDIO response
                                        if hasattr(part, 'inline_data') and part.inline_data:
                                            data = part.inline_data.data
                                            mime = getattr(part.inline_data, 'mime_type', 'unknown')
                                            data_len = len(data) if data else 0

                                            # CRITICAL: Log MIME type prominently - this is key for debugging!
                                            logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
                                            logger.info(f"[{session.session_id}] 🔈 MODEL AUDIO RECEIVED:")
                                            logger.info(f"[{session.session_id}]     MIME TYPE: {mime}")
                                            logger.info(f"[{session.session_id}]     DATA TYPE: {type(data).__name__}")
                                            logger.info(f"[{session.session_id}]     LENGTH: {data_len} bytes")
                                            logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")

                                            if isinstance(data, bytes):
                                                # Resample from 24kHz to 16kHz
                                                audio_24k = np.frombuffer(data, dtype=np.int16)
                                                audio_16k = resample_audio(audio_24k, 24000, 16000)
                                                logger.info(f"[{session.session_id}] ✅ Resampled {len(audio_24k)} samples (24kHz) -> {len(audio_16k)} samples (16kHz)")
                                                await session.websocket.send_bytes(audio_16k.tobytes())
                                            elif isinstance(data, str):
                                                # Handle base64 encoded audio
                                                try:
                                                    padding = 4 - len(data) % 4
                                                    if padding != 4:
                                                        data += '=' * padding
                                                    audio_bytes = base64.b64decode(data)
                                                    audio_24k = np.frombuffer(audio_bytes, dtype=np.int16)
                                                    audio_16k = resample_audio(audio_24k, 24000, 16000)
                                                    logger.info(f"[{session.session_id}] ✅ Decoded base64 and resampled {len(audio_24k)} -> {len(audio_16k)} samples")
                                                    await session.websocket.send_bytes(audio_16k.tobytes())
                                                except Exception as e:
                                                    logger.error(f"[{session.session_id}] ❌ Failed to decode audio: {e}")
                                            else:
                                                logger.warning(f"[{session.session_id}] ⚠️  UNEXPECTED DATA TYPE: {type(data)}")

                                # Output transcription (translated text)
                                if hasattr(content, 'output_transcription') and content.output_transcription:
                                    text = ""
                                    if hasattr(content.output_transcription, 'text'):
                                        text = content.output_transcription.text
                                    else:
                                        text = str(content.output_transcription)

                                    if text:
                                        await session.websocket.send_json({
                                            "type": "translated_text",
                                            "text": text
                                        })
                                        logger.info(f"[{session.session_id}] Translated: {text}")

                                # Turn complete - break to restart receive() for next turn
                                if getattr(content, 'turn_complete', False):
                                    turn_count += 1
                                    session.in_model_turn = False
                                    # CRITICAL: Reset turn state so send_loop can start new turn
                                    session.waiting_for_turn_complete = False
                                    session.turn_active = False  # Will trigger ActivityStart on next audio
                                    logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
                                    logger.info(f"[{session.session_id}] ✅ TURN {turn_count} COMPLETE")
                                    logger.info(f"[{session.session_id}]     chunks_received={session.chunks_received}")
                                    logger.info(f"[{session.session_id}]     chunks_sent={session.chunks_sent}")
                                    logger.info(f"[{session.session_id}]     turn_active={session.turn_active}")
                                    logger.info(f"[{session.session_id}]     waiting_for_turn_complete={session.waiting_for_turn_complete}")
                                    logger.info(f"[{session.session_id}]     READY FOR NEW TURN")
                                    logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
                                    await session.websocket.send_json({
                                        "type": "turn_complete"
                                    })
                                    break  # Exit inner loop to call receive() again

                        except Exception as e:
                            logger.warning(f"[{session.session_id}] Error forwarding response: {e}")

                except Exception as e:
                    error_str = str(e).lower()
                    # Stop on connection close or terminal errors
                    if "close" in error_str or "1011" in error_str or "1006" in error_str:
                        logger.info(f"[{session.session_id}] Session closed by server: {e}")
                        break
                    if "unavailable" in error_str or "deadline" in error_str:
                        logger.warning(f"[{session.session_id}] Gemini service error, stopping: {e}")
                        break
                    logger.warning(f"[{session.session_id}] Receive error: {e}")
                    # Delay before retrying on transient errors
                    await asyncio.sleep(0.5)

        except asyncio.CancelledError:
            logger.info(f"[{session.session_id}] Receive loop cancelled")
        except Exception as e:
            logger.error(f"[{session.session_id}] ❌ RECEIVE LOOP ERROR: {e}")

        logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")
        logger.info(f"[{session.session_id}] 🏁 RECEIVE LOOP ENDED (total_turns={turn_count})")
        logger.info(f"[{session.session_id}] ════════════════════════════════════════════════════════════")

    async def close_streaming_session(self, session: ClientSession):
        """Close the Gemini streaming session"""
        session.is_closing = True
        session.streaming_ready = False
        session.in_model_turn = False

        # Send poison pill to queue
        if session.audio_queue:
            try:
                session.audio_queue.put_nowait(None)
            except:
                pass

        # Cancel session task
        if session.session_task:
            session.session_task.cancel()
            try:
                await session.session_task
            except asyncio.CancelledError:
                pass
            session.session_task = None

        session.audio_queue = None
        logger.info(f"[{session.session_id}] Streaming session closed")


# =============================================================================
# FastAPI Application
# =============================================================================

app = FastAPI(
    title="Gemini Live Translation Server",
    description="Real-time speech-to-speech translation via Google Gemini",
    version="1.0.0"
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Global server instance
server: Optional[GeminiTranslationServer] = None


@app.on_event("startup")
async def startup():
    """Initialize server on startup"""
    global server
    try:
        config = ServerConfig.from_env()
        server = GeminiTranslationServer(config)
        logger.info(f"Server initialized with model: {config.gemini_model}")
        logger.info(f"Listening on {config.server_host}:{config.server_port}")
    except Exception as e:
        logger.error(f"Failed to initialize server: {e}")
        raise


@app.get("/health")
async def health_check():
    """Health check endpoint"""
    return {
        "status": "healthy",
        "server": "gemini_live",
        "model": server.config.gemini_model if server else None,
        "voice": server.config.gemini_voice if server else None,
        "languages": SUPPORTED_LANGUAGES,
        "default_target": server.config.default_target_lang if server else "it",
    }


@app.get("/languages")
async def get_languages():
    """Get supported languages"""
    return {
        "supported": SUPPORTED_LANGUAGES,
        "names": LANGUAGE_NAMES,
        "default_source": "auto",
        "default_target": server.config.default_target_lang if server else "it",
    }


def verify_api_key(api_key: str) -> bool:
    """Verify client API key if configured"""
    if not server or not server.config.client_api_key:
        return True  # No API key configured, allow all
    return api_key == server.config.client_api_key


@app.websocket("/ws/translate")
async def websocket_translate(
    websocket: WebSocket,
    api_key: str = Query(default="", alias="key")
):
    """Main WebSocket endpoint for real-time translation

    Query params:
        key: API key for authentication (required if CLIENT_API_KEY is set)
    """
    # Check API key before accepting connection
    if not verify_api_key(api_key):
        logger.warning(f"Rejected connection: invalid API key from {websocket.client}")
        await websocket.close(code=4001, reason="Invalid API key")
        return

    await websocket.accept()

    session_id = str(uuid.uuid4())[:8]
    session = ClientSession(
        session_id=session_id,
        source_lang=server.config.default_source_lang,
        target_lang=server.config.default_target_lang
    )
    session.websocket = websocket  # Store reference for streaming
    server.sessions[session_id] = session

    logger.info(f"[{session_id}] Client connected")

    # Send welcome message
    await websocket.send_json({
        "type": "connected",
        "server": "gemini",
        "model": server.config.gemini_model,
        "voice": server.config.gemini_voice,
        "auto_detect": session.source_lang == "auto",
        "languages": SUPPORTED_LANGUAGES,
        "streaming_supported": True
    })

    try:
        while True:
            message = await websocket.receive()

            if "bytes" in message:
                # Binary audio data
                audio_bytes = message["bytes"]
                if session.streaming_enabled and session.streaming_ready:
                    # Streaming mode: forward audio directly to Gemini
                    if session.chunks_received % 50 == 0:  # Log every 50 chunks
                        logger.info(f"[{session_id}] Audio chunk #{session.chunks_received}, {len(audio_bytes)} bytes")
                    await server.send_audio_chunk(session, audio_bytes)
                else:
                    # Buffer mode: add to buffer for later processing
                    session.audio_buffer.extend(audio_bytes)

            elif "text" in message:
                # JSON control message
                try:
                    data = json.loads(message["text"])
                    await handle_json_message(websocket, session, data)
                except json.JSONDecodeError as e:
                    logger.warning(f"[{session_id}] Invalid JSON: {e}")
                    await websocket.send_json({
                        "type": "error",
                        "message": "Invalid JSON message",
                        "code": "INVALID_JSON"
                    })

    except WebSocketDisconnect:
        logger.info(f"[{session_id}] Client disconnected")
    except Exception as e:
        logger.error(f"[{session_id}] WebSocket error: {e}")
        try:
            await websocket.send_json({
                "type": "error",
                "message": str(e),
                "code": "SERVER_ERROR"
            })
        except:
            pass
    finally:
        # Cleanup streaming session if active
        if session.streaming_enabled:
            await server.close_streaming_session(session)

        # Cleanup
        if session_id in server.sessions:
            del server.sessions[session_id]
        logger.info(f"[{session_id}] Session cleaned up")


async def handle_json_message(
    websocket: WebSocket,
    session: ClientSession,
    data: dict
):
    """Handle JSON control messages from client"""
    msg_type = data.get("type", "")

    if msg_type == "configure" or msg_type == "config":
        # Update language configuration - normalize codes first (es-ES -> es)
        new_source = normalize_language_code(data.get("source_lang", session.source_lang))
        new_target = normalize_language_code(data.get("target_lang", session.target_lang))

        # Validate languages
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

        # Update session
        session.source_lang = new_source
        session.target_lang = new_target

        logger.info(f"[{session.session_id}] Configured: {new_source} -> {new_target}")

        await websocket.send_json({
            "type": "configured",
            "source_lang": session.source_lang,
            "target_lang": session.target_lang,
            "auto_detect": session.source_lang == "auto",
            "gemini_session": True
        })

        # If streaming is already enabled, recreate session with new config
        if session.streaming_enabled and session.streaming_ready:
            await server.close_streaming_session(session)
            session.is_closing = False
            success = await server.create_streaming_session(session)
            if success:
                await websocket.send_json({
                    "type": "streaming_session_ready",
                    "source_lang": session.source_lang,
                    "target_lang": session.target_lang
                })

    elif msg_type == "set_streaming":
        # Enable or disable streaming mode
        enabled = data.get("enabled", False)

        if enabled and not session.streaming_enabled:
            # Enable streaming mode
            session.streaming_enabled = True
            success = await server.create_streaming_session(session)

            if success:
                await websocket.send_json({
                    "type": "streaming_enabled",
                    "enabled": True,
                    "message": "Real-time streaming active"
                })
            else:
                session.streaming_enabled = False
                await websocket.send_json({
                    "type": "error",
                    "message": "Failed to enable streaming mode",
                    "code": "STREAMING_ERROR"
                })

        elif not enabled and session.streaming_enabled:
            # Disable streaming mode
            await server.close_streaming_session(session)
            session.streaming_enabled = False
            session.is_closing = False

            await websocket.send_json({
                "type": "streaming_enabled",
                "enabled": False,
                "message": "Streaming disabled, buffer mode active"
            })

        else:
            await websocket.send_json({
                "type": "streaming_enabled",
                "enabled": session.streaming_enabled
            })

    elif msg_type == "translate":
        # Process buffered audio
        if len(session.audio_buffer) < 1600:  # Minimum ~100ms at 16kHz
            await websocket.send_json({
                "type": "error",
                "message": "Audio too short (minimum 100ms required)",
                "code": "AUDIO_TOO_SHORT"
            })
            session.audio_buffer.clear()
            return

        try:
            # Process audio through Gemini
            result = await server.process_audio(
                session,
                bytes(session.audio_buffer)
            )
            session.audio_buffer.clear()

            # Check if same language (skip translation)
            if session.source_lang != "auto" and session.source_lang == session.target_lang:
                await websocket.send_json({
                    "type": "skipped",
                    "reason": "same_language",
                    "detected_language": session.source_lang,
                    "detected_language_name": LANGUAGE_NAMES.get(session.source_lang, "")
                })
                return

            # Send text result
            await websocket.send_json({
                "type": "translation",
                "source_text": result["source_text"],
                "translated_text": result["translated_text"],
                "detected_language": result["detected_language"],
                "detected_language_name": LANGUAGE_NAMES.get(result["detected_language"] or "", ""),
                "audio_sample_rate": result["sample_rate"],
                "pipeline": result["pipeline"],
                "latency_ms": int(result["latency_ms"])
            })

            # Send audio if available
            if result["audio"]:
                await websocket.send_bytes(result["audio"])

        except Exception as e:
            logger.error(f"[{session.session_id}] Translation error: {e}")
            import traceback
            traceback.print_exc()
            session.audio_buffer.clear()
            await websocket.send_json({
                "type": "error",
                "message": f"Translation failed: {str(e)}",
                "code": "TRANSLATION_ERROR"
            })

    elif msg_type == "set_language":
        # Override source language - normalize code first (es-ES -> es)
        new_lang = normalize_language_code(data.get("source_lang", ""))
        if new_lang and new_lang in SUPPORTED_LANGUAGES:
            session.source_lang = new_lang
            session.last_detected_lang = new_lang

            await websocket.send_json({
                "type": "language_set",
                "source_lang": new_lang,
                "auto_detect": False
            })
        else:
            await websocket.send_json({
                "type": "error",
                "message": f"Invalid language: {new_lang}",
                "code": "INVALID_LANGUAGE"
            })

    elif msg_type == "enable_auto_detect":
        session.source_lang = "auto"

        await websocket.send_json({
            "type": "auto_detect_enabled",
            "auto_detect": True
        })

    elif msg_type == "end_of_turn":
        # Signal to Gemini that the user has finished speaking
        # NOTE: With Manual VAD, use activity_end instead
        if session.streaming_enabled and session.streaming_ready:
            await server.send_end_of_turn(session)
            await websocket.send_json({
                "type": "end_of_turn_sent"
            })
        else:
            await websocket.send_json({
                "type": "error",
                "message": "Streaming mode not active",
                "code": "NOT_STREAMING"
            })

    elif msg_type == "activity_start":
        # Signal start of user speech (Manual VAD)
        # Call this before sending audio chunks
        if session.streaming_enabled and session.streaming_ready:
            await server.send_activity_start(session)
            await websocket.send_json({
                "type": "activity_start_sent"
            })
        else:
            await websocket.send_json({
                "type": "error",
                "message": "Streaming mode not active",
                "code": "NOT_STREAMING"
            })

    elif msg_type == "activity_end":
        # Signal end of user speech (Manual VAD) - triggers translation response
        if session.streaming_enabled and session.streaming_ready:
            await server.send_activity_end(session)
            await websocket.send_json({
                "type": "activity_end_sent"
            })
        else:
            await websocket.send_json({
                "type": "error",
                "message": "Streaming mode not active",
                "code": "NOT_STREAMING"
            })

    elif msg_type == "clear":
        session.audio_buffer.clear()
        await websocket.send_json({
            "type": "cleared"
        })

    elif msg_type == "ping":
        await websocket.send_json({
            "type": "pong",
            "last_detected_language": session.last_detected_lang,
            "buffer_size": len(session.audio_buffer)
        })

    else:
        logger.warning(f"[{session.session_id}] Unknown message type: {msg_type}")
        await websocket.send_json({
            "type": "error",
            "message": f"Unknown message type: {msg_type}",
            "code": "UNKNOWN_MESSAGE"
        })


# =============================================================================
# Main Entry Point
# =============================================================================

if __name__ == "__main__":
    # Load config for port
    try:
        config = ServerConfig.from_env()
    except ValueError as e:
        logger.error(f"Configuration error: {e}")
        logger.error("Please check GOOGLE_CLOUD_PROJECT and GOOGLE_APPLICATION_CREDENTIALS environment variables")
        sys.exit(1)

    logger.info("=" * 60)
    logger.info("Gemini Live Translation Server")
    logger.info("=" * 60)
    logger.info(f"Model: {config.gemini_model}")
    logger.info(f"Voice: {config.gemini_voice}")
    logger.info(f"Languages: {', '.join(SUPPORTED_LANGUAGES)}")
    logger.info(f"Default: {config.default_source_lang} -> {config.default_target_lang}")
    if config.client_api_key:
        logger.info(f"API Key: ENABLED (key=***{config.client_api_key[-4:]})")
    else:
        logger.warning("API Key: DISABLED - set CLIENT_API_KEY env var for production!")
    logger.info("=" * 60)

    uvicorn.run(
        "gemini_server:app",
        host=config.server_host,
        port=config.server_port,
        reload=False,
        log_level="info"
    )
