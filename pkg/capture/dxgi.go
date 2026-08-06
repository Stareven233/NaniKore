package capture

import (
	"fmt"
)

// DXGICapturer 使用 DXGI Desktop Duplication API
// TODO: 完整实现 DXGI 捕获逻辑
type DXGICapturer struct {
}

func NewDXGICapturer() (*DXGICapturer, error) {
	// 暂时返回错误，优先使用 GDI 路径验证整体流程
	return nil, fmt.Errorf("DXGI capturer not yet implemented, will use GDI fallback")
}

func (c *DXGICapturer) Capture() (*Frame, error) {
	return nil, fmt.Errorf("DXGI capture not implemented")
}

func (c *DXGICapturer) Name() string {
	return "DXGI"
}

func (c *DXGICapturer) Release() {
}
