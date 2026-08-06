package overlay

import (
	"fmt"
	"image"
	"image/color"
	"log"

	"nanikore/pkg/capture"
	"nanikore/pkg/winapi"

	"gioui.org/app"
	"gioui.org/font"
	"gioui.org/font/gofont"
	"gioui.org/io/event"
	"gioui.org/io/key"
	"gioui.org/io/pointer"
	"gioui.org/io/system"
	"gioui.org/layout"
	"gioui.org/op"
	"gioui.org/op/clip"
	"gioui.org/op/paint"
	"gioui.org/text"
	"gioui.org/unit"
	"gioui.org/widget"
)

const (
	// dimAlpha 是选区外的压暗强度。
	dimAlpha = 150
	// minSelectionPx 以下的选区当作误触，按取消处理——否则 0x0 选区会让
	// 下游的 SubImage / PNG 编码拿到空图。
	minSelectionPx = 4
	borderWidthPx  = 2
)

var (
	colBorder = color.NRGBA{R: 255, G: 255, B: 255, A: 255}
	colDim    = color.NRGBA{A: dimAlpha}
	colChip   = color.NRGBA{A: 190}
	colText   = color.NRGBA{R: 255, G: 255, B: 255, A: 255}
)

// SelectionOverlay 在冻结画面上让用户拖框选出一块区域。
//
// 坐标约定：全程**物理像素**，原点是 frame 位图的左上角。浮层窗口被摆成与
// frame 逐像素重合，因此 Gio 的窗口客户区坐标就是 frame 位图坐标，不需要任何
// 缩放换算。这条重合关系由 winapi.SetPerMonitorV2（进程级 DPI 感知）加
// winapi.SetWindowFullscreenOverlay（按物理像素摆位）共同保证，是"选区零偏移"
// 这条质量约束的实现基础。
type SelectionOverlay struct {
	frame  *capture.Frame
	window *app.Window
	shaper *text.Shaper
	imgOp  paint.ImageOp

	dragging bool
	start    image.Point
	cur      image.Point

	result     image.Rectangle
	ok         bool
	closing    bool
	positioned bool
}

func NewSelectionOverlay(frame *capture.Frame) *SelectionOverlay {
	imgOp := paint.NewImageOp(frame.Image)
	// 1:1 绘制时插值方式本无差别，但选区放大或后续加放大镜时最近邻不会把
	// 小字糊掉。
	imgOp.Filter = paint.FilterNearest

	return &SelectionOverlay{
		frame:  frame,
		imgOp:  imgOp,
		shaper: text.NewShaper(text.WithCollection(gofont.Collection())),
	}
}

// Show 显示浮层，阻塞直到用户完成选择或取消。
//
// 返回的矩形以 frame 位图坐标（物理像素）表达，可直接喂给 frame.Image.SubImage。
//
// 必须在主 goroutine 调用：Gio 的窗口事件循环要求调用方的 goroutine 持续
// 消费 window.Event()，中间不能被别的阻塞操作插队。
func (o *SelectionOverlay) Show() (image.Rectangle, bool) {
	o.window = new(app.Window)
	// 不设 app.Fullscreen：那条路径只覆盖一块显示器，且无边框时 Gio 会把
	// 客户区裁到 WorkArea（不含任务栏）。摆位交给 positionWindow。
	o.window.Option(
		app.Title("NaniKore Selection"),
		app.Decorated(false),
	)

	if err := o.loop(); err != nil {
		log.Printf("Selection overlay loop error: %v", err)
		return image.Rectangle{}, false
	}
	return o.result, o.ok
}

func (o *SelectionOverlay) loop() error {
	var ops op.Ops

	for {
		switch e := o.window.Event().(type) {
		case app.DestroyEvent:
			return e.Err

		case app.Win32ViewEvent:
			// 拿到 HWND 的唯一时机。Gio 没有暴露"覆盖整个虚拟桌面"的选项，
			// 所以自己按物理像素摆位，让窗口客户区与 frame 位图逐像素重合。
			o.positionWindow(uintptr(e.HWND))

		case app.FrameEvent:
			gtx := app.NewContext(&ops, e)
			o.layout(gtx)
			e.Frame(gtx.Ops)

			if o.closing {
				o.window.Perform(system.ActionClose)
			}
		}
	}
}

