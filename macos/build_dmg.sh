#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

echo "[1/4] Running Go unit tests..."
go test . -count=1 -v

mkdir -p build

echo "[2/4] Building macOS amd64 (native WebView)..."
CGO_ENABLED=1 GOOS=darwin GOARCH=amd64 go build -ldflags="-s -w" -o build/mirage_amd64 .

echo "[3/4] Building macOS arm64 (Apple Silicon, native WebView)..."
CGO_ENABLED=1 GOOS=darwin GOARCH=arm64 go build -ldflags="-s -w" -o build/mirage_arm64 .

echo "[4/4] Packaging signed Mirage.app + Mirage-macOS-v1.1.0.dmg..."
go run ./cmd/mkdmg \
  -amd64 build/mirage_amd64 \
  -arm64 build/mirage_arm64 \
  -tpws assets/tpws \
  -icns assets/Mirage.icns \
  -sites assets/sites.txt \
  -out ../release/macos/Mirage-macOS-v1.1.0.dmg

echo "[SUCCESS] Built ../release/macos/Mirage-macOS-v1.1.0.dmg"

