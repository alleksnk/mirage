package main

import (
	"bufio"
	"crypto/rand"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	TpwsSocksPort  = 1080
	SmartProxyPort = 1085
)

type StrategyProfile struct {
	ID   string
	Args []string
}

var TpwsProfiles = []StrategyProfile{
	{ID: "tpws-disorder-sni", Args: []string{"--disorder", "--split-pos=1,sniext+1,host+1,midsld,endhost-1"}},
	{ID: "tpws-disorder-midsld", Args: []string{"--disorder", "--split-pos=1,midsld"}},
	{ID: "tpws-tlsrec-sni", Args: []string{"--tlsrec=sni", "--split-pos=1,midsld"}},
	{ID: "tpws-tlsrec-disorder", Args: []string{"--tlsrec=sni", "--disorder", "--split-pos=1"}},
	{ID: "tpws-oob-sni", Args: []string{"--oob", "--split-pos=1,sniext+1"}},
	{ID: "tpws-disorder-1", Args: []string{"--disorder", "--split-pos=1"}},
}

type dnsCacheEntry struct {
	ip      net.IP
	expires time.Time
}

type EngineState struct {
	Running       bool   `json:"running"`
	Probing       bool   `json:"probing"`
	StatusText    string `json:"statusText"`
	SubStatus     string `json:"subStatus"`
	StrategyLabel string `json:"strategyLabel"`
	ProxyPort     int    `json:"proxyPort"`
}

type EngineManager struct {
	mu            sync.Mutex
	running       bool
	probing       bool
	statusText    string
	subStatus     string
	strategyLabel string
	tpwsCmd       *exec.Cmd
	pacListener   net.Listener
	pacServer     *http.Server
	dnsMu         sync.Mutex
	dnsCache      map[string]dnsCacheEntry
	stopSeq       int64
}

var Engine = &EngineManager{
	statusText: "Обход DPI выключен",
	subStatus:  "Нажмите «Включить», чтобы обойти блокировки",
	dnsCache:   make(map[string]dnsCacheEntry),
}

func (e *EngineManager) State() EngineState {
	e.mu.Lock()
	defer e.mu.Unlock()
	p := 0
	if e.running {
		p = SmartProxyPort
	}
	return EngineState{
		Running:       e.running,
		Probing:       e.probing,
		StatusText:    e.statusText,
		SubStatus:     e.subStatus,
		StrategyLabel: e.strategyLabel,
		ProxyPort:     p,
	}
}

func findTpwsBinary() string {
	if exe, err := os.Executable(); err == nil {
		dir := filepath.Dir(exe)
		candidates := []string{
			filepath.Join(dir, "tpws"),
			filepath.Join(dir, "..", "Resources", "tpws"),
			filepath.Join(dir, "assets", "tpws"),
		}
		for _, c := range candidates {
			if st, err := os.Stat(c); err == nil && !st.IsDir() {
				return c
			}
		}
	}
	if _, err := os.Stat("assets/tpws"); err == nil {
		return "assets/tpws"
	}
	return ""
}

