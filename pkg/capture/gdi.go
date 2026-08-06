package capture

import (
	"fmt"
	"image"

	"github.com/kbinani/screenshot"
)

// GDICapturer 使用 GDI BitBlt 作为兜底
type GDICapturer struct{}

func NewGDICapturer() *GDICapturer {
	return &GDICapturer{}
}

func (c *GDICapturer) Capture() (*Frame, error) {
	// 捕获所有显示器，拼成虚拟桌面
	n := screenshot.NumActiveDisplays()
	if n == 0 {
		return nil, fmt.Errorf("no active displays")
	}

	// 计算虚拟桌面边界
	var minX, minY, maxX, maxY int
	displays := make([]image.Rectangle, n)
	for i := 0; i < n; i++ {
		bounds := screenshot.GetDisplayBounds(i)
		displays[i] = bounds
		if i == 0 {
			minX, minY = bounds.Min.X, bounds.Min.Y
			maxX, maxY = bounds.Max.X, bounds.Max.Y
		} else {
			if bounds.Min.X < minX {
				minX = bounds.Min.X
			}
			if bounds.Min.Y < minY {
				minY = bounds.Min.Y
			}
			if bounds.Max.X > maxX {
				maxX = bounds.Max.X
			}
			if bounds.Max.Y > maxY {
				maxY = bounds.Max.Y
			}
		}
	}

	// 创建虚拟桌面画布
	width := maxX - minX
	height := maxY - minY
	canvas := image.NewRGBA(image.Rect(0, 0, width, height))

	// 捕获每个显示器并拼接
	for i, bounds := range displays {
		img, err := screenshot.CaptureRect(bounds)
		if err != nil {
			return nil, fmt.Errorf("capture display %d failed: %w", i, err)
		}

		// 转换并绘制到画布上
		offsetX := bounds.Min.X - minX
		offsetY := bounds.Min.Y - minY
		for y := 0; y < img.Bounds().Dy(); y++ {
			for x := 0; x < img.Bounds().Dx(); x++ {
				canvas.Set(offsetX+x, offsetY+y, img.At(x, y))
			}
		}
	}

	// TODO: 收集 DPI 信息
	frame := &Frame{
		Image:     canvas,
		OriginX:   minX,
		OriginY:   minY,
		DPIScales: []DPIInfo{},
	}

	return frame, nil
}

func (c *GDICapturer) Name() string {
	return "GDI"
}
