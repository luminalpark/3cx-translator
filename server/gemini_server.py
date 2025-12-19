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
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
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
    gemini_model: str = "gemini-2.0-flash-exp"
    gemini_voice: str = "Kore"
    server_host: str = "0.0.0.0"
    server_port: int = 8001
    default_source_lang: str = "auto"
    default_target_lang: str = "it"
    max_audio_duration_sec: int = 30
    input_sample_rate: int = 16000
    output_sample_rate: int = 16000

    @classmethod
    def from_env(cls) -> "ServerConfig":
        api_key = os.environ.get("GEMINI_API_KEY")
        if not api_key:
            raise ValueError("GEMINI_API_KEY environment variable is required")

        return cls(
            gemini_api_key=api_key,
            gemini_model=os.environ.get("GEMINI_MODEL", "gemini-2.0-flash-exp"),
            gemini_voice=os.environ.get("GEMINI_VOICE", "Kore"),
            server_host=os.environ.get("SERVER_HOST", "0.0.0.0"),
            server_port=int(os.environ.get("SERVER_PORT", "8001")),
            default_source_lang=os.environ.get("DEFAULT_SOURCE_LANG", "auto"),
            default_target_lang=os.environ.get("DEFAULT_TARGET_LANG", "it"),
        )


# =============================================================================
# Language Mappings
# =============================================================================

GEMINI_LANG_CODES = {
    "de": "de-DE", "german": "de-DE", "deu": "de-DE",
    "es": "es-ES", "spanish": "es-ES", "spa": "es-ES",
    "en": "en-US", "english": "en-US", "eng": "en-US",
    "fr": "fr-FR", "french": "fr-FR", "fra": "fr-FR",
    "it": "it-IT", "italian": "it-IT", "ita": "it-IT",
}

LANGUAGE_NAMES = {
    "de": "German", "deu": "German",
    "es": "Spanish", "spa": "Spanish",
    "en": "English", "eng": "English",
    "fr": "French", "fra": "French",
    "it": "Italian", "ita": "Italian",
}

SUPPORTED_LANGUAGES = ["de", "es", "en", "fr", "it"]


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

        # Get target language BCP-47 code
        target_bcp47 = GEMINI_LANG_CODES.get(session.target_lang, "it-IT")

        # Configure Gemini Live session
        config = types.LiveConnectConfig(
            response_modalities=["AUDIO"],
            speech_config=types.SpeechConfig(
                voice_config=types.VoiceConfig(
                    prebuilt_voice_config=types.PrebuiltVoiceConfig(
                        voice_name=self.config.gemini_voice
                    )
                ),
                language_code=target_bcp47
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
            async with client.aio.live.connect(
                model=self.config.gemini_model,
                config=config
            ) as gemini_session:

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

                # Receive response
                async for response in gemini_session.receive():
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
            session.pending_audio.clear()

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

        # Get target language BCP-47 code
        target_bcp47 = GEMINI_LANG_CODES.get(session.target_lang, "it-IT")

        # Configure Gemini Live session
        config = types.LiveConnectConfig(
            response_modalities=["AUDIO"],
            speech_config=types.SpeechConfig(
                voice_config=types.VoiceConfig(
                    prebuilt_voice_config=types.PrebuiltVoiceConfig(
                        voice_name=self.config.gemini_voice
                    )
                ),
                language_code=target_bcp47
            ),
            system_instruction=system_instruction,
            input_audio_transcription=types.AudioTranscriptionConfig(),
            output_audio_transcription=types.AudioTranscriptionConfig()
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

                    # Send to Gemini
                    await gemini_session.send_realtime_input(
                        audio=types.Blob(
                            data=audio_data,
                            mime_type="audio/pcm;rate=16000"
                        )
                    )

                except asyncio.TimeoutError:
                    continue  # Check is_closing and continue
                except Exception as e:
                    logger.warning(f"[{session.session_id}] Send error: {e}")
                    break

        except asyncio.CancelledError:
            pass

        logger.info(f"[{session.session_id}] Send loop ended")

    async def send_audio_chunk(self, session: ClientSession, audio_data: bytes):
        """Queue audio chunk for sending to Gemini"""
        if session.audio_queue and not session.is_closing:
            # If Gemini is in a model turn, buffer audio to avoid losing it
            if session.in_model_turn:
                session.pending_audio.append(audio_data)
                if len(session.pending_audio) == 1:
                    logger.info(f"[{session.session_id}] Buffering audio during model turn")
            else:
                try:
                    session.audio_queue.put_nowait(audio_data)
                except asyncio.QueueFull:
                    logger.warning(f"[{session.session_id}] Audio queue full, dropping chunk")

    async def _receive_loop(self, session: ClientSession, gemini_session):
        """Receive responses from Gemini and forward to client"""
        logger.info(f"[{session.session_id}] Receive loop started")

        try:
            # Loop to handle multiple turns - receive() completes after each turn
            while not session.is_closing:
                turn_count = 0
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
                                    logger.info(f"[{session.session_id}] Turn {turn_count} complete, waiting for next turn")
                                    await session.websocket.send_json({
                                        "type": "turn_complete"
                                    })

                                    # Flush any audio that was buffered during the model turn
                                    if session.pending_audio:
                                        pending_count = len(session.pending_audio)
                                        logger.info(f"[{session.session_id}] Flushing {pending_count} buffered audio chunks")
                                        for audio_chunk in session.pending_audio:
                                            try:
                                                session.audio_queue.put_nowait(audio_chunk)
                                            except asyncio.QueueFull:
                                                logger.warning(f"[{session.session_id}] Queue full during flush")
                                                break
                                        session.pending_audio.clear()

                                    break  # Exit inner loop to call receive() again

                        except Exception as e:
                            logger.warning(f"[{session.session_id}] Error forwarding response: {e}")

                except Exception as e:
                    if "close" in str(e).lower():
                        logger.info(f"[{session.session_id}] Session closed by server")
                        break
                    logger.warning(f"[{session.session_id}] Receive error: {e}")
                    # Small delay before retrying
                    await asyncio.sleep(0.1)

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
        session.pending_audio.clear()

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


@app.websocket("/ws/translate")
async def websocket_translate(websocket: WebSocket):
    """Main WebSocket endpoint for real-time translation"""
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
        # Update language configuration
        new_source = data.get("source_lang", session.source_lang)
        new_target = data.get("target_lang", session.target_lang)

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
        # Override source language
        new_lang = data.get("source_lang")
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
    logger.info("=" * 60)

    uvicorn.run(
        "gemini_server:app",
        host=config.server_host,
        port=config.server_port,
        reload=False,
        log_level="info"
    )
