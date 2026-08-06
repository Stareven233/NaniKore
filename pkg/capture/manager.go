package capture

import (
	"fmt"
	"log"
)

// Manager 管理捕获器的降级链
type Manager struct {
	capturers []Capturer
}

func NewManager() *Manager {
	m := &Manager{
		capturers: []Capturer{},
	}

	// 尝试初始化 DXGI
	dxgi, err := NewDXGICapturer()
	if err != nil {
		log.Printf("DXGI capturer init failed: %v, will use GDI fallback", err)
	} else {
		m.capturers = append(m.capturers, dxgi)
	}

	// GDI 作为兜底
	m.capturers = append(m.capturers, NewGDICapturer())

	return m
}

// Capture 依次尝试各个捕获器
func (m *Manager) Capture() (*Frame, error) {
	var lastErr error
	for _, capturer := range m.capturers {
		frame, err := capturer.Capture()
		if err == nil {
			log.Printf("Captured using %s", capturer.Name())
			return frame, nil
		}
		log.Printf("%s capture failed: %v", capturer.Name(), err)
		lastErr = err
	}
	return nil, fmt.Errorf("all capturers failed, last error: %w", lastErr)
}
