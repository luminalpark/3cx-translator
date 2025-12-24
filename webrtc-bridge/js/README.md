# WebRTC Bridge JavaScript Dependencies

## Required External Libraries

### 1. janus.js (Janus Gateway JavaScript Library)

**Option A: From Janus Docker container (recommended)**
After starting the Janus container, copy the file:
```bash
docker cp janus-webrtc-bridge:/usr/share/janus/html/janus.js ./janus.js
```

**Option B: From Janus demos page**
1. Open https://janus.conf.meetecho.com/demos.html
2. View page source or use DevTools
3. Find and download janus.js

**Option C: Clone Janus repo**
```bash
git clone https://github.com/meetecho/janus-gateway.git
cp janus-gateway/html/janus.js ./
```

### 2. adapter.js (WebRTC Adapter)
Download from the WebRTC adapter project:
```bash
curl -o adapter.js https://webrtc.github.io/adapter/adapter-latest.js
```

Or use CDN in index.html:
```html
<script src="https://webrtc.github.io/adapter/adapter-latest.js"></script>
```

## File Structure

- `adapter.js` - WebRTC browser compatibility shim
- `janus.js` - Janus Gateway JavaScript API
- `vad.js` - Voice Activity Detection state machine
- `gemini-client.js` - WebSocket client for Gemini translation server
- `sip-handler.js` - Janus SIP plugin handler
- `bridge-app.js` - Main application logic

## Quick Setup

1. Start Janus Docker container:
```bash
cd ../docker
docker-compose up -d
```

2. Copy janus.js from container:
```bash
docker cp janus-webrtc-bridge:/usr/share/janus/html/janus.js ./janus.js
```

3. adapter.js is already downloaded or use CDN
