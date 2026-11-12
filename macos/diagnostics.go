package main

import (
	"crypto/tls"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"sync"
	"time"
)

type ResourceTarget struct {
	ID              string `json:"id"`
	Name            string `json:"name"`
	URL             string `json:"url"`
	IsTelegramProxy bool   `json:"isTelegramProxy"`
}

type ResourceCheckResult struct {
	ID         string `json:"id"`
	Name       string `json:"name"`
	OK         bool   `json:"ok"`
	ElapsedMs  int64  `json:"elapsedMs"`
	StatusText string `json:"statusText"`
}

var PopularResources = []ResourceTarget{
	{ID: "youtube", Name: "YouTube", URL: "https://www.youtube.com"},
	{ID: "instagram", Name: "Instagram", URL: "https://www.instagram.com"},
	{ID: "discord", Name: "Discord", URL: "https://discord.com"},
	{ID: "telegram", Name: "Telegram", URL: "156.239.251.46:443", IsTelegramProxy: true},
	{ID: "rutracker", Name: "RuTracker", URL: "https://rutracker.org"},
	{ID: "rutor", Name: "Rutor.info", URL: "https://rutor.info"},
	{ID: "x", Name: "X (Twitter)", URL: "https://x.com"},
	{ID: "medium", Name: "Medium", URL: "https://medium.com"},
}

func CheckAllPopularResources(proxyPort int, timeout time.Duration) []ResourceCheckResult {
	results := make([]ResourceCheckResult, len(PopularResources))
	var wg sync.WaitGroup
	for i, t := range PopularResources {
		wg.Add(1)
		go func(idx int, target ResourceTarget) {
			defer wg.Done()
			if target.IsTelegramProxy {
				results[idx] = probeTelegramMtProxy(target)
			} else {
				results[idx] = probeWebResource(target, proxyPort, timeout)
			}
		}(i, t)
	}
	wg.Wait()
	return results
}

func probeWebResource(target ResourceTarget, proxyPort int, timeout time.Duration) ResourceCheckResult {
	start := time.Now()
	tr := &http.Transport{
		TLSClientConfig:     &tls.Config{InsecureSkipVerify: true},
		DisableKeepAlives:   true,
		TLSHandshakeTimeout: timeout,
	}
	if proxyPort > 0 {
		proxyURL, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", proxyPort))
		tr.Proxy = http.ProxyURL(proxyURL)
	}
	client := &http.Client{
		Transport: tr,
		Timeout:   timeout,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}

	req, err := http.NewRequest(http.MethodGet, target.URL, nil)
	if err != nil {
		return ResourceCheckResult{ID: target.ID, Name: target.Name, StatusText: "Ошибка"}
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/537.36 Chrome/128.0.0.0 Safari/537.36")

	resp, err := client.Do(req)
	ms := time.Since(start).Milliseconds()
	if err != nil {
		var netErr net.Error
		msg := "Недоступен"
		if errors.As(err, &netErr) && netErr.Timeout() {
			msg = "Таймаут"
		}
		Logger.Write("DIAG", fmt.Sprintf("%s (%s) viaProxy=%v -> FAIL (%v, %dms)", target.Name, target.URL, proxyPort > 0, err, ms))
		return ResourceCheckResult{ID: target.ID, Name: target.Name, OK: false, ElapsedMs: ms, StatusText: msg}
	}
	_ = resp.Body.Close()

	if resp.StatusCode == http.StatusBadGateway || resp.StatusCode == http.StatusGatewayTimeout {
		Logger.Write("DIAG", fmt.Sprintf("%s (%s) -> FAIL HTTP %d (%dms)", target.Name, target.URL, resp.StatusCode, ms))
		return ResourceCheckResult{ID: target.ID, Name: target.Name, OK: false, ElapsedMs: ms, StatusText: "Недоступен"}
	}

	Logger.Write("DIAG", fmt.Sprintf("%s (%s) -> OK(%d, %dms)", target.Name, target.URL, resp.StatusCode, ms))
	return ResourceCheckResult{
		ID:         target.ID,
		Name:       target.Name,
		OK:         true,
		ElapsedMs:  ms,
		StatusText: fmt.Sprintf("%d мс", ms),
	}
}

func probeTelegramMtProxy(target ResourceTarget) ResourceCheckResult {
	start := time.Now()
	dialer := net.Dialer{Timeout: 2500 * time.Millisecond}
	conn, err := tls.DialWithDialer(&dialer, "tcp", "156.239.251.46:443", &tls.Config{
		ServerName:         "www.microsoft.com",
		InsecureSkipVerify: true,
	})
	ms := time.Since(start).Milliseconds()
	if err != nil {
		Logger.Write("DIAG", fmt.Sprintf("Telegram MTProxy (156.239.251.46:443) -> FAIL (%v, %dms)", err, ms))
		return ResourceCheckResult{ID: target.ID, Name: target.Name, OK: false, ElapsedMs: ms, StatusText: "Нужен прокси"}
	}
	_ = conn.Close()
	Logger.Write("DIAG", fmt.Sprintf("Telegram MTProxy (156.239.251.46:443) -> OK (%dms)", ms))
	return ResourceCheckResult{
		ID:         target.ID,
		Name:       target.Name,
		OK:         true,
		ElapsedMs:  ms,
		StatusText: fmt.Sprintf("%d мс", ms),
	}
}

