// NaniKore 同进程挂载 WPF（界面）与 WinForms（图标/托盘），全局 using 两侧命名空间都在，
// Button / Application / Color / Point 等类型名大面积撞车——文件级 using 与全局 using 同作用域，
// 无法靠「就近覆盖」消除歧义（这也是早期自测满屏全限定名的原因）。
// 这里为整个程序集提供 WPF 各命名空间的短别名 + WinForms 显式别名：
// 自测与 UI 代码统一写 WpfControls.Button / WinForms.Keys 这类短名，可读且绝无歧义。
global using Wpf = System.Windows;
global using WpfControls = System.Windows.Controls;
global using WpfPrimitives = System.Windows.Controls.Primitives;
global using WpfDocuments = System.Windows.Documents;
global using WpfInput = System.Windows.Input;
global using WpfMedia = System.Windows.Media;
global using WpfThreading = System.Windows.Threading;
global using WinForms = System.Windows.Forms;
