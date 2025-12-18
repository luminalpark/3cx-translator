# Voice References for Voice Cloning

Place your voice reference WAV files in this directory to enable voice cloning.

## Requirements

- **Format**: WAV (PCM)
- **Sample Rate**: 16kHz or 24kHz
- **Channels**: Mono
- **Duration**: 5-10 seconds
- **Content**: Clear speech without background noise

## File Naming Convention

```
voice_default.wav    -> Used for ALL languages (fallback)
voice_it.wav         -> Used for Italian output
voice_en.wav         -> Used for English output
voice_de.wav         -> Used for German output
voice_fr.wav         -> Used for French output
voice_es.wav         -> Used for Spanish output
```

## Priority

1. Language-specific voice (e.g., `voice_it.wav` for Italian)
2. Default voice (`voice_default.wav`)
3. Built-in Chatterbox voice (if no reference files)

## Recording Tips

1. Record in a quiet environment
2. Speak naturally at normal pace
3. Use consistent volume
4. Avoid background music/noise
5. Sample phrases that cover various sounds in the language

## Example Recording Script (Italian)

> "Ciao, mi chiamo Carlo e lavoro come operatore telefonico.
> Sono qui per aiutarti con qualsiasi domanda tu possa avere.
> Non esitare a chiedere se hai bisogno di assistenza."

## Quick Recording with FFmpeg

```bash
# Record 10 seconds from microphone (Linux/Mac)
ffmpeg -f alsa -i default -t 10 -ar 16000 -ac 1 voice_default.wav

# Record 10 seconds (Windows with DirectShow)
ffmpeg -f dshow -i audio="Microphone" -t 10 -ar 16000 -ac 1 voice_default.wav
```

## Testing

After adding voice references, restart the server. Check logs for:
```
[4/4] Loading voice references...
Loaded voice reference: default -> voice_default.wav
Voice cloning enabled with 1 voice(s)
```
