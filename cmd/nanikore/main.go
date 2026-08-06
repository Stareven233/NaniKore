package main

import (
	"fmt"
	"image"
	"image/png"
	"log"
	"os"
	"path/filepath"

	"nanikore/pkg/capture"
	"nanikore/pkg/hotkey"
	"nanikore/pkg/overlay"
	"nanikore/pkg/version"
	"nanikore/pkg/winapi"
)

func main() {
	log.SetFlags(log.Ltime | log.Lshortfile)

	// 必须在任何窗口创建前声明 Per-Monitor V2。Gio 自己只调
	// SetProcessDPIAware()（System aware），混合 DPI 下非主屏的窗口会被 DWM
	// 位图拉伸，窗口坐标与鼠标坐标都变成虚拟化后的值——选区必然偏移。
	// DPI 感知级别是进程级一次性设置，先设的赢。
	if err := winapi.SetPerMonitorV2(); err != nil {
		log.Printf("Per-Monitor V2 DPI awareness unavailable (%v); "+
			"falling back to system DPI awareness, selection may drift on mixed-DPI setups", err)
	}

	log.Printf("NaniKore %s starting...", version.String())

	// 初始化捕获管理器
	captureManager := capture.NewManager()

	// 初始化热键管理器
	hotkeyManager := hotkey.NewManager()

	// 注册热键：Ctrl+Shift+A
	_, err := hotkeyManager.Register(
		hotkey.ModCtrl|hotkey.ModShift,
		0x41, // VK_A
		func() {
			log.Println("Hotkey triggered: capturing screen...")
			handleCapture(captureManager)
		},
	)
	if err != nil {
		log.Fatalf("Failed to register hotkey: %v", err)
	}

	fmt.Printf("NaniKore %s\n", version.String())
	fmt.Println("Press Ctrl+Shift+A to capture and select region")
	fmt.Println("Press Ctrl+C to exit")

	// 在主线程运行消息循环（阻塞）
	hotkeyManager.Run()
}

func handleCapture(cm *capture.Manager) {
	// 捕获屏幕
	frame, err := cm.Capture()
	if err != nil {
		log.Printf("Capture failed: %v", err)
		return
	}

	log.Printf("Captured frame: %dx%d at (%d, %d)",
		frame.Image.Bounds().Dx(), frame.Image.Bounds().Dy(),
		frame.OriginX, frame.OriginY)

	// 显示选区浮层
	selOverlay := overlay.NewSelectionOverlay(frame)
	rect, ok := selOverlay.Show()
	if !ok {
		log.Println("Selection cancelled")
		return
	}

	log.Printf("Selected rect: %v", rect)

	// 裁剪选区并保存到临时文件用于验证
	cropped := frame.Image.SubImage(rect).(*image.RGBA)
	saveToTemp(cropped)
}

func saveToTemp(img *image.RGBA) {
	tempDir := os.TempDir()
	filename := filepath.Join(tempDir, "nanikore_selection.png")

	f, err := os.Create(filename)
	if err != nil {
		log.Printf("Failed to create temp file: %v", err)
		return
	}
	defer f.Close()

	if err := png.Encode(f, img); err != nil {
		log.Printf("Failed to encode PNG: %v", err)
		return
	}

	log.Printf("Selection saved to: %s", filename)
}
