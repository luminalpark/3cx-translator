#!/bin/bash
# Start SeamlessM4T Translation Server

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/server"

# Check if virtual environment exists
if [ ! -d "venv" ]; then
    echo "Creating virtual environment..."
    python3 -m venv venv
fi

# Activate virtual environment
source venv/bin/activate

# Install/update dependencies
echo "Installing dependencies..."
pip install -q -r requirements.txt

# Check CUDA
python3 -c "import torch; print(f'CUDA available: {torch.cuda.is_available()}')"
python3 -c "import torch; print(f'GPU: {torch.cuda.get_device_name(0) if torch.cuda.is_available() else \"None\"}')"

# Start server
echo ""
echo "Starting SeamlessM4T Translation Server..."
echo "URL: http://0.0.0.0:8000"
echo "WebSocket: ws://0.0.0.0:8000/ws/translate"
echo ""

python3 seamless_server.py
