# 3CX Real-Time Translation Bridge

Sistema di traduzione vocale **bidirezionale** con **rilevamento automatico della lingua**, **selezione manuale** per chiamate in uscita, e **override** in caso di rilevamento errato.

## ✅ Scenari Supportati

| Scenario | Azione Operatore |
|----------|------------------|
| **Chiamata in entrata** | Nessuna - Auto-detect rileva la lingua |
| **Chiamata in uscita** | Click destro → Seleziona lingua → Chiama |
| **Lingua rilevata errata** | Click destro → Seleziona lingua corretta (Override) |
| **Reset** | Click destro → Auto-Detect |

## Interfaccia Utente (System Tray)

```
┌─────────────────────────────────────────────────┐
│  3CX Translation Bridge                         │
├─────────────────────────────────────────────────┤
│  Operatore: IT                                  │
│  Lingua cliente: Tedesco (rilevata)             │  ← Stato attuale
├─────────────────────────────────────────────────┤
│  ═══ MODALITÀ ═══                               │
│  ✓ 🔍 Auto-Detect (rileva automaticamente)     │
├─────────────────────────────────────────────────┤
│  💡 Lingua sbagliata? Seleziona quella corretta │  ← Hint per override
├─────────────────────────────────────────────────┤
│  ═══ SELEZIONA LINGUA ═══                       │
│    🇩🇪 Tedesco ← rilevata                       │  ← Evidenziata
│    🇬🇧 Inglese (English)                        │
│    🇫🇷 Francese (French)                        │
│    🇪🇸 Spagnolo (Spanish)                       │
│    ...                                           │
├─────────────────────────────────────────────────┤
│  🔄 Reset (torna ad Auto-Detect)                │
│  ❌ Esci                                        │
└─────────────────────────────────────────────────┘
```

## Override Lingua

Quando l'auto-detect rileva una lingua errata:

```
1. Sistema rileva: "TEDESCO" (ma il cliente parla francese)
   │
2. Operatore: click destro sull'icona tray
   │
3. Operatore: seleziona "🇫🇷 Francese"
   │
4. Sistema: "OVERRIDE MANUALE: FRANCESE"
   │
5. Traduzione ora usa Francese ↔ Italiano
```

### Indicatori Visivi

| Colore Icona | Significato |
|--------------|-------------|
| 🔵 Blu | Auto-detect attivo |
| 🟢 Verde | Lingua selezionata manualmente (outbound) |
| 🟠 Arancione | Override attivo (correzione manuale) |

### Notifiche

```
┌─────────────────────────────────┐
│ ⚠️ Override                     │
│ 🇫🇷 Francese                    │
│ Override manuale attivo         │
└─────────────────────────────────┘
```

## Flusso Completo

```
╔══════════════════════════════════════════════════════════════════════════╗
║                                                                          ║
║  CHIAMATA IN ENTRATA              OVERRIDE LINGUA                        ║
║  (auto-detect)                    (correzione errore)                    ║
║                                                                          ║
║  1. Cliente chiama                1. Auto-detect dice "TEDESCO"          ║
║  2. Cliente parla                 2. Ma il cliente parla francese!       ║
║  3. Sistema: "TEDESCO"            3. Operatore: click destro             ║
║  4. Traduzione DE↔IT             4. Seleziona "🇫🇷 Francese"             ║
║                                   5. Sistema: "OVERRIDE: FRANCESE"       ║
║                                   6. Traduzione ora FR↔IT               ║
║                                                                          ║
║  ────────────────────────────────────────────────────────────────────── ║
║                                                                          ║
║  CHIAMATA IN USCITA               RESET                                  ║
║  (selezione manuale)              (torna ad auto)                        ║
║                                                                          ║
║  1. Click destro                  1. Click destro                        ║
║  2. Seleziona lingua              2. "🔍 Auto-Detect" oppure             ║
║  3. Chiama cliente                3. "🔄 Reset"                          ║
║  4. Traduzione attiva             4. Sistema torna in auto-detect        ║
║                                                                          ║
╚══════════════════════════════════════════════════════════════════════════╝
```

## Output Console

```
╔═══════════════════════════════════════════════════════════╗
║  🌐 LINGUA RILEVATA: GERMAN                               ║
║  💡 Click destro sull'icona per correggere se errata     ║
╚═══════════════════════════════════════════════════════════╝

[INBOUND] [deu] "Guten Tag..." → "Buongiorno..." | 523ms

--- Operatore fa override ---

╔═══════════════════════════════════════════════════════════╗
║  ⚠️  OVERRIDE MANUALE: FR                                 ║
║  La lingua rilevata è stata corretta dall'operatore       ║
╚═══════════════════════════════════════════════════════════╝

[INBOUND] [fra] "Bonjour..." → "Buongiorno..." | 487ms
```

## Architettura

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           NVIDIA DGX Spark                               │
│                                                                          │
│   ┌────────────────────────────────────────────────────────────────┐    │
│   │              SeamlessM4T v2 Server (Python/FastAPI)            │    │
│   │                                                                 │    │
│   │   • Auto-detection lingua                                      │    │
│   │   • Traduzione speech-to-speech                                │    │
│   │   • WebSocket streaming                                         │    │
│   └────────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────────┘
                               │ Network