func (e *EngineManager) Start() error {
	e.mu.Lock()
	if e.running || e.probing {
		e.mu.Unlock()
		return nil
	}
	e.stopSeq++
	mySeq := e.stopSeq
	e.probing = true
	e.statusText = "Подбор рабочей стратегии"
	e.subStatus = "Тестируем профили tpws через Яндекс DNS (1253)..."
	e.mu.Unlock()

	EnsureAssetsPrepared()
	blacklistPath, err := WriteZapretBlacklist()
	if err != nil {
		e.setStopped("Ошибка подготовки списка сайтов")
		return err
	}

	tpwsPath := findTpwsBinary()
	if tpwsPath == "" {
		e.setStopped("Бинарник tpws не найден")
		return errors.New("tpws binary not found")
	}
	_ = os.Chmod(tpwsPath, 0o755)

	if err := e.startSmartProxy(); err != nil {
		e.setStopped("Ошибка запуска локального PAC-прокси")
		return err
	}

	settings := LoadSettings()
	var winner *StrategyProfile
	bestScore := -1

	if !settings.AutoProbe {
		cand := TpwsProfiles[0]
		if err := e.launchTpws(tpwsPath, cand, blacklistPath); err == nil {
			winner = &cand
		}
	} else {
		for _, cand := range TpwsProfiles {
			if e.isAborted(mySeq) {
				return nil
			}
			e.setProbing(fmt.Sprintf("Тест tpws (%s)...", cand.ID))
			if err := e.launchTpws(tpwsPath, cand, blacklistPath); err != nil {
				Logger.Write("TPWS", fmt.Sprintf("Failed %s: %v", cand.ID, err))
				continue
			}

			res := CheckAllPopularResources(SmartProxyPort, 2800*time.Millisecond)
			score := 0
			var ytOK, rutOK, trackerOK bool
			for _, r := range res {
				if !r.OK {
					continue
				}
				switch r.ID {
				case "youtube":
					ytOK = true
					score += 4
				case "rutor":
					rutOK = true
					score += 2
				case "rutracker":
					trackerOK = true
					score += 1
				}
			}
			Logger.Write("PROBE", fmt.Sprintf("[%s] score=%d (YT=%v Rutor=%v RuTracker=%v)", cand.ID, score, ytOK, rutOK, trackerOK))
			c := cand
			if score > bestScore {
				bestScore = score
				winner = &c
			}
			if score == 7 {
				break
			}
		}
		if winner != nil && bestScore < 7 && !e.isAborted(mySeq) {
			_ = e.launchTpws(tpwsPath, *winner, blacklistPath)
		}
	}

	if e.isAborted(mySeq) {
		return nil
	}
	if winner == nil {
		e.Stop()
		return errors.New("не удалось запустить tpws")
	}

	pacApplied := false
	if settings.AutoSetPac {
		pacApplied = applyMacOsPac(fmt.Sprintf("http://127.0.0.1:%d/proxy.pac?v=%d", SmartProxyPort, time.Now().UnixNano()))
	}

	e.mu.Lock()
	e.probing = false
	e.running = true
	e.strategyLabel = fmt.Sprintf("tpws (%s)", winner.ID)
	e.statusText = "Обход активен"
	if pacApplied {
		e.subStatus = fmt.Sprintf("%s • PAC 127.0.0.1:%d • Яндекс DNS %s:%d", e.strategyLabel, SmartProxyPort, settings.DnsHost, settings.DnsPort)
	} else {
		e.subStatus = fmt.Sprintf("%s • Прокси 127.0.0.1:%d • Яндекс DNS", e.strategyLabel, SmartProxyPort)
	}
	e.mu.Unlock()
	Logger.Write("OK", fmt.Sprintf("Обход активен (%s), systemPAC=%v", e.strategyLabel, pacApplied))
	return nil
}

func (e *EngineManager) Stop() {
	e.mu.Lock()
	e.stopSeq++
	e.probing = false
	e.running = false
	e.strategyLabel = ""
	e.statusText = "Обход DPI выключен"
	e.subStatus = "Нажмите «Включить», чтобы обойти блокировки"
	cmd := e.tpwsCmd
	e.tpwsCmd = nil
	srv := e.pacServer
	ln := e.pacListener
	e.pacServer = nil
	e.pacListener = nil
	e.mu.Unlock()

	clearMacOsPac()
	if cmd != nil && cmd.Process != nil {
		_ = cmd.Process.Kill()
		_, _ = cmd.Process.Wait()
	}
	if srv != nil {
		_ = srv.Close()
	}
	if ln != nil {
		_ = ln.Close()
	}
	Logger.Write("STOP", "Обход остановлен, системный PAC отключён")
}

func (e *EngineManager) isAborted(mySeq int64) bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.stopSeq != mySeq
}

func (e *EngineManager) setProbing(sub string) {
	e.mu.Lock()
	e.subStatus = sub
	e.mu.Unlock()
}

func (e *EngineManager) setStopped(reason string) {
	e.mu.Lock()
	e.probing = false
	e.running = false
	e.statusText = "Ошибка запуска"
	e.subStatus = reason
	e.mu.Unlock()
}

