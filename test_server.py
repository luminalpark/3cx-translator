#!/usr/bin/env python3
"""
Test script for SeamlessM4T Translation Server
Verifies WebSocket connection and basic translation
"""

import asyncio
import base64
import json
import sys
import numpy as np

try:
    import websockets
except ImportError:
    print("Installing websockets...")
    import subprocess
    subprocess.check_call([sys.executable, "-m", "pip", "install", "websockets"])
    import websockets


async def test_translation(server_url: str = "ws://localhost:8000/ws/translate"):
    """Test the translation server with synthetic audio"""
    
    print(f"\n🔌 Connecting to {server_url}...")
    
    try:
        async with websockets.connect(server_url) as ws:
            # Wait for welcome
            welcome = await ws.recv()
            welcome_data = json.loads(welcome)
            print(f"✅ Connected! Session: {welcome_data.get('session_id')}")
            print(f"   Model: {welcome_data.get('model')}")
            
            # Configure languages
            print("\n🌐 Configuring: English → Italian")
            await ws.send(json.dumps({
                "type": "config",
                "source_lang": "en",
                "target_lang": "it"
            }))
            
            config_ack = await ws.recv()
            config_data = json.loads(config_ack)
            print(f"✅ Config acknowledged: {config_data.get('source_lang')} → {config_data.get('target_lang')}")
            
            # Generate test audio (1 second of 440Hz sine wave)
            # This won't produce meaningful translation but tests the pipeline
            print("\n🎤 Sending test audio (1s sine wave)...")
            sample_rate = 16000
            duration = 1.0
            t = np.linspace(0, duration, int(sample_rate * duration), dtype=np.float32)
            audio = (np.sin(2 * np.pi * 440 * t) * 0.3 * 32767).astype(np.int16)
            
            # Send audio in chunks
            chunk_size = 3200  # 200ms chunks
            for i in range(0, len(audio), chunk_size // 2):
                chunk = audio[i:i + chunk_size // 2].tobytes()
                await ws.send(chunk)
            
            # Request translation
            print("📤 Requesting translation...")
            await ws.send(json.dumps({"type": "translate"}))
            
            # Wait for result
            result = await asyncio.wait_for(ws.recv(), timeout=30)
            result_data = json.loads(result)
            
            if result_data.get("type") == "result":
                print(f"\n✅ Translation received!")
                print(f"   Source text: \"{result_data.get('source_text', 'N/A')}\"")
                print(f"   Translated: \"{result_data.get('translated_text', 'N/A')}\"")
                print(f"   Processing time: {result_data.get('processing_time_ms', 0):.0f}ms")
                
                # Receive audio
                try:
                    audio_data = await asyncio.wait_for(ws.recv(), timeout=5)
                    if isinstance(audio_data, bytes):
                        print(f"   Audio received: {len(audio_data)} bytes")
                except asyncio.TimeoutError:
                    print("   ⚠️  No audio received (timeout)")
            else:
                print(f"⚠️  Unexpected response: {result_data}")
            
            print("\n✅ Test completed successfully!")
            return True
            
    except websockets.exceptions.ConnectionClosed as e:
        print(f"❌ Connection closed: {e}")
        return False
    except asyncio.TimeoutError:
        print("❌ Timeout waiting for response")
        return False
    except Exception as e:
        print(f"❌ Error: {e}")
        return False


async def test_health(server_url: str = "http://localhost:8000"):
    """Test health endpoint"""
    import urllib.request
    
    print(f"🏥 Checking health at {server_url}/health...")
    
    try:
        with urllib.request.urlopen(f"{server_url}/health", timeout=10) as response:
            data = json.loads(response.read().decode())
            print(f"✅ Server healthy!")
            print(f"   Status: {data.get('status')}")
            print(f"   Device: {data.get('device')}")
            print(f"   GPU Memory: {data.get('gpu_memory_used_gb', 0):.1f}GB / {data.get('gpu_memory_total_gb', 0):.1f}GB")
            print(f"   Active sessions: {data.get('active_sessions', 0)}")
            return True
    except Exception as e:
        print(f"❌ Health check failed: {e}")
        return False


async def main():
    server = sys.argv[1] if len(sys.argv) > 1 else "localhost:8000"
    
    print("=" * 60)
    print("SeamlessM4T Translation Server Test")
    print("=" * 60)
    
    # Test health
    if not await test_health(f"http://{server}"):
        print("\n💡 Make sure the server is running:")
        print("   cd server && python seamless_server.py")
        return
    
    # Test WebSocket translation
    await test_translation(f"ws://{server}/ws/translate")


if __name__ == "__main__":
    asyncio.run(main())
