#!/bin/bash
# Setup script for Janus 3CX Bridge on Linux VM
# Run this script on the VM: bash setup-janus.sh

set -e

echo "=== Creating Janus 3CX Bridge configuration ==="

# Create directories
mkdir -p ~/janus/config

# Create docker-compose.yml
cat > ~/janus/docker-compose.yml << 'EOF'
version: '3.8'

services:
  janus:
    image: canyan/janus-gateway:latest
    container_name: janus-3cx-bridge
    restart: unless-stopped
    network_mode: host
    volumes:
      - ./config/janus.jcfg:/usr/local/etc/janus/janus.jcfg:ro
      - ./config/janus.plugin.sip.jcfg:/usr/local/etc/janus/janus.plugin.sip.jcfg:ro
      - ./config/janus.transport.http.jcfg:/usr/local/etc/janus/janus.transport.http.jcfg:ro
      - ./config/janus.transport.websockets.jcfg:/usr/local/etc/janus/janus.transport.websockets.jcfg:ro
    environment:
      - JANUS_ADMIN_SECRET=xK9mP2vL8nQ4wR7jF3hT6yB1cA5dG0sE
EOF

# Create janus.jcfg
cat > ~/janus/config/janus.jcfg << 'EOF'
general: {
    configs_folder = "/etc/janus"
    plugins_folder = "/usr/local/lib/janus/plugins"
    transports_folder = "/usr/local/lib/janus/transports"
    events_folder = "/usr/local/lib/janus/events"
    loggers_folder = "/usr/local/lib/janus/loggers"
    debug_level = 5
    admin_secret = "xK9mP2vL8nQ4wR7jF3hT6yB1cA5dG0sE"
    token_auth = false
    server_name = "3CX Translation Bridge"
}

certificates: {
}

media: {
    ipv6 = false
    rtp_port_range = "10000-10100"
    dscp = 46
}

nat: {
    ice_lite = false
    ice_tcp = false
    full_trickle = true
    stun_server = "stun.l.google.com"
    stun_port = 19302
    nat_1_1_mapping = "10.0.5.19"
}

plugins: {
    disable = ""
}

transports: {
    disable = ""
}
EOF

# Create janus.plugin.sip.jcfg
cat > ~/janus/config/janus.plugin.sip.jcfg << 'EOF'
general: {
    local_ip = "10.0.5.19"
    sdp_ip = "10.0.5.19"
    rtp_port_range = "10000-10100"
    user_agent = "3CX-Translation-Bridge/1.0"
    register_ttl = 3600
    behind_nat = false
    keepalive_interval = 60
}

codecs: {
    audio_codecs = "pcmu,pcma,opus"
    video_codecs = ""
}

sofia: {
    log_level = 5
}
EOF

# Create janus.transport.http.jcfg
cat > ~/janus/config/janus.transport.http.jcfg << 'EOF'
general: {
    json = "indented"
    base_path = "/janus"
    http = true
    port = 8088
    https = false
}

admin: {
    admin_base_path = "/admin"
    admin_http = false
}

cors: {
}
EOF

# Create janus.transport.websockets.jcfg
cat > ~/janus/config/janus.transport.websockets.jcfg << 'EOF'
general: {
    json = "indented"
    pingpong_trigger = 30
    pingpong_timeout = 10
    ws = true
    ws_port = 8188
    wss = false
}

admin: {
    admin_ws = false
}

cors: {
}
EOF

echo ""
echo "=== Configuration created in ~/janus ==="
echo ""
echo "Files created:"
ls -la ~/janus/
ls -la ~/janus/config/
echo ""
echo "=== Next steps ==="
echo "1. Go to Portainer -> Stacks -> Add Stack"
echo "2. Name: janus-3cx-bridge"
echo "3. Select 'Upload' and choose ~/janus/docker-compose.yml"
echo "   OR use 'Repository' with path to docker-compose.yml"
echo "   OR copy-paste the content manually"
echo ""
echo "4. Or start manually with:"
echo "   cd ~/janus && docker-compose up -d"
echo ""
echo "5. Check logs with:"
echo "   docker logs -f janus-3cx-bridge"
echo ""
echo "=== Ports used ==="
echo "- 8188: WebSocket API (for browser)"
echo "- 8088: HTTP API"
echo "- 5060: SIP (UDP/TCP)"
echo "- 10000-10100: RTP media"
