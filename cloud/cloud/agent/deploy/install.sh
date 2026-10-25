#!/usr/bin/env bash

# Amazon Linux 2023 bootstrap for ACC Shooter dedicated‑server agent
# This script is deliberately defensive – it aborts with a clear error message
# if any required component is missing.

set -euo pipefail

# Helper for error messages
error() {
    echo "[install.sh] ERROR: $*" >&2
    exit 1
}

# -------------------------------------------------------------------
# Verify we are running on Amazon Linux 2023 (or compatible)
# ---------------------------------------------------------------
if [[ -f /etc/os-release ]]; then
    . /etc/os-release
    if [[ "$ID" != "amzn" && "$ID" != "amazon" ]]; then
        echo "[install.sh] WARNING: Detected OS $ID – script is intended for Amazon Linux 2023 but may still work."
    fi
fi

# -------------------------------------------------------------------
# Load environment configuration (must exist)
# ---------------------------------------------------------------
ENV_FILE="/etc/acc-agent.env"
if [[ ! -f "$ENV_FILE" ]]; then
    error "Environment file $ENV_FILE not found. Copy acc-agent.env.example to $ENV_FILE and edit the values."
fi

# Export variables for the remainder of the script (but do not export empty SERVER_BINARY)
set -a
. "$ENV_FILE"
set +a

# -------------------------------------------------------------------
# Validate required variables
# ---------------------------------------------------------------
: "${API_URL:?Missing API_URL in $ENV_FILE}"
: "${AWS_REGION:?Missing AWS_REGION in $ENV_FILE}"
: "${GAME_PORT:?Missing GAME_PORT in $ENV_FILE}"
: "${CAPACITY:?Missing CAPACITY in $ENV_FILE}"
: "${SERVER_BINARY:?Missing SERVER_BINARY in $ENV_FILE}"
: "${AGENT_PORT:?Missing AGENT_PORT in $ENV_FILE}"

# SERVER_BINARY must point to an existing executable
if [[ ! -x "$SERVER_BINARY" ]]; then
    error "Server binary $SERVER_BINARY does not exist or is not executable. Build the Linux server binary (ACCServer.x86_64) and place it at this path."
fi

# -------------------------------------------------------------------
# Install Python dependencies (optional requirements.txt)
# ---------------------------------------------------------------
REQ_FILE="$(dirname "${BASH_SOURCE[0]}")/../requirements.txt"
if [[ -f "$REQ_FILE" ]]; then
    echo "[install.sh] Installing Python requirements from $REQ_FILE"
    pip3 install -r "$REQ_FILE"
else
    echo "[install.sh] No requirements.txt found at $REQ_FILE – installing minimal dependencies"
    pip3 install --quiet boto3 botocore
fi

# -------------------------------------------------------------------
# Create deployment directory and copy files
# ---------------------------------------------------------------
TARGET_DIR="/opt/acc"
mkdir -p "$TARGET_DIR"

# Copy the agent source (agent.py) and the server binary
cp "$(dirname "${BASH_SOURCE[0]}")/../agent.py" "$TARGET_DIR/agent.py"
cp "$SERVER_BINARY" "$TARGET_DIR/"
# Determine binary name and make executable
BINARY_NAME="$(basename "$SERVER_BINARY")"
chmod +x "$TARGET_DIR/$BINARY_NAME"

# Copy accompanying data folder if it exists
DATA_DIR="${SERVER_BINARY%.*}_Data"
if [[ -d "$DATA_DIR" ]]; then
    cp -r "$DATA_DIR" "$TARGET_DIR/"
else
    echo "[install.sh] WARNING: Data folder $DATA_DIR not found; proceeding without it"
fi

# Copy any required shared libraries (*.so) from the same directory as the binary
SOURCE_DIR="$(dirname "$SERVER_BINARY")"
if compgen -G "$SOURCE_DIR/*.so" > /dev/null; then
    cp "$SOURCE_DIR"/*.so "$TARGET_DIR/"
else
    echo "[install.sh] WARNING: No .so files found in $SOURCE_DIR"
fi

# Install the systemd unit file
UNIT_FILE="/etc/systemd/system/acc-agent.service"
cat <<'EOF' > "$UNIT_FILE"
[Unit]
Description=ACC Shooter dedicated‑server agent
After=network.target

[Service]
Type=simple
EnvironmentFile=/etc/acc-agent.env
ExecStart=/usr/bin/python3 /opt/acc/agent.py \
    --api-url $API_URL \
    --region $AWS_REGION \
    --public-address $PUBLIC_ADDRESS \
    --game-port $GAME_PORT \
    --capacity $CAPACITY \
    --server-binary $SERVER_BINARY \
    --loopback-port $AGENT_PORT \
    --heartbeat-interval $HEARTBEAT_INTERVAL \
    --log-file $SERVER_LOG
Restart=on-failure
RestartSec=5
StandardOutput=journal
StandardError=journal
User=root

[Install]
WantedBy=multi-user.target
EOF

# Reload systemd and enable the service
systemctl daemon-reload
systemctl enable --now acc-agent.service

echo "[install.sh] Installation complete. Service status:" && systemctl status acc-agent.service --no-pager
