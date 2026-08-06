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
)

func main() {
	log.SetFlags(log.Ltime | log.Lshortfile)
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