func (o *SelectionOverlay) positionWindow(hwnd uintptr) {
	if o.positioned || hwnd == 0 {
		return
	}
	o.positioned = true

	// frame 的原点是虚拟桌面坐标，尺寸是物理像素——正是浮层要覆盖的范围。
	b := o.frame.Image.Bounds()
	target := image.Rect(
		o.frame.OriginX,
		o.frame.OriginY,
		o.frame.OriginX+b.Dx(),
		o.frame.OriginY+b.Dy(),
	)
	if err := winapi.SetWindowFullscreenOverlay(hwnd, target); err != nil {
		log.Printf("Failed to position overlay window: %v", err)
	}
}

// layout 绘制一帧并处理这一帧的输入。
//
// 顺序很关键：先 handleInput 消费事件（拿到最新的拖框状态），再按该状态绘制，
// 最后 registerInput 把命中区域重新登记给下一帧。
func (o *SelectionOverlay) layout(gtx layout.Context) {
	o.handleInput(gtx)
	o.draw(gtx)
	o.registerInput(gtx)
}

// registerInput 把整个窗口登记为 o 的输入命中区域。
//
// 这是原来"鼠标事件不响应"的根因：Gio 的 pointer.Filter 只描述"想要哪些事件"，
// **命中区域另外靠 event.Op 声明**。io/input/router.go 处理 ops.TypeInput 时才
// 把 tag 挂进 hitTree；没有 event.Op 的 tag 永远不在命中树里，pointer 事件因此
// 无处可投。原实现只 Push 了 clip.Rect 却没有 event.Op，等于声明了一块没有主人
// 的区域。键盘同理，还需要 key.FocusCmd 主动取得焦点才能收到 ESC。
func (o *SelectionOverlay) registerInput(gtx layout.Context) {
	defer clip.Rect{Max: gtx.Constraints.Max}.Push(gtx.Ops).Pop()
	event.Op(gtx.Ops, o)
	pointer.CursorCrosshair.Add(gtx.Ops)
}

func (o *SelectionOverlay) handleInput(gtx layout.Context) {
	// 焦点不会自动落到 tag 上，每帧请求是幂等的，也能覆盖窗口刚显示时
	// 焦点还没到位的情况。
	if !gtx.Focused(o) {
		gtx.Execute(key.FocusCmd{Tag: o})
	}

	for {
		ev, ok := gtx.Event(
			pointer.Filter{
				Target: o,
				Kinds:  pointer.Press | pointer.Drag | pointer.Release | pointer.Cancel,
			},
			key.FocusFilter{Target: o},
			key.Filter{Focus: o, Name: key.NameEscape},
		)
		if !ok {
			return
		}

		switch e := ev.(type) {
		case pointer.Event:
			o.handlePointer(e)
		case key.Event:
			if e.Name == key.NameEscape && e.State == key.Press {
				o.cancel()
			}
		}
	}
}

func (o *SelectionOverlay) handlePointer(e pointer.Event) {
	// Position 是相对本 tag 的局部坐标；本 tag 覆盖整个客户区且无变换，
	// 所以它就是 frame 位图坐标。
	p := image.Pt(int(e.Position.X), int(e.Position.Y))

	switch e.Kind {
	case pointer.Press:
		if e.Buttons.Contain(pointer.ButtonSecondary) {
			o.cancel()
			return
		}
		o.dragging = true
		o.start, o.cur = p, p

	case pointer.Drag:
		if o.dragging {
			o.cur = p
		}

	case pointer.Release:
		if !o.dragging {
			return
		}
		o.dragging = false
		o.cur = p
		o.finish()

	case pointer.Cancel:
		// 系统抢走了捕获（Alt+Tab、锁屏等）。当作取消，不要留下半截选区。
		o.cancel()
	}
}

func (o *SelectionOverlay) finish() {
	rect := o.selectionRect()
	if rect.Dx() < minSelectionPx || rect.Dy() < minSelectionPx {
		// 点一下就松手是误触，按取消处理。
		o.cancel()
		return
	}

	// 交给调用方的是 frame 位图坐标，裁剪前对齐到位图实际边界。
	o.result = rect.Intersect(o.frame.Image.Bounds())
	o.ok = true
	o.closing = true
}

func (o *SelectionOverlay) cancel() {
	o.ok = false
	o.result = image.Rectangle{}
	o.closing = true
}

// selectionRect 返回规范化后的选区（Min 一定小于 Max）。
func (o *SelectionOverlay) selectionRect() image.Rectangle {
	return image.Rectangle{Min: o.start, Max: o.cur}.Canon()
}

