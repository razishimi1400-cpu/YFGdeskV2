#!/bin/bash
set -euo pipefail

# ============================================================
#  YFG YASTA v2 — Linux Server Installation Script
#
#  Usage:
#    sudo ./install-linux.sh
#
#  Or specify the binary path:
#    sudo ./install-linux.sh /path/to/YfgDesk.Server
# ============================================================

INSTALL_DIR="/opt/yfgdesk-server"
DATA_DIR="/var/lib/yfgdesk"
SERVICE_FILE="/etc/systemd/system/yfgdesk-server.service"
SERVICE_NAME="yfgdesk-server"

# Determine architecture
ARCH=$(uname -m)
case "$ARCH" in
  x86_64)  RID="linux-x64" ;;
  aarch64) RID="linux-arm64" ;;
  *) echo "ERROR: Unsupported architecture: $ARCH"; exit 1 ;;
esac

echo "============================================================"
echo "  YFG YASTA v2 — Linux Server Installation"
echo "  Architecture: $ARCH ($RID)"
echo "============================================================"
echo ""

# Find the server binary
BINARY="${1:-}"
if [ -z "$BINARY" ]; then
  # Check common locations
  for candidate in \
    "./publish/server-$RID/YfgDesk.Server" \
    "./server/bin/Release/net8.0/$RID/YfgDesk.Server" \
    "./YfgDesk.Server"; do
    if [ -f "$candidate" ]; then
      BINARY="$candidate"
      break
    fi
  done
fi

if [ -z "$BINARY" ] || [ ! -f "$BINARY" ]; then
  echo "ERROR: YfgDesk.Server binary not found."
  echo ""
  echo "Build it first:"
  echo "  dotnet publish server/YfgDesk.Server.csproj -c Release -r $RID --self-contained true -p:PublishSingleFile=true -o ./publish/server-$RID"
  echo ""
  echo "Or specify the path:"
  echo "  sudo $0 /path/to/YfgDesk.Server"
  exit 1
fi

echo "Binary: $BINARY"
echo "Install dir: $INSTALL_DIR"
echo "Data dir: $DATA_DIR"
echo ""

# Check root
if [ "$EUID" -ne 0 ]; then
  echo "ERROR: Must run as root (use sudo)."
  exit 1
fi

# Create user
if ! id yfgdesk &>/dev/null; then
  echo "Creating user 'yfgdesk'..."
  useradd --system --no-create-home --shell /usr/sbin/nologin yfgdesk
fi

# Create directories
echo "Creating directories..."
mkdir -p "$INSTALL_DIR"
mkdir -p "$DATA_DIR"
chown yfgdesk:yfgdesk "$DATA_DIR"

# Copy binary + config
echo "Copying files..."
cp "$BINARY" "$INSTALL_DIR/YfgDesk.Server"
chmod +x "$INSTALL_DIR/YfgDesk.Server"

# Copy appsettings if available
for f in appsettings.json appsettings.Development.json; do
  DIR=$(dirname "$BINARY")
  if [ -f "$DIR/$f" ]; then
    cp "$DIR/$f" "$INSTALL_DIR/$f"
  fi
done

chown -R yfgdesk:yfgdesk "$INSTALL_DIR"

# Generate token secret
TOKEN_SECRET=$(openssl rand -base64 32)
echo "Generated token secret: $TOKEN_SECRET"

# Install systemd service
echo "Installing systemd service..."
SCRIPT_DIR=$(dirname "$0")
if [ -f "$SCRIPT_DIR/yfgdesk-server.service" ]; then
  cp "$SCRIPT_DIR/yfgdesk-server.service" "$SERVICE_FILE"
else
  # Generate inline
  cat > "$SERVICE_FILE" << EOF
[Unit]
Description=YFG YASTA Signaling Server
After=network.target
Wants=network.target

[Service]
Type=simple
User=yfgdesk
Group=yfgdesk
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/YfgDesk.Server
Restart=always
RestartSec=5
KillSignal=SIGINT
Environment=YFGDESK_DB=Data Source=$DATA_DIR/yfgdesk.db
Environment=YFGDESK_TOKEN_SECRET=$TOKEN_SECRET
Environment=ASPNETCORE_URLS=http://0.0.0.0:7070
Environment=ASPNETCORE_ENVIRONMENT=Production
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$DATA_DIR

[Install]
WantedBy=multi-user.target
EOF
fi

# Update the token secret in the service file
sed -i "s|REPLACE_WITH_BASE64_OF_AT_LEAST_32_RANDOM_BYTES|$TOKEN_SECRET|g" "$SERVICE_FILE"

# Reload systemd
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"

echo ""
echo "============================================================"
echo "  Installation complete!"
echo "============================================================"
echo ""
echo "  Start:   sudo systemctl start $SERVICE_NAME"
echo "  Stop:    sudo systemctl stop $SERVICE_NAME"
echo "  Status:  sudo systemctl status $SERVICE_NAME"
echo "  Logs:    sudo journalctl -u $SERVICE_NAME -f"
echo ""
echo "  Config:  $SERVICE_FILE"
echo "  Binary:  $INSTALL_DIR/YfgDesk.Server"
echo "  Data:    $DATA_DIR/yfgdesk.db"
echo ""
echo "  Token secret: $TOKEN_SECRET"
echo "  (saved in $SERVICE_FILE)"
echo ""
echo "  Firewall (if ufw is active):"
echo "    sudo ufw allow 7070/tcp"
echo ""
echo "  Health check:"
echo "    curl http://localhost:7070/api/health"