┌──────────────────────────────┼───────────────────────────────────────────┐
│                        Windows PC (Operatore)                            │
│                                                                          │
│   ┌─────────────────────────────────────────────────────────────────┐   │
│   │                    Translation Bridge (.NET 8)                   │   │
│   │                                                                  │   │
│   │  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │   │
│   │  │ Tray Icon   │  │ INBOUND     │  │ OUTBOUND                │  │   │
│   │  │ (UI)        │  │ Client→Op   │  │ Op→Client               │  │   │
│   │  └─────────────┘  └─────────────┘  └─────────────────────────┘  │   │
│   └─────────────────────────────────────────────────────────────────┘   │
│                                                                          │
│   ┌─────────────────────────────────────────────────────────────────┐   │
│   │                        VB-Cable Audio Routing                    │   │
│   │                                                                  │   │
│   │  3CX Speaker → VB-Cable A → Bridge → Cuffie Operatore          │   │
│   │  Microfono Op → Bridge → VB-Cable B → 3CX Microphone           │   │
│   └─────────────────────────────────────────────────────────────────┘   │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘
```

## Installazione

### 1. Server (DGX Spark)

```bash
cd server
pip install -r requirements.txt
python seamless_server.py
```

### 2. Client (Windows)

```powershell
# Installa VB-Cable (2 dispositivi)
# Scarica da: https://vb-audio.com/Cable/

# Configura 3CX:
# - Speaker: CABLE Input
# - Microphone: CABLE-A Output

# Avvia client
cd client
dotnet run --project src\TranslationBridge
```

## Configurazione

```json
{
  "TranslationBridge": {
    "ServerUrl": "ws://192.168.1.100:8000/ws/translate",
    
    "Languages": {
      "LocalLanguage": "it",
      "RemoteLanguage": "auto",
      "ExpectedLanguages": ["de", "en", "fr", "es", "it", "pt", "ru"],
      "SkipSameLanguage": true
    }
  }
}
```

## Lingue Supportate

| Codice | Lingua | Menu Tray |
|--------|--------|-----------|
| `de` | Tedesco | 🇩🇪 Tedesco (German) |
| `en` | Inglese | 🇬🇧 Inglese (English) |
| `fr` | Francese | 🇫🇷 Francese (French) |
| `es` | Spagnolo | 🇪🇸 Spagnolo (Spanish) |
| `pt` | Portoghese | 🇵🇹 Portoghese (Portuguese) |
| `ru` | Russo | 🇷🇺 Russo (Russian) |
| `zh` | Cinese | 🇨🇳 Cinese (Chinese) |
| `ja` | Giapponese | 🇯🇵 Giapponese (Japanese) |
| `ko` | Coreano | 🇰🇷 Coreano (Korean) |
| `ar` | Arabo | 🇸🇦 Arabo (Arabic) |
| `nl` | Olandese | 🇳🇱 Olandese (Dutch) |
| `pl` | Polacco | 🇵🇱 Polacco (Polish) |

## Output Console

```
╔════════════════════════════════════════════════════════════╗
║  3CX Translation Bridge - Multi-Language Support          ║
╠════════════════════════════════════════════════════════════╣
║  CHIAMATE IN ENTRATA: Auto-detect attivo                  ║
║  CHIAMATE IN USCITA:  Seleziona lingua dal menu tray     ║
╚════════════════════════════════════════════════════════════╝

Lingua operatore: IT
Lingue supportate: de, en, fr, es, it, pt, ru

════════════════════════════════════════════════════════════
  ✓ Translation Active
  ✓ Tray icon ready - click destro per selezionare lingua
════════════════════════════════════════════════════════════

╔═══════════════════════════════════════════════╗
║  LINGUA RILEVATA: GERMAN                      ║
╚═══════════════════════════════════════════════╝

[INBOUND] [deu] "Guten Tag..." → "Buongiorno..." | 523ms
[OUTBOUND] IT → DE: "Come posso..." → "Wie kann ich..." | 487ms
```

## Performance

| Metrica | Valore |
|---------|--------|
| Latenza traduzione | ~500-800ms |
| Latenza detection | ~200-300ms |
| VRAM GPU | ~11GB |
| RAM Client | ~100MB |

## Troubleshooting

| Problema | Soluzione |
|----------|-----------|
| Lingua rilevata errata | Click destro → Seleziona lingua corretta (Override) |
| Override non funziona | Verifica che la lingua sia nella lista supportate |
| Icona tray non appare | Esegui come applicazione, non come servizio |
| Menu non risponde | Riavvia applicazione |
| Traduzione non funziona | Verifica connessione al server |
| Vuoi tornare ad auto-detect | Click destro → "🔄 Reset" |

## Licenze

- **SeamlessM4T v2**: CC BY-NC 4.0 (non commerciale)
- **Codice**: MIT