func (e *EngineManager) launchTpws(tpwsPath string, profile StrategyProfile, blacklistPath string) error {
	e.mu.Lock()
	old := e.tpwsCmd
	e.tpwsCmd = nil
	e.mu.Unlock()

	if old != nil && old.Process != nil {
		_ = old.Process.Kill()
		_, _ = old.Process.Wait()
	}

	args := []string{
		"--socks",
		"--bind-addr=127.0.0.1",
		fmt.Sprintf("--port=%d", TpwsSocksPort),
		"--hostlist=" + blacklistPath,
	}
	args = append(args, profile.Args...)

	cmd := exec.Command(tpwsPath, args...)
	if err := cmd.Start(); err != nil {
		return err
	}

	e.mu.Lock()
	e.tpwsCmd = cmd
	e.mu.Unlock()

	go func(c *exec.Cmd) { _ = c.Wait() }(cmd)

	deadline := time.Now().Add(1800 * time.Millisecond)
	for time.Now().Before(deadline) {
		conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", TpwsSocksPort), 150*time.Millisecond)
		if err == nil {
			_ = conn.Close()
			return nil
		}
		time.Sleep(60 * time.Millisecond)
	}
	return errors.New("tpws failed to open SOCKS port")
}

func (e *EngineManager) startSmartProxy() error {
	e.mu.Lock()
	already := e.pacListener != nil
	e.mu.Unlock()
	if already {
		return nil
	}

	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", SmartProxyPort))
	if err != nil {
		return err
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/proxy.pac", func(w http.ResponseWriter, r *http.Request) {
		domains := FilterHostlistLines(ReadSitesRaw())
		w.Header().Set("Content-Type", "application/x-ns-proxy-autoconfig; charset=utf-8")
		w.Header().Set("Cache-Control", "no-store")
		_, _ = w.Write([]byte(BuildPacScript(domains, SmartProxyPort)))
	})

	srv := &http.Server{
		Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.Method == http.MethodConnect {
				e.handleConnectProxy(w, r)
				return
			}
			if r.URL.Path == "/proxy.pac" && r.Host != "" && strings.HasPrefix(r.Host, "127.0.0.1") {
				mux.ServeHTTP(w, r)
				return
			}
			e.handleHttpProxy(w, r)
		}),
	}

	e.mu.Lock()
	e.pacListener = ln
	e.pacServer = srv
	e.mu.Unlock()

	go func() {
		if err := srv.Serve(ln); err != nil && !errors.Is(err, http.ErrServerClosed) {
			Logger.Write("PAC-ERR", err.Error())
		}
	}()
	return nil
}

func (e *EngineManager) handleConnectProxy(w http.ResponseWriter, r *http.Request) {
	hj, ok := w.(http.Hijacker)
	if !ok {
		http.Error(w, "hijack unsupported", http.StatusInternalServerError)
		return
	}
	clientConn, _, err := hj.Hijack()
	if err != nil {
		return
	}
	defer clientConn.Close()

	host, portStr, err := net.SplitHostPort(r.Host)
	if err != nil {
		host = r.Host
		portStr = "443"
	}
	port, err := strconv.Atoi(portStr)
	if err != nil || port <= 0 || port > 65535 {
		port = 443
	}

	upConn, err := e.dialViaTpws(host, port)
	if err != nil {
		_, _ = clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\n\r\n"))
		return
	}
	defer upConn.Close()

	_, _ = clientConn.Write([]byte("HTTP/1.1 200 Connection Established\r\n\r\n"))
	relayConns(clientConn, upConn)
}

