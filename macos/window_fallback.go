//go:build !darwin || !cgo

package main

import (
	"os"
	"os/exec"
	"os/signal"
	"runtime"
	"syscall"
)

var fallbackQuitCh = make(chan struct{})

func runMacAppWindow(url string, onQuit func()) {
	openBrowserFallback(url)

	sigCh := make(chan os.Signal, 2)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM, syscall.SIGHUP)
	select {
	case <-sigCh:
	case <-fallbackQuitCh:
	}

	if onQuit != nil {
		onQuit()
	}
}

func closeMacAppWindow() {
	select {
	case <-fallbackQuitCh:
	default:
		close(fallbackQuitCh)
	}
}

func openBrowserFallback(url string) {
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

