"""
Gemini Live API Translation Server
Real-time speech-to-speech translation using Google Gemini

Proxy architecture:
  Client (WebSocket) -> This Server -> Gemini Live API (WebSocket)

Features:
- Real-time bidirectional audio streaming
- Automatic language detection
- 5 language pairs: DE, ES, EN, FR <-> IT

Usage:
    GEMINI_API_KEY=your_key python gemini_server.py
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
import wave
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
    gemini_api_key: str
    gemini_model: str = "gemini-2.5-flash-native-audio-preview-12-2025"
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
        api_key = os.environ.get("GEMINI_API_KEY")
        if not api_key:
            raise ValueError("GEMINI_API_KEY environment variable is required")

        return cls(
            gemini_api_key=api_key,
            gemini_model=os.environ.get("GEMINI_MODEL", "gemini-2.5-flash-native-audio-preview-12-2025"),
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


# Debug: counter for audio files
_audio_file_counter = 0

def save_debug_audio(audio_data: bytes, session_id: str, direction: str):
    """Save audio to WAV file for debugging"""
    global _audio_file_counter
    _audio_file_counter += 1

    # Create logs directory if not exists
    os.makedirs("/app/logs", exist_ok=True)

    filename = f"/app/logs/debug_{session_id}_{direction}_{_audio_file_counter}.wav"

    try:
        with wave.open(filename, 'wb') as wav_file:
            wav_file.setnchannels(1)  # mono
            wav_file.setsampwidth(2)  # 16-bit
            wav_file.setframerate(16000)  # 16kHz
            wav_file.writeframes(audio_data)

        logger.info(f"[{session_id}] DEBUG: Saved audio to {filename}")
    except Exception as e:
        logger.warning(f"[{session_id}] Failed to save debug audio: {e}")


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
        """Lazy initialization of Google GenAI client"""
        if self._genai_client is None:
            from google import genai
            self._genai_client = genai.Client(api_key=self.config.gemini_api_key)
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

        # DEBUG: Save audio to file for analysis
        save_debug_audio(audio_data, session.session_id, f"{session.source_lang}_to_{session.target_lang}")
        

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

                # Send audio to Gemini (raw bytes)
                await gemini_session.send(
                    input=types.LiveClientRealtimeInput(
                        media_chunks=[
                            types.Blob(
                                mime_type="audio/pcm;rate=16000",
                                data=audio_data  # Send raw bytes
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
            # Log audio info to help debug sample rate issues
            num_samples = len(translated_audio) // 2  # 16-bit = 2 bytes per sample
            duration_24k = num_samples / 24000
            duration_16k = num_samples / 16000
            duration_48k = num_samples / 48000
            logger.info(f"[{session.session_id}] Received audio: {len(translated_audio)} bytes, {num_samples} samples")
            logger.info(f"[{session.session_id}] Duration if 24kHz: {duration_24k:.2f}s, if 16kHz: {duration_16k:.2f}s, if 48kHz: {duration_48k:.2f}s")

            # Gemini outputs 24kHz audio (verify with logs above if using different model)
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
        # MANUAL VAD: We disable automatic voice activity detection and control
        # turn boundaries manually with activity_start/activity_end signals.
        # This gives precise control for file playback and call simulation.
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
            output_audio_transcription=types.AudioTranscriptionConfig(),
            realtime_input_config=types.RealtimeInputConfig(
                automatic_activity_detection=types.AutomaticActivityDetection(
                    disabled=True
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
        """Send audio chunks from queue to Gemini"""
        from google.genai import types

        logger.info(f"[{session.session_id}] Send loop started")

        try:
            while not session.is_closing:
                try:
                    # Wait for audio with timeout to check is_closing periodically
                    audio_data = await asyncio.wait_for(
                        session.audio_queue.get(),
                        timeout=1.0
                    )

                    if audio_data is None:  # Poison pill
                        break

                    # Send audio to Gemini
                    await gemini_session.send_realtime_input(
                        audio=types.Blob(
                            data=audio_data,
                            mime_type="audio/pcm;rate=16000"
                        )
                    )
                    session.chunks_sent += 1
                    # Log every 40 chunks (~1 second at 25ms chunks)
                    if session.chunks_sent % 40 == 0:
                        logger.info(f"[{session.session_id}] Audio chunks sent to Gemini: {session.chunks_sent}")

                except asyncio.TimeoutError:
                    continue  # Check is_closing and continue
                except Exception as e:
                    logger.warning(f"[{session.session_id}] Send error: {e}")
                    break

        except asyncio.CancelledError:
            pass

        logger.info(f"[{session.session_id}] Send loop ended")

    async def send_audio_chunk(self, session: ClientSession, audio_data: bytes):
        """Queue audio chunk for sending to Gemini.

        Best practice: Always send audio continuously (full duplex).
        Do NOT buffer during model turn - Gemini handles this internally.
        """
        if session.audio_queue and not session.is_closing:
            try:
                session.audio_queue.put_nowait(audio_data)
                session.chunks_received += 1
                session.last_chunk_time = time.time()
                # Log every 40 chunks (~1 second at 25ms chunks)
                if session.chunks_received % 40 == 0:
                    logger.info(f"[{session.session_id}] Audio chunks queued: {session.chunks_received} (queue size: {session.audio_queue.qsize()})")
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
        """
        from google.genai import types

        if session.gemini_session and session.streaming_ready and not session.is_closing:
            try:
                logger.info(f"[{session.session_id}] Sending activity_start to Gemini (Manual VAD)")
                await session.gemini_session.send_realtime_input(
                    activity_start=types.ActivityStart()
                )
            except Exception as e:
                logger.warning(f"[{session.session_id}] Failed to send activity_start: {e}")

    async def send_activity_end(self, session: ClientSession):
        """Signal end of user speech (Manual VAD).

        Call this after sending audio chunks to indicate the user stopped speaking.
        This triggers Gemini to generate the translation response.
        Required when automatic_activity_detection is disabled.
        """
        from google.genai import types

        if session.gemini_session and session.streaming_ready and not session.is_closing:
            try:
                logger.info(f"[{session.session_id}] Sending activity_end to Gemini (Manual VAD) - triggering response")
                await session.gemini_session.send_realtime_input(
                    activity_end=types.ActivityEnd()
                )
            except Exception as e:
                logger.warning(f"[{session.session_id}] Failed to send activity_end: {e}")

    async def _receive_loop(self, session: ClientSession, gemini_session):
        """Receive responses from Gemini and forward to client"""
        logger.info(f"[{session.session_id}] Receive loop started")

        try:
            # Loop to handle multiple turns - receive() completes after each turn
            outer_loop_count = 0
            while not session.is_closing:
                outer_loop_count += 1
                turn_count = 0
                logger.info(f"[{session.session_id}] Starting receive() iteration #{outer_loop_count}")
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

                                # Audio from model turn
                                if hasattr(content, 'model_turn') and content.model_turn:
                                    # Mark that we're in a model turn (Gemini is speaking)
                                    if not session.in_model_turn:
                                        session.in_model_turn = True
                                        logger.info(f"[{session.session_id}] Model turn started")
                                        await session.websocket.send_json({
                                            "type": "model_turn_started"
                                        })

                                    for part in content.model_turn.parts:
                                        if hasattr(part, 'inline_data') and part.inline_data:
                                            data = part.inline_data.data
                                            mime = getattr(part.inline_data, 'mime_type', 'unknown')
                                            if isinstance(data, bytes):
                                                # Log for sample rate debugging
                                                num_samples = len(data) // 2
                                                logger.debug(f"[{session.session_id}] Stream chunk: {len(data)} bytes ({num_samples} samples), mime: {mime}")
                                                # Resample from 24kHz to 16kHz
                                                audio_24k = np.frombuffer(data, dtype=np.int16)
                                                audio_16k = resample_audio(audio_24k, 24000, 16000)
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
                                                    await session.websocket.send_bytes(audio_16k.tobytes())
                                                except Exception as e:
                                                    logger.warning(f"[{session.session_id}] Failed to decode audio: {e}")

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
                                    logger.info(f"[{session.session_id}] Turn {turn_count} complete (chunks: queued={session.chunks_received}, sent={session.chunks_sent})")
                                    await session.websocket.send_json({
                                        "type": "turn_complete"
                                    })

                                    # With Manual VAD: Client will send activity_start/activity_end
                                    # for the next turn. No automatic audio_stream_end needed.
                                    logger.info(f"[{session.session_id}] Ready for next turn (Manual VAD)")
                                    break  # Exit inner loop to call receive() again

                        except Exception as e:
                            logger.warning(f"[{session.session_id}] Error forwarding response: {e}")

                    # After inner for-loop completes (turn_complete), log before calling receive() again
                    logger.info(f"[{session.session_id}] Calling receive() for next turn")

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
            logger.error(f"[{session.session_id}] Receive loop error: {e}")

        logger.info(f"[{session.session_id}] Receive loop ended")

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
                if session.streaming_enabled and session.streaming_ready:
                    # Streaming mode: forward audio directly to Gemini
                    await server.send_audio_chunk(session, message["bytes"])
                else:
                    # Buffer mode: add to buffer for later processing
                    session.audio_buffer.extend(message["bytes"])

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
        # Signal end of user speech (Manual VAD)
        # Call this after sending audio chunks to trigger translation
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
        logger.error("Please set GEMINI_API_KEY environment variable")
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