func (e *EngineManager) handleHttpProxy(w http.ResponseWriter, r *http.Request) {
	host := r.URL.Hostname()
	if host == "" {
		http.Error(w, "bad proxy request", http.StatusBadRequest)
		return
	}
	port := 80
	if pStr := r.URL.Port(); pStr != "" {
		if p, err := strconv.Atoi(pStr); err == nil {
			port = p
		}
	}
	upConn, err := e.dialViaTpws(host, port)
	if err != nil {
		http.Error(w, "502 Bad Gateway", http.StatusBadGateway)
		return
	}
	defer upConn.Close()

	outReq := r.Clone(r.Context())
	outReq.RequestURI = ""
	outReq.Header.Del("Proxy-Connection")
	if err := outReq.Write(upConn); err != nil {
		http.Error(w, "502 Bad Gateway", http.StatusBadGateway)
		return
	}
	resp, err := http.ReadResponse(bufio.NewReader(upConn), outReq)
	if err != nil {
		http.Error(w, "502 Bad Gateway", http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()
	for k, vals := range resp.Header {
		for _, v := range vals {
			w.Header().Add(k, v)
		}
	}
	w.WriteHeader(resp.StatusCode)
	_, _ = io.Copy(w, resp.Body)
}

func (e *EngineManager) dialViaTpws(host string, port int) (net.Conn, error) {
	ip := e.resolveCleanIPv4(host)
	if ip == nil {
		return nil, fmt.Errorf("dns resolution failed for %s", host)
	}
	ip4 := ip.To4()
	if ip4 == nil {
		return nil, fmt.Errorf("non-ipv4 resolved for %s", host)
	}

	sock, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", TpwsSocksPort), 4*time.Second)
	if err != nil {
		return nil, err
	}
	_ = sock.SetDeadline(time.Now().Add(6 * time.Second))

	if _, err := sock.Write([]byte{0x05, 0x01, 0x00}); err != nil {
		_ = sock.Close()
		return nil, err
	}
	var greet [2]byte
	if _, err := io.ReadFull(sock, greet[:]); err != nil || greet[1] != 0x00 {
		_ = sock.Close()
		return nil, errors.New("socks5 greeting failed")
	}

	req := []byte{
		0x05, 0x01, 0x00, 0x01,
		ip4[0], ip4[1], ip4[2], ip4[3],
		byte(port >> 8), byte(port & 0xff),
	}
	if _, err := sock.Write(req); err != nil {
		_ = sock.Close()
		return nil, err
	}

	var repHead [4]byte
	if _, err := io.ReadFull(sock, repHead[:]); err != nil || repHead[1] != 0x00 {
		_ = sock.Close()
		return nil, errors.New("socks5 connect failed")
	}
	switch repHead[3] {
	case 0x01:
		var tail [6]byte
		_, _ = io.ReadFull(sock, tail[:])
	case 0x04:
		var tail [18]byte
		_, _ = io.ReadFull(sock, tail[:])
	case 0x03:
		var l [1]byte
		if _, err := io.ReadFull(sock, l[:]); err == nil {
			tail := make([]byte, int(l[0])+2)
			_, _ = io.ReadFull(sock, tail)
		}
	}
	_ = sock.SetDeadline(time.Time{})
	return sock, nil
}

func (e *EngineManager) resolveCleanIPv4(host string) net.IP {
	if ip := net.ParseIP(host); ip != nil {
		return ip.To4()
	}
	key := strings.ToLower(strings.TrimSpace(host))

	e.dnsMu.Lock()
	if ent, ok := e.dnsCache[key]; ok && time.Now().Before(ent.expires) {
		e.dnsMu.Unlock()
		return ent.ip
	}
	e.dnsMu.Unlock()

	s := LoadSettings()
	primary := fmt.Sprintf("%s:%d", s.DnsHost, s.DnsPort)
	for _, server := range []string{primary, "8.8.8.8:53"} {
		if ip := queryUdpDnsA(key, server, 1800*time.Millisecond); ip != nil {
			e.dnsMu.Lock()
			e.dnsCache[key] = dnsCacheEntry{ip: ip, expires: time.Now().Add(5 * time.Minute)}
			e.dnsMu.Unlock()
			return ip
		}
	}

	if addrs, err := net.LookupIP(key); err == nil {
		for _, a := range addrs {
			if v4 := a.To4(); v4 != nil {
				return v4
			}
		}
	}
	return nil
}

func queryUdpDnsA(domain, server string, timeout time.Duration) net.IP {
	query := BuildDnsQuery(domain, 1)
	if len(query) == 0 {
		return nil
	}
	conn, err := net.DialTimeout("udp", server, timeout)
	if err != nil {
		return nil
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(timeout))
	if _, err := conn.Write(query); err != nil {
		return nil
	}
	buf := make([]byte, 1500)
	n, err := conn.Read(buf)
	if err != nil || n < 12 {
		return nil
	}
	return ParseDnsAResponse(buf[:n])
}

func BuildDnsQuery(domain string, qtype uint16) []byte {
	domain = strings.TrimSuffix(strings.TrimSpace(domain), ".")
	if domain == "" {
		return nil
	}
	var id [2]byte
	_, _ = rand.Read(id[:])
	pkt := []byte{id[0], id[1], 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00}
	for _, label := range strings.Split(domain, ".") {
		if len(label) == 0 || len(label) > 63 {
			return nil
		}
		pkt = append(pkt, byte(len(label)))
		pkt = append(pkt, []byte(label)...)
	}
	pkt = append(pkt, 0x00, byte(qtype>>8), byte(qtype), 0x00, 0x01)
	return pkt
}

func ParseDnsAResponse(resp []byte) net.IP {
	if len(resp) < 12 {
		return nil
	}
	qdCount := int(binary.BigEndian.Uint16(resp[4:6]))
	anCount := int(binary.BigEndian.Uint16(resp[6:8]))
	pos := 12

	skipName := func(p int) (int, bool) {
		jumps := 0
		for p < len(resp) {
			l := resp[p]
			if l == 0 {
				return p + 1, true
			}
			if l&0xC0 == 0xC0 {
				if p+1 >= len(resp) {
					return 0, false
				}
				return p + 2, true
			}
			p += 1 + int(l)
			jumps++
			if jumps > 64 {
				return 0, false
			}
		}
		return 0, false
	}

	var ok bool
	for i := 0; i < qdCount; i++ {
		pos, ok = skipName(pos)
		if !ok || pos+4 > len(resp) {
			return nil
		}
		pos += 4
	}
	for i := 0; i < anCount; i++ {
		pos, ok = skipName(pos)
		if !ok || pos+10 > len(resp) {
			return nil
		}
		rtype := binary.BigEndian.Uint16(resp[pos : pos+2])
		rdlen := int(binary.BigEndian.Uint16(resp[pos+8 : pos+10]))
		pos += 10
		if pos+rdlen > len(resp) {
			return nil
		}
		if rtype == 1 && rdlen == 4 {
			return net.IPv4(resp[pos], resp[pos+1], resp[pos+2], resp[pos+3])
		}
		pos += rdlen
	}
	return nil
}

func BuildPacScript(domains []string, proxyPort int) string {
	var b strings.Builder
	b.WriteString("function FindProxyForURL(url, host) {\n")
	b.WriteString("  var h = host.toLowerCase();\n")
	b.WriteString("  if (isPlainHostName(h) || shExpMatch(h, '127.*') || shExpMatch(h, '10.*') || shExpMatch(h, '192.168.*')) return 'DIRECT';\n")
	b.WriteString("  var list = [\n")
	for i, d := range domains {
		if i > 0 {
			b.WriteString(",\n")
		}
		b.WriteString(fmt.Sprintf("    %q", d))
	}
	b.WriteString("\n  ];\n")
	b.WriteString("  for (var i = 0; i < list.length; i++) {\n")
	b.WriteString("    var d = list[i];\n")
	b.WriteString("    if (h === d || (h.length > d.length && h.slice(-(d.length + 1)) === '.' + d)) {\n")
	b.WriteString(fmt.Sprintf("      return 'PROXY 127.0.0.1:%d';\n", proxyPort))
	b.WriteString("    }\n")
	b.WriteString("  }\n")
	b.WriteString("  return 'DIRECT';\n")
	b.WriteString("}\n")
	return b.String()
}

func relayConns(a, b net.Conn) {
	var wg sync.WaitGroup
	wg.Add(2)
	cp := func(dst, src net.Conn) {
		defer wg.Done()
		_, _ = io.Copy(dst, src)
		if tc, ok := dst.(*net.TCPConn); ok {
			_ = tc.CloseWrite()
		}
	}
	go cp(b, a)
	go cp(a, b)
	wg.Wait()
}

func listMacNetworkServices() []string {
	if runtime.GOOS != "darwin" {
		return nil
	}
	out, err := exec.Command("networksetup", "-listallnetworkservices").Output()
	if err != nil {
		return nil
	}
	var res []string
	for idx, line := range strings.Split(string(out), "\n") {
		if idx == 0 {
			continue
		}
		s := strings.TrimSpace(line)
		if s == "" || strings.HasPrefix(s, "*") {
			continue
		}
		res = append(res, s)
	}
	return res
}

func applyMacOsPac(pacURL string) bool {
	if runtime.GOOS != "darwin" {
		return false
	}
	svcs := listMacNetworkServices()
	applied := false
	for _, s := range svcs {
		if exec.Command("networksetup", "-setautoproxyurl", s, pacURL).Run() == nil {
			_ = exec.Command("networksetup", "-setautoproxystate", s, "on").Run()
			applied = true
		}
	}
	return applied
}

func clearMacOsPac() {
	if runtime.GOOS != "darwin" {
		return
	}
	for _, s := range listMacNetworkServices() {
		_ = exec.Command("networksetup", "-setautoproxystate", s, "off").Run()
	}
}

