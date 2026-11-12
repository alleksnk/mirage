//go:build darwin && cgo

package main

/*
#cgo CFLAGS: -x objective-c -fobjc-arc
#cgo LDFLAGS: -framework Cocoa -framework WebKit

#include <stdlib.h>

void mirageRunApp(const char *urlCStr);
void mirageTerminateApp(void);
*/
import "C"

import (
	"sync"
	"unsafe"
)

var (
	onTerminateMu sync.Mutex
	onTerminateFn func()
)

//export mirageOnWillTerminate
func mirageOnWillTerminate() {
	onTerminateMu.Lock()
	fn := onTerminateFn
	onTerminateFn = nil
	onTerminateMu.Unlock()
	if fn != nil {
		fn()
	}
}

//export mirageGetEngineStatus
func mirageGetEngineStatus() C.int {
	st := Engine.State()
	if st.Probing {
		return 2
	}
	if st.Running {
		return 1
	}
	return 0
}

//export mirageToggleEngine
func mirageToggleEngine() {
	st := Engine.State()
	if st.Running || st.Probing {
		go Engine.Stop()
	} else {
		go func() { _ = Engine.Start() }()
	}
}

func runMacAppWindow(url string, onQuit func()) {
	onTerminateMu.Lock()
	onTerminateFn = onQuit
	onTerminateMu.Unlock()

	cURL := C.CString(url)
	defer C.free(unsafe.Pointer(cURL))
	C.mirageRunApp(cURL)

	// Safety net in case NSApp exits without triggering applicationWillTerminate
	mirageOnWillTerminate()
}

func closeMacAppWindow() {
	C.mirageTerminateApp()
}
