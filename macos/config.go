package main

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

//go:embed assets/sites.txt
var defaultSitesTxt string

const (
	tgPrimaryURL    = "tg://proxy?server=156.239.251.46&port=443&secret=eeff2a510da3958357083ec111f4de115f7777772e6d6963726f736f66742e636f6d"
	tgBackup1URL    = "tg://proxy?server=89.108.71.59&port=443&secret=ee00d85706e58f9b5ead3b6c8a08d984df636c6f75642e6d61696c2e7275"
	tgBackup2URL    = "tg://proxy?server=77.mishutkin.click&port=443&secret=eec8017bc0cd6ea4b51ca268fb7b3aba6237372e6d69736875746b696e2e636c69636b"
	tgLocalSocksURL = "tg://socks?server=127.0.0.1&port=1080"
)

var excludedRootDomains = map[string]bool{
	"googleapis.com": true,
	"github.com":     true,
}

type AppSettings struct {
	DnsHost          string `json:"dnsHost"`
	DnsPort          int    `json:"dnsPort"`
	AutoProbe        bool   `json:"autoProbe"`
	AutoSetPac       bool   `json:"autoSetPac"`
	PreferredProfile string `json:"preferredProfile"`
}

func defaultSettings() AppSettings {
	return AppSettings{
		DnsHost:    "77.88.8.8",
		DnsPort:    1253,
		AutoProbe:  true,
		AutoSetPac: true,
	}
}

func appSupportDir() string {
	home, err := os.UserHomeDir()
	if err != nil || home == "" {
		return filepath.Join(os.TempDir(), "Mirage")
	}
	dir := filepath.Join(home, "Library", "Application Support", "Mirage")
	_ = os.MkdirAll(dir, 0o755)
	return dir
}

func sitesFilePath() string {
	return filepath.Join(appSupportDir(), "sites.txt")
}

func blacklistFilePath() string {
	return filepath.Join(appSupportDir(), "gdpi_blacklist.txt")
}

func settingsFilePath() string {
	return filepath.Join(appSupportDir(), "settings.json")
}

func logFilePath() string {
	return filepath.Join(appSupportDir(), "mirage_macos.log")
}

func EnsureAssetsPrepared() {
	p := sitesFilePath()
	if _, err := os.Stat(p); os.IsNotExist(err) {
		_ = os.WriteFile(p, []byte(defaultSitesTxt), 0o644)
	}
}

func ReadSitesRaw() string {
	EnsureAssetsPrepared()
	b, err := os.ReadFile(sitesFilePath())
	if err != nil {
		return defaultSitesTxt
	}
	return string(b)
}

func SaveSitesRaw(raw string) error {
	if err := os.WriteFile(sitesFilePath(), []byte(raw), 0o644); err != nil {
		return err
	}
	_, err := WriteZapretBlacklist()
	return err
}

func ResetSitesRaw() (string, error) {
	if err := SaveSitesRaw(defaultSitesTxt); err != nil {
		return "", err
	}
	return defaultSitesTxt, nil
}

func FilterHostlistLines(raw string) []string {
	seen := make(map[string]bool)
	var out []string
	for _, line := range strings.Split(strings.ReplaceAll(raw, "\r\n", "\n"), "\n") {
		d := strings.TrimSpace(strings.ToLower(line))
		if d == "" || strings.HasPrefix(d, "#") {
			continue
		}
		d = strings.TrimPrefix(d, "https://")
		d = strings.TrimPrefix(d, "http://")
		d = strings.TrimSuffix(d, "/")
		if excludedRootDomains[d] || seen[d] {
			continue
		}
		seen[d] = true
		out = append(out, d)
	}
	return out
}

func WriteZapretBlacklist() (string, error) {
	domains := FilterHostlistLines(ReadSitesRaw())
	content := strings.Join(domains, "\n") + "\n"
	target := blacklistFilePath()
	return target, os.WriteFile(target, []byte(content), 0o644)
}

func LoadSettings() AppSettings {
	s := defaultSettings()
	b, err := os.ReadFile(settingsFilePath())
	if err != nil {
		return s
	}
	_ = json.Unmarshal(b, &s)
	if strings.TrimSpace(s.DnsHost) == "" {
		s.DnsHost = "77.88.8.8"
	}
	if s.DnsPort <= 0 || s.DnsPort > 65535 {
		s.DnsPort = 1253
	}
	return s
}

func SaveSettings(s AppSettings) error {
	if strings.TrimSpace(s.DnsHost) == "" {
		s.DnsHost = "77.88.8.8"
	}
	if s.DnsPort <= 0 || s.DnsPort > 65535 {
		s.DnsPort = 1253
	}
	b, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(settingsFilePath(), b, 0o644)
}

type RuntimeLogger struct {
	mu    sync.Mutex
	lines []string
}

var Logger = &RuntimeLogger{}

func (l *RuntimeLogger) Write(tag, msg string) {
	line := fmt.Sprintf("[%s] [%s] %s", time.Now().Format("15:04:05"), tag, msg)
	l.mu.Lock()
	l.lines = append(l.lines, line)
	if len(l.lines) > 250 {
		l.lines = l.lines[len(l.lines)-250:]
	}
	l.mu.Unlock()

	if f, err := os.OpenFile(logFilePath(), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644); err == nil {
		_, _ = f.WriteString(line + "\n")
		_ = f.Close()
	}
}

func (l *RuntimeLogger) Snapshot() string {
	l.mu.Lock()
	defer l.mu.Unlock()
	if len(l.lines) == 0 {
		return "Журнал пуст"
	}
	return strings.Join(l.lines, "\n")
}

func (l *RuntimeLogger) Clear() {
	l.mu.Lock()
	l.lines = nil
	l.mu.Unlock()
	_ = os.WriteFile(logFilePath(), nil, 0o644)
}