func (o *SelectionOverlay) draw(gtx layout.Context) {
	full := image.Rectangle{Max: gtx.Constraints.Max}

	// 冻结画面铺底。1:1 绘制：位图已是物理像素，客户区也是物理像素。
	o.imgOp.Add(gtx.Ops)
	paint.PaintOp{}.Add(gtx.Ops)

	sel := o.selectionRect().Intersect(full)
	if !o.dragging && !o.ok || sel.Empty() {
		// 还没开始拖：整屏压暗。
		paint.FillShape(gtx.Ops, colDim, clip.Rect(full).Op())
		return
	}

	// 只压暗选区外的四块，选区内保持原亮度。比"整屏压暗 + 选区内重绘原图"
	// 少一次全屏采样，也不会在选区边缘出现半透明叠加的接缝。
	for _, r := range surroundingRects(full, sel) {
		if !r.Empty() {
			paint.FillShape(gtx.Ops, colDim, clip.Rect(r).Op())
		}
	}

	o.drawBorder(gtx, sel)
	o.drawSizeChip(gtx, sel)
}

// surroundingRects 返回 outer 减去 inner 后的四块矩形（上/下/左/右）。
func surroundingRects(outer, inner image.Rectangle) [4]image.Rectangle {
	return [4]image.Rectangle{
		{Min: outer.Min, Max: image.Pt(outer.Max.X, inner.Min.Y)},
		{Min: image.Pt(outer.Min.X, inner.Max.Y), Max: outer.Max},
		{Min: image.Pt(outer.Min.X, inner.Min.Y), Max: image.Pt(inner.Min.X, inner.Max.Y)},
		{Min: image.Pt(inner.Max.X, inner.Min.Y), Max: image.Pt(outer.Max.X, inner.Max.Y)},
	}
}

func (o *SelectionOverlay) drawBorder(gtx layout.Context, sel image.Rectangle) {
	// 边框画在选区外侧，避免盖掉选区最边上的那一行像素——那行像素可能正是
	// 用户要识别的文字。
	paint.FillShape(gtx.Ops, colBorder, clip.Stroke{
		Path:  clip.Rect(sel.Inset(-borderWidthPx / 2)).Path(),
		Width: borderWidthPx,
	}.Op())
}

// drawSizeChip 在选区旁显示物理像素尺寸，用于对齐小字。
func (o *SelectionOverlay) drawSizeChip(gtx layout.Context, sel image.Rectangle) {
	label := fmt.Sprintf("%d × %d", sel.Dx(), sel.Dy())

	const (
		padX      = 6
		padY      = 3
		gap       = 6
		fontSizeD = unit.Sp(13)
	)

	colorMacro := op.Record(gtx.Ops)
	paint.ColorOp{Color: colText}.Add(gtx.Ops)
	textColor := colorMacro.Stop()

	// 先离屏排版量出尺寸，再决定摆哪里。
	macro := op.Record(gtx.Ops)
	dims := widget.Label{MaxLines: 1}.Layout(
		gtx, o.shaper, font.Font{}, fontSizeD, label, textColor,
	)
	textCall := macro.Stop()

	chipW := dims.Size.X + padX*2
	chipH := dims.Size.Y + padY*2

	// 默认贴选区下方左对齐；下方放不下就翻到上方；上下都放不下（选区几乎
	// 占满屏幕）就放进选区内部左上角。
	x := sel.Min.X
	y := sel.Max.Y + gap
	switch {
	case y+chipH > gtx.Constraints.Max.Y && sel.Min.Y-gap-chipH >= 0:
		y = sel.Min.Y - gap - chipH
	case y+chipH > gtx.Constraints.Max.Y:
		y = sel.Min.Y + gap
		x = sel.Min.X + gap
	}
	if x+chipW > gtx.Constraints.Max.X {
		x = gtx.Constraints.Max.X - chipW
	}
	if x < 0 {
		x = 0
	}

	defer op.Offset(image.Pt(x, y)).Push(gtx.Ops).Pop()
	paint.FillShape(gtx.Ops, colChip, clip.UniformRRect(
		image.Rectangle{Max: image.Pt(chipW, chipH)}, 3,
	).Op(gtx.Ops))

	defer op.Offset(image.Pt(padX, padY)).Push(gtx.Ops).Pop()
	textCall.Add(gtx.Ops)
}
