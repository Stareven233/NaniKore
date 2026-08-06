package hotkey

import (
	"syscall"
	"unsafe"
)

// createMessageWindow 创建一个隐藏窗口用于接收热键消息
func (m *Manager) createMessageWindow() error {
	className, _ := syscall.UTF16PtrFromString("NaniKoreHotkeyWindow")

	var wc struct {
		cbSize        uint32
		style         uint32
		lpfnWndProc   uintptr
		cbClsExtra    int32
		cbWndExtra    int32
		hInstance     uintptr
		hIcon         uintptr
		hCursor       uintptr
		hbrBackground uintptr
		lpszMenuName  *uint16
		lpszClassName *uint16
		hIconSm       uintptr
	}

	wc.cbSize = uint32(unsafe.Sizeof(wc))
	wc.lpfnWndProc = syscall.NewCallback(defWindowProc)
	wc.lpszClassName = className

	ret, _, _ := procRegisterClassEx.Call(uintptr(unsafe.Pointer(&wc)))
	if ret == 0 {
		// 类可能已注册，继续
	}

	windowName, _ := syscall.UTF16PtrFromString("NaniKore Hotkey Window")
	hwnd, _, _ := procCreateWindowEx.Call(
		0,
		uintptr(unsafe.Pointer(className)),
		uintptr(unsafe.Pointer(windowName)),
		0, // WS_OVERLAPPED
		0, 0, 0, 0,
		0, // no parent
		0, // no menu
		0, // hInstance
		0,
	)

	if hwnd == 0 {
		return syscall.GetLastError()
	}

	m.hwnd = hwnd
	return nil
}

func defWindowProc(hwnd, msg, wParam, lParam uintptr) uintptr {
	ret, _, _ := procDefWindowProc.Call(hwnd, msg, wParam, lParam)
	return ret
}
