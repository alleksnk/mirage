@echo off
setlocal
cd /d "%~dp0"

echo [1/4] Running Go unit tests...
go test . -v
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

if not exist build mkdir build

echo [2/4] Cross-compiling macOS amd64...
set GOOS=darwin
set GOARCH=amd64
set CGO_ENABLED=0
go build -ldflags="-s -w" -o build\mirage_amd64 .
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

echo [3/4] Cross-compiling macOS arm64 (Apple Silicon)...
set GOOS=darwin
set GOARCH=arm64
set CGO_ENABLED=0
go build -ldflags="-s -w" -o build\mirage_arm64 .
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

set GOOS=windows
set GOARCH=amd64

echo [4/4] Packaging signed Mirage.app + Mirage-macOS-v1.1.0.dmg...
go run ./cmd/mkdmg -amd64 build\mirage_amd64 -arm64 build\mirage_arm64 -tpws assets\tpws -icns assets\Mirage.icns -sites assets\sites.txt -out ..\release\macos\Mirage-macOS-v1.1.0.dmg
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

echo [SUCCESS] Built ..\release\macos\Mirage-macOS-v1.1.0.dmg

