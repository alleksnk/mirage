package main

import (
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/exec"
	"os/signal"
	"runtime"
	"strings"
	"syscall"
	"time"
)

func main() {
	EnsureAssetsPrepared()
	Logger.Write("INIT", fmt.Sprintf("Mirage macOS started (%s/%s)", runtime.GOOS, runtime.GOARCH))

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		Logger.Write("ERR", "UI listen failed: "+err.Error())
		return
	}
	uiURL := fmt.Sprintf("http://%s", ln.Addr().String())

	quitCh := make(chan struct{})
	mux := buildHttpHandler(quitCh)

	srv := &http.Server{Handler: mux}
	go func() { _ = srv.Serve(ln) }()

	// Auto-start engine in background so bypass starts immediately
	go func() {
		if err := Engine.Start(); err != nil {
			Logger.Write("ERR", err.Error())
		}
	}()

	openMacAppWindow(uiURL)

	sigCh := make(chan os.Signal, 2)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM, syscall.SIGHUP)
	select {
	case <-sigCh:
	case <-quitCh:
	}

	Engine.Stop()
	_ = srv.Close()
}

func buildHttpHandler(quitCh chan struct{}) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		_, _ = w.Write([]byte(mirageHtmlUI))
	})

	mux.HandleFunc("/api/state", func(w http.ResponseWriter, r *http.Request) {
		writeJson(w, Engine.State())
	})

	mux.HandleFunc("/api/toggle", func(w http.ResponseWriter, r *http.Request) {
		st := Engine.State()
		if st.Running || st.Probing {
			go Engine.Stop()
		} else {
			go func() { _ = Engine.Start() }()
		}
		writeJson(w, map[string]bool{"ok": true})
	})

	mux.HandleFunc("/api/check", func(w http.ResponseWriter, r *http.Request) {
		st := Engine.State()
		items := CheckAllPopularResources(st.ProxyPort, 4*time.Second)
		writeJson(w, map[string]any{"items": items})
	})

	mux.HandleFunc("/api/config", func(w http.ResponseWriter, r *http.Request) {
		writeJson(w, map[string]any{
			"sites":    ReadSitesRaw(),
			"settings": LoadSettings(),
			"tgLinks": map[string]string{
				"primary": tgPrimaryURL,
				"backup1": tgBackup1URL,
				"backup2": tgBackup2URL,
				"socks":   tgLocalSocksURL,
			},
		})
	})

	mux.HandleFunc("/api/sites", func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Sites string `json:"sites"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		_ = SaveSitesRaw(body.Sites)
		Logger.Write("SITES", "Список сайтов обновлён")
		writeJson(w, map[string]bool{"ok": true})
	})

	mux.HandleFunc("/api/sites/add", func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Domain string `json:"domain"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		d := strings.TrimSpace(strings.ToLower(body.Domain))
		d = strings.TrimPrefix(d, "https://")
		d = strings.TrimPrefix(d, "http://")
		d = strings.TrimSuffix(d, "/")
		raw := ReadSitesRaw()
		if d != "" && !strings.Contains(raw, d) {
			raw = strings.TrimRight(raw, "\r\n") + "\n" + d + "\n"
			_ = SaveSitesRaw(raw)
			Logger.Write("SITES", "Добавлен домен: "+d)
		}
		writeJson(w, map[string]string{"sites": raw})
	})

	mux.HandleFunc("/api/sites/reset", func(w http.ResponseWriter, r *http.Request) {
		s, _ := ResetSitesRaw()
		writeJson(w, map[string]string{"sites": s})
	})

	mux.HandleFunc("/api/settings", func(w http.ResponseWriter, r *http.Request) {
		var s AppSettings
		if err := json.NewDecoder(r.Body).Decode(&s); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		_ = SaveSettings(s)
		Logger.Write("SET", fmt.Sprintf("Настройки сохранены (DNS %s:%d, autoProbe=%v, autoPac=%v)", s.DnsHost, s.DnsPort, s.AutoProbe, s.AutoSetPac))
		writeJson(w, map[string]bool{"ok": true})
	})

	mux.HandleFunc("/api/tg/open", func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Key string `json:"key"`
		}
		_ = json.NewDecoder(r.Body).Decode(&body)
		urlMap := map[string]string{
			"primary": tgPrimaryURL,
			"backup1": tgBackup1URL,
			"backup2": tgBackup2URL,
			"socks":   tgLocalSocksURL,
		}
		target := urlMap[body.Key]
		if target == "" {
			target = tgPrimaryURL
		}
		Logger.Write("TG", "Открытие ссылки: "+target)
		if runtime.GOOS == "darwin" {
			_ = exec.Command("open", target).Start()
		}
		writeJson(w, map[string]bool{"ok": true})
	})

	mux.HandleFunc("/api/logs", func(w http.ResponseWriter, r *http.Request) {
		writeJson(w, map[string]string{"logs": Logger.Snapshot()})
	})

	mux.HandleFunc("/api/logs/clear", func(w http.ResponseWriter, r *http.Request) {
		Logger.Clear()
		writeJson(w, map[string]bool{"ok": true})
	})

	mux.HandleFunc("/api/quit", func(w http.ResponseWriter, r *http.Request) {
		writeJson(w, map[string]bool{"ok": true})
		go func() {
			time.Sleep(120 * time.Millisecond)
			close(quitCh)
		}()
	})

	return mux
}

func writeJson(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_ = json.NewEncoder(w).Encode(v)
}

func openMacAppWindow(url string) {
	if runtime.GOOS != "darwin" {
		return
	}
	browsers := []string{
		"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
		"/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
		"/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
		"/Applications/Chromium.app/Contents/MacOS/Chromium",
	}
	for _, b := range browsers {
		if _, err := os.Stat(b); err == nil {
			_ = exec.Command(b, "--app="+url, "--window-size=560,680").Start()
			return
		}
	}
	_ = exec.Command("open", url).Start()
}

