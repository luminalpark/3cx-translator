"""
Streaming Translation Test Client
Tests real-time translation with OpenAI Server VAD

Usage:
    pip install websockets pyaudio
    python test_streaming_client.py

Controls:
    - Speak into microphone
    - Translation starts automatically when you stop speaking (VAD)
    - Audio plays back as it streams
    - Ctrl+C to stop
"""

import asyncio
import json
import sys
import struct
import threading
import queue

try:
    import websockets
except ImportError:
    print("Install websockets: pip install websockets")
    sys.exit(1)

try:
    import pyaudio
except ImportError:
    print("Install pyaudio: pip install pyaudio")
    sys.exit(1)

# Configuration
SERVER_URL = "ws://localhost:8001/ws/translate"
SAMPLE_RATE = 16000
CHANNELS = 1
CHUNK_SIZE = 1600  # 100ms at 16kHz
FORMAT = pyaudio.paInt16

# Audio queues
playback_queue = queue.Queue()
stop_event = threading.Event()


def audio_capture_thread(ws_send_queue: queue.Queue):
    """Capture audio from microphone and queue for sending"""
    p = pyaudio.PyAudio()

    # Find default input device
    try:
        stream = p.open(
            format=FORMAT,
            channels=CHANNELS,
            rate=SAMPLE_RATE,
            input=True,
            frames_per_buffer=CHUNK_SIZE
        )
        print(f"🎤 Recording from default microphone (16kHz)")
    except Exception as e:
        print(f"Error opening microphone: {e}")
        return

    try:
        while not stop_event.is_set():
            try:
                data = stream.read(CHUNK_SIZE, exception_on_overflow=False)
                ws_send_queue.put(data)
            except Exception as e:
                print(f"Recording error: {e}")
                break
    finally:
        stream.stop_stream()
        stream.close()
        p.terminate()
        print("🎤 Recording stopped")


def audio_playback_thread():
    """Play audio from queue"""
    p = pyaudio.PyAudio()

    try:
        stream = p.open(
            format=FORMAT,
            channels=CHANNELS,
            rate=SAMPLE_RATE,
            output=True,
            frames_per_buffer=CHUNK_SIZE
        )
        print(f"🔊 Playing to default speaker (16kHz)")
    except Exception as e:
        print(f"Error opening speaker: {e}")
        return

    try:
        while not stop_event.is_set():
            try:
                data = playback_queue.get(timeout=0.1)
                stream.write(data)
            except queue.Empty:
                continue
            except Exception as e:
                print(f"Playback error: {e}")
                break
    finally:
        stream.stop_stream()
        stream.close()
        p.terminate()
        print("🔊 Playback stopped")


async def main():
    print("=" * 60)
    print("Streaming Translation Test Client")
    print("=" * 60)
    print(f"Server: {SERVER_URL}")
    print("Speak into your microphone. Translation plays back automatically.")
    print("Press Ctrl+C to stop.")
    print("=" * 60)

    ws_send_queue = queue.Queue()

    # Start audio threads
    capture_thread = threading.Thread(target=audio_capture_thread, args=(ws_send_queue,))
    playback_thread = threading.Thread(target=audio_playback_thread)

    capture_thread.start()
    playback_thread.start()

    try:
        async with websockets.connect(SERVER_URL) as ws:
            print("✅ Connected to server")

            # Wait for welcome message
            welcome = await ws.recv()
            welcome_data = json.loads(welcome)
            print(f"Server: {welcome_data.get('server')}")
            print(f"Model: {welcome_data.get('model')}")
            print(f"VAD enabled: {welcome_data.get('vad_enabled')}")

            # Enable streaming mode
            await ws.send(json.dumps({
                "type": "set_streaming",
                "enabled": True
            }))

            # Configure language (Italian -> English for testing)
            await ws.send(json.dumps({
                "type": "configure",
                "source_lang": "auto",
                "target_lang": "en"
            }))

            # Handle messages
            async def receive_messages():
                try:
                    async for message in ws:
                        if isinstance(message, bytes):
                            # Audio data - queue for playback
                            playback_queue.put(message)
                        else:
                            # JSON message
                            data = json.loads(message)
                            msg_type = data.get("type")

                            if msg_type == "speech_started":
                                print("🎙️ Speech detected...")
                            elif msg_type == "speech_stopped":
                                print("⏸️ Speech ended, translating...")
                            elif msg_type == "translation_started":
                                print("🔄 Translation started...")
                            elif msg_type == "translation":
                                source = data.get("source_text", "")
                                translated = data.get("translated_text", "")
                                print(f"\n📝 '{source}'")
                                print(f"🌐 '{translated}'\n")
                            elif msg_type == "configured":
                                print(f"✅ Configured: {data.get('source_lang')} -> {data.get('target_lang')}")
                            elif msg_type == "streaming_mode":
                                print(f"✅ Streaming mode: {data.get('enabled')}")
                            elif msg_type == "error":
                                print(f"❌ Error: {data.get('message')}")
                except websockets.exceptions.ConnectionClosed:
                    print("Connection closed")

            # Send audio continuously
            async def send_audio():
                while not stop_event.is_set():
                    try:
                        # Non-blocking get from queue
                        try:
                            data = ws_send_queue.get_nowait()
                            await ws.send(data)
                        except queue.Empty:
                            await asyncio.sleep(0.01)
                    except Exception as e:
                        print(f"Send error: {e}")
                        break

            # Run both tasks
            await asyncio.gather(
                receive_messages(),
                send_audio()
            )

    except KeyboardInterrupt:
        print("\n⏹️ Stopping...")
    except Exception as e:
        print(f"Error: {e}")
    finally:
        stop_event.set()
        capture_thread.join(timeout=1)
        playback_thread.join(timeout=1)
        print("Done.")


if __name__ == "__main__":
    asyncio.run(main())
