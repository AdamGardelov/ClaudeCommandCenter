#!/usr/bin/env bash
set -euo pipefail

REPO="AdamGardelov/ClaudeCommandCenter"
INSTALL_DIR="/usr/local/bin"
BINARY="ccc"

# Detect platform
OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS" in
  Linux)  RID="linux-x64" ;;
  Darwin)
    case "$ARCH" in
      arm64) RID="osx-arm64" ;;
      *)     RID="osx-x64" ;;
    esac
    ;;
  *) echo "Unsupported OS: $OS" >&2; exit 1 ;;
esac

echo "Detected platform: $RID"

# Get latest version
LATEST=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | grep '"tag_name"' | cut -d'"' -f4)
echo "Latest version: $LATEST"

# Download and extract
TMPDIR=$(mktemp -d)
trap 'rm -rf "$TMPDIR"' EXIT

ARCHIVE="ccc-${RID}.tar.gz"
URL="https://github.com/$REPO/releases/download/${LATEST}/${ARCHIVE}"

echo "Downloading $URL..."
curl -fsSL "$URL" -o "$TMPDIR/$ARCHIVE"
tar -xzf "$TMPDIR/$ARCHIVE" -C "$TMPDIR"

# Install
if [ -w "$INSTALL_DIR" ]; then
  cp "$TMPDIR/$BINARY" "$INSTALL_DIR/$BINARY"
else
  echo "Installing to $INSTALL_DIR (requires sudo)..."
  sudo cp "$TMPDIR/$BINARY" "$INSTALL_DIR/$BINARY"
fi

chmod +x "$INSTALL_DIR/$BINARY"

echo "Installed $BINARY $LATEST to $INSTALL_DIR/$BINARY"
