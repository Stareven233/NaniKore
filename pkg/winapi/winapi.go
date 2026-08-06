// Package winapi 封装 NaniKore 需要、但 golang.org/x/sys/windows 未导出的
// 少量 Win32 调用。只放共享的底层原语，业务逻辑不进这里。
package winapi

import (
	"fmt"
	"image"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	user32 = windows.NewLazySystemDLL("user32.dll")

	procSetProcessDpiAwarenessContext = user32.NewProc("SetProcessDpiAwarenessContext")
	procGetWindowRect                 = user32.NewProc("GetWindowRect")
	procSetWindowPos                  = user32.NewProc("SetWindowPos")
	procGetSystemMetrics              = user32.NewProc("GetSystemMetrics")
)

// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 是句柄式常量 (HANDLE)(-4)。
const dpiPerMonitorAwareV2 = ^uintptr(3) // -4

// 虚拟桌面度量。GetSystemMetrics 的返回值受进程 DPI 感知级别影响：
// PerMonitorV2 下是物理像素，System-aware 下是按主屏缩放虚拟化过的值。
const (
	smXVirtualScreen  = 76
	smYVirtualScreen  = 77
	smCXVirtualScreen = 78
	smCYVirtualScreen = 79
)

const (
	swpNoActivate  = 0x0010
	swpShowWindow  = 0x0040
	swpFrameChange = 0x0020
)

// 窗口样式。浮层要的是 WS_POPUP：没有边框、没有标题栏、不可缩放。
const (
	gwlStyle   = ^(uintptr(16) - 1) // -16
	gwlExStyle = ^(uintptr(20) - 1) // -20

	wsPopup         = 0x80000000
	wsVisible       = 0x10000000
	wsClipSiblings  = 0x04000000
	wsClipChildren  = 0x02000000
	wsExToolWindow  = 0x00000080
	wsExTopMost     = 0x00000008
	wsExNoActivate  = 0x08000000
)

// hwndTopMost 即 (HWND)(-1)。
const hwndTopMost = ^uintptr(0)

type rect struct {
	Left, Top, Right, Bottom int32
}

// SetPerMonitorV2 把进程声明为 Per-Monitor DPI Aware V2。
//
// 必须在创建任何窗口之前调用。Gio 自己只调 SetProcessDPIAware()（System
// aware），那会让混合 DPI 下非主屏的窗口被 DWM 位图拉伸，GetWindowRect 与
// 鼠标坐标都变成虚拟化后的值，选区必然偏移。DPI 感知级别是进程级一次性
// 设置，先设的赢——所以这里必须早于 Gio 的第一个窗口。
//
// 已经设置过（或系统早于 Win10 1703）时返回 error，调用方可以只记日志：
// 那种情况下退化成 Gio 的 System aware 行为，单屏仍然正确。
func SetPerMonitorV2() error {
	if err := procSetProcessDpiAwarenessContext.Find(); err != nil {
		return fmt.Errorf("SetProcessDpiAwarenessContext unavailable: %w", err)
	}
	ret, _, err := procSetProcessDpiAwarenessContext.Call(dpiPerMonitorAwareV2)
	if ret == 0 {
		return fmt.Errorf("SetProcessDpiAwarenessContext failed: %w", err)
	}
	return nil
}

// VirtualScreenBounds 返回虚拟桌面的边界。左上角可以是负坐标（副屏在
// 主屏左侧或上方时）。
func VirtualScreenBounds() image.Rectangle {
	x := getSystemMetrics(smXVirtualScreen)
	y := getSystemMetrics(smYVirtualScreen)
	w := getSystemMetrics(smCXVirtualScreen)
	h := getSystemMetrics(smCYVirtualScreen)
	return image.Rect(x, y, x+w, y+h)
}

func getSystemMetrics(index int32) int {
	ret, _, _ := procGetSystemMetrics.Call(uintptr(index))
	return int(int32(ret))
}

// GetWindowRect 返回窗口在屏幕坐标系中的矩形。
func GetWindowRect(hwnd uintptr) (image.Rectangle, error) {
	var r rect
	ret, _, err := procGetWindowRect.Call(hwnd, uintptr(unsafe.Pointer(&r)))
	if ret == 0 {
		return image.Rectangle{}, fmt.Errorf("GetWindowRect failed: %w", err)
	}
	return image.Rect(int(r.Left), int(r.Top), int(r.Right), int(r.Bottom)), nil
}

// SetWindowFullscreenOverlay 把窗口摆成覆盖 bounds 的置顶浮层。
//
// 不用 Gio 的 app.Fullscreen：那条路径走 SW_SHOWMAXIMIZED，只覆盖**一块**
// 显示器，而且无边框时 Gio 在 WM_NCCALCSIZE 里把客户区裁到 MonitorInfo
// .WorkArea（不含任务栏）。浮层需要盖住整个虚拟桌面且包含任务栏，所以
// 自己摆位置。
//
// 调用后不要再调 app.Window.Option，否则 Gio 的 Configure 会重新摆位。
func SetWindowFullscreenOverlay(hwnd uintptr, bounds image.Rectangle) error {
	ret, _, err := procSetWindowPos.Call(
		hwnd,
		hwndTopMost,
		uintptr(int32(bounds.Min.X)),
		uintptr(int32(bounds.Min.Y)),
		uintptr(int32(bounds.Dx())),
		uintptr(int32(bounds.Dy())),
		uintptr(swpShowWindow|swpFrameChange|swpNoActivate),
	)
	if ret == 0 {
		return fmt.Errorf("SetWindowPos failed: %w", err)
	}
	return nil
}
