package capture

import (
	"image"
)

// Frame 表示捕获的帧，包含位图和元信息
type Frame struct {
	Image     *image.RGBA // 位图数据
	OriginX   int         // 虚拟桌面坐标原点 X
	OriginY   int         // 虚拟桌面坐标原点 Y
	DPIScales []DPIInfo   // 各显示器的 DPI 缩放信息
}

// DPIInfo 存储单个显示器的 DPI 信息
type DPIInfo struct {
	MonitorID int     // 显示器标识
	X, Y      int     // 显示器在虚拟桌面的位置
	Width     int     // 显示器宽度（虚拟像素）
	Height    int     // 显示器高度（虚拟像素）
	ScaleX    float64 // DPI 缩放比例 X
	ScaleY    float64 // DPI 缩放比例 Y
}

// Capturer 定义捕获接口
type Capturer interface {
	Capture() (*Frame, error)
	Name() string
}
