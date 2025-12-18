"""
Test client for Gemini Live Translation Server
Verifies WebSocket connection and basic translation flow
"""

import asyncio
import json
import wave
import sys
import os

try:
    import websockets
except ImportError:
    print("Installing websockets...")
    os.system("pip install websockets")
    import websockets

SERVER_URL = "ws://localhost:8001/ws/translate"


async def test_health():
    """Test health endpoint"""
    import urllib.request
    try:
        with urllib.request.urlopen("http://localhost:8001/health") as response:
            data = json.loads(response.read().decode())
            print("✓ Health check passed:")
            print(f"  Server: {data.get('server')}")
            print(f"  Model: {data.get('model')}")
            print(f"  Voice: {data.get('voice')}")
            print(f"  Languages: {data.get('languages')}")
            return True
    except Exception as e:
        print(f"✗ Health check failed: {e}")
        return False


async def test_websocket_connection():
    """Test WebSocket connection"""
    print("\n--- Testing WebSocket Connection ---")

    try:
        async with websockets.connect(SERVER_URL) as ws:
            # Should receive welcome message
            msg = await asyncio.wait_for(ws.recv(), timeout=5.0)
            data = json.loads(msg)

            if data.get("type") == "connected":
                print("✓ WebSocket connected successfully")
                print(f"  Server: {data.get('server')}")
                print(f"  Model: {data.get('model')}")
                print(f"  Auto-detect: {data.get('auto_detect')}")
                return True
            else:
                print(f"✗ Unexpected welcome message: {data}")
                return False

    except asyncio.TimeoutError:
        print("✗ Connection timeout - no welcome message received")
        return False
    except Exception as e:
        print(f"✗ WebSocket connection failed: {e}")
        return False


async def test_configure():
    """Test language configuration"""
    print("\n--- Testing Language Configuration ---")

    try:
        async with websockets.connect(SERVER_URL) as ws:
            # Wait for welcome
            await ws.recv()

            # Send configure message
            await ws.send(json.dumps({
                "type": "configure",
                "source_lang": "de",
                "target_lang": "it"
            }))

            # Wait for configured response
            msg = await asyncio.wait_for(ws.recv(), timeout=10.0)
            data = json.loads(msg)

            if data.get("type") == "configured":
                print("✓ Configuration successful")
                print(f"  Source: {data.get('source_lang')}")
                print(f"  Target: {data.get('target_lang')}")
                print(f"  Gemini session: {data.get('gemini_session')}")
                return True
            else:
                print(f"✗ Unexpected response: {data}")
                return False

    except asyncio.TimeoutError:
        print("✗ Configuration timeout")
        return False
    except Exception as e:
        print(f"✗ Configuration failed: {e}")
        return False


async def test_ping():
    """Test ping/pong"""
    print("\n--- Testing Ping/Pong ---")

    try:
        async with websockets.connect(SERVER_URL) as ws:
            await ws.recv()  # welcome

            await ws.send(json.dumps({"type": "ping"}))

            msg = await asyncio.wait_for(ws.recv(), timeout=5.0)
            data = json.loads(msg)

            if data.get("type") == "pong":
                print("✓ Ping/Pong working")
                return True
            else:
                print(f"✗ Unexpected response: {data}")
                return False

    except Exception as e:
        print(f"✗ Ping failed: {e}")
        return False


async def test_translation_with_audio(audio_file: str = None):
    """Test actual translation with audio file"""
    print("\n--- Testing Translation ---")

    # Generate test audio if no file provided
    if audio_file is None:
        print("  No audio file provided, generating silent test audio...")
        # Create 1 second of silence as test
        import struct
        sample_rate = 16000
        duration = 1.0
        samples = int(sample_rate * duration)
        audio_data = struct.pack(f'<{samples}h', *([0] * samples))
    else:
        try:
            with wave.open(audio_file, 'rb') as wf:
                if wf.getsampwidth() != 2:
                    print(f"✗ Audio must be 16-bit PCM (got {wf.getsampwidth()*8}-bit)")
                    return False
                if wf.getnchannels() != 1:
                    print(f"✗ Audio must be mono (got {wf.getnchannels()} channels)")
                    return False
                audio_data = wf.readframes(wf.getnframes())
                print(f"  Loaded {len(audio_data)} bytes from {audio_file}")
        except Exception as e:
            print(f"✗ Failed to load audio: {e}")
            return False

    try:
        async with websockets.connect(SERVER_URL) as ws:
            # Wait for welcome
            await ws.recv()

            # Configure
            await ws.send(json.dumps({
                "type": "configure",
                "source_lang": "de",
                "target_lang": "it"
            }))
            await ws.recv()  # configured

            # Send audio
            print(f"  Sending {len(audio_data)} bytes of audio...")
            await ws.send(audio_data)

            # Request translation
            await ws.send(json.dumps({"type": "translate"}))

            # Wait for response
            msg = await asyncio.wait_for(ws.recv(), timeout=30.0)

            if isinstance(msg, bytes):
                print(f"✓ Received audio response: {len(msg)} bytes")
                return True
            else:
                data = json.loads(msg)
                if data.get("type") == "translation":
                    print("✓ Translation response received")
                    print(f"  Source text: {data.get('source_text', '(empty)')}")
                    print(f"  Translated: {data.get('translated_text', '(empty)')}")
                    print(f"  Latency: {data.get('latency_ms')}ms")

                    # Check for audio
                    try:
                        audio_msg = await asyncio.wait_for(ws.recv(), timeout=5.0)
                        if isinstance(audio_msg, bytes):
                            print(f"✓ Received audio: {len(audio_msg)} bytes")
                    except asyncio.TimeoutError:
                        print("  (no audio received)")

                    return True
                elif data.get("type") == "error":
                    print(f"✗ Translation error: {data.get('message')}")
                    return False
                else:
                    print(f"? Unexpected response: {data}")
                    return True

    except asyncio.TimeoutError:
        print("✗ Translation timeout (30s)")
        return False
    except Exception as e:
        print(f"✗ Translation failed: {e}")
        import traceback
        traceback.print_exc()
        return False


async def main():
    print("=" * 60)
    print("Gemini Live Translation Server - Test Suite")
    print("=" * 60)

    results = []

    # Test health
    print("\n--- Testing Health Endpoint ---")
    results.append(("Health", await test_health()))

    # Test WebSocket
    results.append(("WebSocket", await test_websocket_connection()))

    # Test configure
    results.append(("Configure", await test_configure()))

    # Test ping
    results.append(("Ping", await test_ping()))

    # Test translation (with test audio or provided file)
    audio_file = sys.argv[1] if len(sys.argv) > 1 else None
    results.append(("Translation", await test_translation_with_audio(audio_file)))

    # Summary
    print("\n" + "=" * 60)
    print("Test Results Summary")
    print("=" * 60)

    passed = 0
    for name, result in results:
        status = "✓ PASS" if result else "✗ FAIL"
        print(f"  {name}: {status}")
        if result:
            passed += 1

    print(f"\nTotal: {passed}/{len(results)} tests passed")

    return passed == len(results)


if __name__ == "__main__":
    success = asyncio.run(main())
    sys.exit(0 if success else 1)
