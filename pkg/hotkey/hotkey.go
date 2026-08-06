package hotkey

import (
	"fmt"
	"log"
	"sync"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Modifier 表示组合键修饰符
type Modifier uint32

const (
	ModAlt   Modifier = 0x0001
	ModCtrl  Modifier = 0x0002
	ModShift Modifier = 0x0004
	ModWin   Modifier = 0x0008
)

// Handler 是热键触发时的回调
type Handler func()

// Manager 管理全局热键
type Manager struct {
	mu       sync.Mutex
	handlers map[int32]Handler
	nextID   int32
	stopChan chan struct{}
	hwnd     uintptr // 隐藏窗口句柄
}

func NewManager() *Manager {
	return &Manager{
		handlers: make(map[int32]Handler),
		nextID:   1,
		stopChan: make(chan struct{}),
	}
}

var (
	user32                 = windows.NewLazySystemDLL("user32.dll")
	procRegisterHotKey     = user32.NewProc("RegisterHotKey")
	procUnregisterHotKey   = user32.NewProc("UnregisterHotKey")
	procGetMessage         = user32.NewProc("GetMessageW")
	procDispatchMessage    = user32.NewProc("DispatchMessageW")
	procTranslateMessage   = user32.NewProc("TranslateMessage")
	procDefWindowProc      = user32.NewProc("DefWindowProcW")
	procRegisterClassEx    = user32.NewProc("RegisterClassExW")
	procCreateWindowEx     = user32.NewProc("CreateWindowExW")
	procPostQuitMessage    = user32.NewProc("PostQuitMessage")
)

const (
	WM_HOTKEY = 0x0312
)

// Register 注册一个全局热键
func (m *Manager) Register(mod Modifier, vk uint32, handler Handler) (int32, error) {
	m.mu.Lock()
	defer m.mu.Unlock()

	id := m.nextID
	m.nextID++

	ret, _, err := procRegisterHotKey.Call(
		0,
		uintptr(id),
		uintptr(mod),
		uintptr(vk),
	)

	if ret == 0 {
		return 0, fmt.Errorf("RegisterHotKey failed: %w", err)
	}

	m.handlers[id] = handler
	log.Printf("Registered hotkey ID=%d, mod=%d, vk=%d", id, mod, vk)
	return id, nil
}

// Unregister 取消注册热键
func (m *Manager) Unregister(id int32) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	ret, _, err := procUnregisterHotKey.Call(0, uintptr(id))
	if ret == 0 {
		return fmt.Errorf("UnregisterHotKey failed: %w", err)
	}

	delete(m.handlers, id)
	return nil
}

// Start 开始消息循环（已废弃，使用 Run 代替）
func (m *Manager) Start() {
	// 保留此方法以兼容，但不做任何事
	log.Println("Warning: Start() is deprecated, use Run() instead")
}

// Run 在当前线程运行消息循环（阻塞）
func (m *Manager) Run() {
	// 创建隐藏窗口以接收消息
	if err := m.createMessageWindow(); err != nil {
		log.Fatalf("Failed to create message window: %v", err)
	}
	m.messageLoop()
}

// Stop 停止消息循环
func (m *Manager) Stop() {
	close(m.stopChan)
}

func (m *Manager) messageLoop() {
	type MSG struct {
		HWND    uintptr
		Message uint32
		WParam  uintptr
		LParam  uintptr
		Time    uint32
		Pt      struct{ X, Y int32 }
	}

	log.Println("Hotkey message loop started")

	var msg MSG
	for {
		// GetMessage 阻塞等待消息
		ret, _, _ := procGetMessage.Call(
			uintptr(unsafe.Pointer(&msg)),
			0,
			0,
			0,
		)

		// ret == 0 表示收到 WM_QUIT
		if ret == 0 {
			log.Println("Received WM_QUIT, stopping hotkey loop")
			return
		}

		// ret == -1 表示错误
		if ret == ^uintptr(0) {
			log.Println("GetMessage error")
			continue
		}

		log.Printf("Received message: 0x%X (WM_HOTKEY=0x0312), WParam=%d", msg.Message, msg.WParam)

		if msg.Message == 0x0312 { // WM_HOTKEY
			id := int32(msg.WParam)
			log.Printf("WM_HOTKEY received, ID=%d", id)

			m.mu.Lock()
			handler, ok := m.handlers[id]
			m.mu.Unlock()

			if ok {
				log.Printf("Executing handler for hotkey ID=%d", id)
				handler() // 同步执行，看看是否有问题
			} else {
				log.Printf("No handler found for hotkey ID=%d", id)
			}
		}

		// 分发消息
		procTranslateMessage.Call(uintptr(unsafe.Pointer(&msg)))
		procDispatchMessage.Call(uintptr(unsafe.Pointer(&msg)))
	}
}
