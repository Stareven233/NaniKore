package overlay

import (
	"fmt"
	"image"
	"image/color"
	"log"
	"nanikore/pkg/capture"

	"gioui.org/app"
	"gioui.org/io/key"
	"gioui.org/io/pointer"
	"gioui.org/layout"
	"gioui.org/op"
	"gioui.org/op/clip"
	"gioui.org/op/paint"
)

// SelectionOverlay 显示冻结画面并处理框选
type SelectionOverlay struct {
	frame       *capture.Frame
	window      *app.Window
	selecting   bool
	startX      float32
	startY      float32
	endX        float32
	endY        float32
	resultChan  chan image.Rectangle
	cancelChan  chan struct{}
}

func NewSelectionOverlay(frame *capture.Frame) *SelectionOverlay {
	return &SelectionOverlay{
		frame:      frame,
		resultChan: make(chan image.Rectangle, 1),
		cancelChan: make(chan struct{}, 1),
	}
}

// Show 显示浮层并阻塞直到用户完成选择或取消
func (o *SelectionOverlay) Show() (image.Rectangle, bool) {
	// 创建全屏无边框窗口
	o.window = new(app.Window)
	o.window.Option(
		app.Title("NaniKore Selection"),
		app.Fullscreen.Option(),
		app.Decorated(false),
	)

	log.Println("Selection overlay window created, starting event loop...")
	go o.run()

	// 等待结果
	select {
	case rect := <-o.resultChan:
		log.Printf("Selection completed: %v", rect)
		return rect, true
	case <-o.cancelChan:
		log.Println("Selection cancelled")
		return image.Rectangle{}, false
	}
}

func (o *SelectionOverlay) run() {
	var ops op.Ops

	log.Println("Selection overlay event loop started")

	for {
		e := o.window.Event()

		switch ev := e.(type) {
		case app.DestroyEvent:
			log.Println("Window destroy event received")
			return

		case app.FrameEvent:
			gtx := app.NewContext(&ops, ev)
			o.layout(gtx)
			ev.Frame(gtx.Ops)
		}
	}
}

func (o *SelectionOverlay) layout(gtx layout.Context) layout.Dimensions {
	// 填充整个区域以接收事件
	stack := clip.Rect{Max: gtx.Constraints.Max}.Push(gtx.Ops)

	// 处理事件队列
	for {
		ev, ok := gtx.Event(
			pointer.Filter{Target: o, Kinds: pointer.Press | pointer.Drag | pointer.Release | pointer.Move},
		)
		if !ok {
			break
		}

		if e, ok := ev.(pointer.Event); ok {
			log.Printf("Pointer event: %v at (%.0f, %.0f)", e.Kind, e.Position.X, e.Position.Y)
			o.handlePointer(e)
		}
	}

	for {
		ev, ok := gtx.Event(key.Filter{Focus: o})
		if !ok {
			break
		}

		if e, ok := ev.(key.Event); ok {
			log.Printf("Key event: %v", e.Name)
			if e.Name == key.NameEscape && e.State == key.Press {
				stack.Pop()
				o.cancelChan <- struct{}{}
				return layout.Dimensions{}
			}
		}
	}

	stack.Pop()

	// 绘制半透明黑色蒙层（压暗效果）
	paint.FillShape(gtx.Ops, color.NRGBA{R: 0, G: 0, B: 0, A: 180}, clip.Rect{
		Max: gtx.Constraints.Max,
	}.Op())

	// 如果正在选择，绘制选区矩形
	if o.selecting || (o.endX != 0 && o.endY != 0) {
		o.drawSelection(gtx)
	}

	return layout.Dimensions{Size: gtx.Constraints.Max}
}

func (o *SelectionOverlay) drawSelection(gtx layout.Context) {
	minX, maxX := o.startX, o.endX
	if minX > maxX {
		minX, maxX = maxX, minX
	}
	minY, maxY := o.startY, o.endY
	if minY > maxY {
		minY, maxY = maxY, minY
	}

	// 绘制选区边框
	width := maxX - minX
	height := maxY - minY

	stack := op.Offset(image.Pt(int(minX), int(minY))).Push(gtx.Ops)
	defer stack.Pop()

	// 白色边框
	paint.FillShape(gtx.Ops, color.NRGBA{R: 255, G: 255, B: 255, A: 255},
		clip.Stroke{
			Path:  clip.Rect{Max: image.Pt(int(width), int(height))}.Path(),
			Width: 2,
		}.Op())

	// 显示尺寸信息
	sizeText := fmt.Sprintf("%.0f x %.0f", width, height)
	_ = sizeText // TODO: 绘制文本需要 font 包
}

func (o *SelectionOverlay) handlePointer(e pointer.Event) {
	switch e.Kind {
	case pointer.Press:
		o.selecting = true
		o.startX = e.Position.X
		o.startY = e.Position.Y
		o.endX = e.Position.X
		o.endY = e.Position.Y
		log.Printf("Selection started at (%.0f, %.0f)", o.startX, o.startY)

	case pointer.Drag:
		if o.selecting {
			o.endX = e.Position.X
			o.endY = e.Position.Y
			log.Printf("Selection dragging to (%.0f, %.0f)", o.endX, o.endY)
		}

	case pointer.Release:
		if o.selecting {
			o.selecting = false
			o.endX = e.Position.X
			o.endY = e.Position.Y

			// 计算选区矩形
			rect := o.normalizeRect()
			log.Printf("Selection completed: %v", rect)

			// 发送结果
			o.resultChan <- rect
		}
	}
}

func (o *SelectionOverlay) normalizeRect() image.Rectangle {
	minX, maxX := int(o.startX), int(o.endX)
	if minX > maxX {
		minX, maxX = maxX, minX
	}

	minY, maxY := int(o.startY), int(o.endY)
	if minY > maxY {
		minY, maxY = maxY, minY
	}

	return image.Rect(minX, minY, maxX, maxY)
}
