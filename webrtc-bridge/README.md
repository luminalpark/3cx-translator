# 3CX Translation Bridge - WebRTC Client

Client WebRTC browser-based che si registra come estensione 3CX per traduzione in tempo reale via Gemini.

## Architettura

```
┌─────────────────┐     SIP      ┌─────────────────┐
│  Remote Caller  │◄────────────►│    3CX PBX      │
└─────────────────┘              └────────┬────────┘
                                          │ SIP
                                          ▼
                                 ┌─────────────────┐
                                 │  Janus Gateway  │
                                 │  (SIP Plugin)   │
                                 └────────┬────────┘
                                          │ WebRTC
                                          ▼
                                 ┌─────────────────┐
                                 │  WebRTC Bridge  │◄──── Browser
                                 │    (questo)     │
                                 └────────┬────────┘
                                          │ WebSocket
                                          ▼
                                 ┌─────────────────┐
                                 │ gemini_server   │
                                 └─────────────────┘
```

## Prerequisiti

- Docker e Docker Compose
- 3CX PBX con accesso admin
- Browser moderno (Chrome/Edge consigliato)
- gemini_server.py in esecuzione

## Quick Start

### 1. Avvia Janus Gateway

```bash
cd docker
docker-compose up -d
```

### 2. Scarica janus.js

Dopo che il container è avviato:
```bash
docker cp janus-webrtc-bridge:/usr/share/janus/html/janus.js js/janus.js
```

### 3. Configura 3CX

1. Crea estensione **900** (Generic SIP Device)
2. Configura password
3. Imposta auto-answer se necessario

### 4. Avvia il bridge

Apri `index.html` in un browser (consigliato usare un server locale):

```bash
# Con Python
python -m http.server 8080

# O con Node.js
npx serve
```

### 5. Configura e connetti

1. Inserisci l'indirizzo del server Janus (default: `ws://localhost:8188`)
2. Inserisci IP del 3CX (es. `192.168.1.100:5060`)
3. Inserisci estensione e password
4. Inserisci URL del Gemini server
5. Clicca "Connect"

## Struttura File

```
webrtc-bridge/
├── index.html              # UI principale
├── css/
│   └── styles.css          # Stili
├── js/
│   ├── janus.js           # Libreria Janus (da scaricare)
│   ├── adapter.js         # WebRTC adapter
│   ├── vad.js             # Voice Activity Detection
│   ├── gemini-client.js   # Client WebSocket Gemini
│   ├── sip-handler.js     # Handler SIP via Janus
│   └── bridge-app.js      # Logica applicazione
├── docker/
│   ├── docker-compose.yml # Janus container
│   └── janus/jcfg/        # Configurazione Janus
├── setup.ps1              # Script setup (Windows)
└── README.md              # Questa guida
```

## Funzionalità

### Chiamate Inbound
- Ricevi chiamate inoltrate da 3CX
- Traduzione automatica audio remoto
- VAD (Voice Activity Detection) configurabile

### Chiamate Outbound
- Dialpad per comporre numeri
- Chiama estensioni o numeri esterni
- DTMF durante la chiamata

### Controlli Chiamata
- Answer/Reject chiamate in arrivo
- Mute/Unmute microfono
- Hangup

### VAD Settings
- Threshold speech/silence configurabili
- Preset per VoIP, ambiente silenzioso, ambiente rumoroso
- Meter RMS in tempo reale

## Configurazione 3CX

### Crea Estensione

1. **Management Console** → **Users** → **Add Extension**
2. Numero: `900`
3. Nome: "Translation Bridge"
4. Tipo: Generic SIP Device
5. Imposta password SIP

### Call Routing

Per inoltrare chiamate al bridge:

**Opzione 1: Ring Group**
- Includi estensione 900 nel ring group

**Opzione 2: IVR**
- Aggiungi opzione "Premi X per traduzione"
- Inoltra a estensione 900

**Opzione 3: Trasferimento manuale**
- Operatore trasferisce chiamata a 900

### Outbound Rules

Per permettere chiamate outbound dal bridge:
1. **Outbound Rules** → Aggiungi regola per estensione 900
2. Configura prefissi permessi

## Troubleshooting

### Janus non si connette
- Verifica che il container sia in esecuzione: `docker ps`
- Controlla i log: `docker logs janus-webrtc-bridge`
- Verifica firewall per porta 8188

### SIP registration fallisce
- Verifica IP del 3CX
- Controlla credenziali estensione
- Verifica che 3CX accetti registrazioni SIP

### Audio non funziona
- Verifica permessi microfono nel browser
- Controlla che Gemini server sia attivo
- Verifica codec supportati (Opus/G.711)

### VAD non rileva speech
- Abbassa Speech Threshold
- Usa preset "Quiet Environment"
- Verifica che l'audio arrivi (controlla RMS meter)

## Sviluppo

### Modificare VAD
Vedi `js/vad.js` - parametri in `VAD_PRESETS`

### Aggiungere lingue
Vedi `js/gemini-client.js` - metodo `configure()`

### Debug
Apri DevTools (F12) per vedere log dettagliati

## Licenza

Parte del progetto 3CX Translation Bridge
