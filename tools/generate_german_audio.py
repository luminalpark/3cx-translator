#!/usr/bin/env python3
"""
Generate German TTS audio for testing the 3CX Translation Bridge.

This script generates a ~1 minute WAV file with German speech
simulating a customer call scenario.

Requirements:
    pip install gtts pydub

Also requires ffmpeg installed on the system for audio conversion.
"""

import os
import sys
from pathlib import Path

try:
    from gtts import gTTS
    from pydub import AudioSegment
except ImportError:
    print("Error: Required packages not installed.")
    print("Run: pip install gtts pydub")
    print("Also ensure ffmpeg is installed on your system.")
    sys.exit(1)

# German customer scenario text (~1 minute when spoken)
GERMAN_TEXT = """
Guten Tag, hier spricht Hans Mueller.
Ich rufe an wegen meiner Bestellung vom letzten Montag.
Die Bestellnummer ist vier fünf sieben acht neun.

Ich habe das Paket noch nicht erhalten und wollte fragen,
wo es sich gerade befindet.
Können Sie mir bitte den aktuellen Status mitteilen?

Außerdem hätte ich noch eine Frage zu einem anderen Produkt.
Ich interessiere mich für das Modell XL in der Farbe Blau.
Ist dieses Produkt derzeit auf Lager?
Und wie lange dauert die Lieferung nach Italien?

Wenn es nicht auf Lager ist, wann erwarten Sie die nächste Lieferung?
Ich brauche es spätestens bis Ende des Monats.

Können Sie mir auch einen Kostenvoranschlag für den Versand geben?
Ich wohne in Rom, in der Nähe vom Bahnhof Termini.

Vielen Dank für Ihre Hilfe.
Ich warte auf Ihre Antwort.
Auf Wiederhören!
"""

def generate_german_audio(output_path: str = None):
    """Generate German TTS audio and save as WAV."""

    if output_path is None:
        # Default output path
        script_dir = Path(__file__).parent
        output_path = script_dir / "german_customer_call.wav"
    else:
        output_path = Path(output_path)

    temp_mp3 = output_path.with_suffix('.mp3')

    print("Generating German TTS audio...")
    print(f"Text length: {len(GERMAN_TEXT)} characters")

    # Generate TTS (gTTS outputs MP3)
    tts = gTTS(text=GERMAN_TEXT, lang='de', slow=False)
    tts.save(str(temp_mp3))
    print(f"Generated MP3: {temp_mp3}")

    # Convert to WAV 16kHz mono (required format for translation system)
    print("Converting to WAV 16kHz mono...")
    audio = AudioSegment.from_mp3(str(temp_mp3))

    # Convert to mono and resample to 16kHz
    audio = audio.set_channels(1)
    audio = audio.set_frame_rate(16000)
    audio = audio.set_sample_width(2)  # 16-bit

    # Export as WAV
    audio.export(str(output_path), format='wav')

    # Clean up temp MP3
    temp_mp3.unlink()

    # Print info
    duration_sec = len(audio) / 1000
    file_size = output_path.stat().st_size

    print(f"\nGenerated: {output_path}")
    print(f"Duration: {duration_sec:.1f} seconds ({duration_sec/60:.1f} minutes)")
    print(f"Format: WAV, 16kHz, Mono, 16-bit")
    print(f"File size: {file_size / 1024:.1f} KB")

    return str(output_path)

if __name__ == "__main__":
    output = sys.argv[1] if len(sys.argv) > 1 else None
    generate_german_audio(output)
