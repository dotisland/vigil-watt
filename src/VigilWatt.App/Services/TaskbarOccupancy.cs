using System.Runtime.InteropServices;
using System.Text;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 任务栏"占位"探测：摸清任务栏上别人已经占住的矩形（TrafficMonitor、资讯和兴趣小组件、
/// 小组件按钮……），并能在贴片需要落地的那一段把任务列表（MSTaskSwWClass）收窄，**真正让出一块空地**。
/// 2026/10/18 18:10:00
/// 背景：贴片原来只按"紧贴托盘左边"定位（见 docs/dev/runtime-pitfalls.md §16），
/// 而任务栏右侧本来就可能被别的程序的嵌入式窗口占着，于是三层字叠在同一个位置。
/// Windows 没有给第三方提供"往任务栏布局里插队"的公开接口，可行的两条路只有：
/// ① 避开已经被人占住的矩形；② 把任务列表收窄（TrafficMonitor 在经典任务栏上就是这么给自己腾地方的）。
/// 本类同时提供这两件事，纯 P/Invoke，不碰 XAML，便于单独推理。
/// </summary>
public static class TaskbarOccupancy
{
    private const uint GW_CHILD = 5;
    private const uint GW_HWNDNEXT = 2;

    private const string ReBarClassName = "ReBarWindow32";
    private const string TaskListClassName = "MSTaskSwWClass";
    private const string TaskListFallbackClassName = "MSTaskListWClass";

    /// <summary>贴片落地需要的最小可用宽度（物理像素）。再窄下去任务按钮就没法看了，宁可让。</summary>
    private const int MinTaskListWidthPx = 160;

    /// <summary>任务栏上"别人的窗口"：类名 + 屏幕矩形。</summary>
    public readonly record struct Occupant(string ClassName, PixelRect Rect);

    /// <summary>
    /// 系统自身的任务栏部件。它们当然占位，但不该被贴片当成"障碍物"——否则贴片会被自己的容器逼到屏幕外。
    /// 注意 <c>MSTaskSwWClass</c> 也在这里：任务列表是**可以被收窄的**，走 <see cref="Reserve"/> 单独处理。
    /// </summary>
    private static readonly HashSet<string> ShellChrome = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Start", "TrayButton", "TrayDummySearchControl", ReBarClassName,
        "TrayNotifyWnd", "TrayInputIndicatorWClass", "TrayClockWClass",
        "TrayShowDesktopButtonWClass", "PenWorkspaceButton", TaskListClassName,
        "VigilWattTaskbarPill", "VigilWattTaskbarPillTip",
    };

    /// <summary>
    /// 收集任务栏上被别人占住的矩形（屏幕坐标、物理像素）。
    /// 只看任务栏的**直接子窗口**，外加 ReBarWindow32 的直接子窗口（第三方任务栏窗口通常挂在那里，
    /// 例如 TrafficMonitor 的 <c>#32770</c>）；<c>ReBarWindow32</c> 自身与系统部件不算障碍。
    /// </summary>
    public static List<PixelRect> Collect(IntPtr taskbar, IntPtr self)
    {
        var rects = new List<PixelRect>();
        if (taskbar == IntPtr.Zero) return rects;

        foreach (var child in Children(taskbar))
        {
            if (child == self) continue;

            // ReBar 本身是系统部件、不算障碍物，但**它里面的 band 才是第三方任务栏窗口真正的落点**
            // （TrafficMonitor 就挂在这里），所以必须先判它、再决定要不要跳过。
            if (ClassOf(child).Equals(ReBarClassName, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var band in Children(child))
                {
                    if (band == self) continue;
                    if (IsOccupant(band, out _)) Add(rects, band);
                }
                continue;
            }

            if (IsOccupant(child, out _)) Add(rects, child);
        }

        // 应用按钮区**不做像素扫描**（2026-10-18 用户决策）：贴片只跟第三方窗口（BatteryBar、
        // TrafficMonitor 等）避让，落在"任务按钮区结束 → 托盘/BatteryBar"之间。
        // 之前扫按钮区会让贴片被自排除机制钉在按钮中间：贴片占住的位置从扫描里消失，
        // 新开的程序按钮只能挤在它旁边，看起来就像贴片"压"在图标区里。
        // 代价：TrafficMonitor 收窄任务列表时，贴片可能落在按钮区尾部——用户已接受该取舍。
        return rects;
    }

    /// <summary>检测 TrafficMonitor 是否在任务栏上（它在收窄同一个任务列表，我们在场时必须让路，否则互相追赶振荡）。</summary>
    public static bool HasTrafficMonitor(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero) return false;
        var bar = Native.FindWindowExW(taskbar, IntPtr.Zero, ReBarClassName, null);
        if (bar == IntPtr.Zero) return false;

        var child = GetWindow(bar, GW_CHILD);
        while (child != IntPtr.Zero)
        {
            if (GetWindowText(child) == "TrafficMonitorTaskbarWindow") return true;
            child = GetWindow(child, GW_HWNDNEXT);
        }
        return false;
    }

    /// <summary>找出任务列表窗口（收窄它就能腾出地方）；找不到返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr FindTaskList(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero) return IntPtr.Zero;
        var bar = Native.FindWindowExW(taskbar, IntPtr.Zero, ReBarClassName, null);
        if (bar == IntPtr.Zero) return IntPtr.Zero;

        var list = Native.FindWindowExW(bar, IntPtr.Zero, TaskListClassName, null);
        return list != IntPtr.Zero
            ? list
            : Native.FindWindowExW(bar, IntPtr.Zero, TaskListFallbackClassName, null);
    }

    /// <summary>
    /// 找出任务列表的**绘制画布**（MSTaskListWClass）。任务按钮是它自绘的、不是独立窗口，
    /// 按钮的真实矩形只有 UIA 能量（见 <see cref="TaskbarButtons"/>）。找不到退回容器本身。
    /// </summary>
    public static IntPtr FindTaskListCanvas(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero) return IntPtr.Zero;
        var bar = Native.FindWindowExW(taskbar, IntPtr.Zero, ReBarClassName, null);
        if (bar == IntPtr.Zero) return IntPtr.Zero;
        var sw = Native.FindWindowExW(bar, IntPtr.Zero, TaskListClassName, null);
        if (sw == IntPtr.Zero) return IntPtr.Zero;

        var canvas = Native.FindWindowExW(sw, IntPtr.Zero, TaskListFallbackClassName, null);
        return canvas != IntPtr.Zero ? canvas : sw;
    }

    /// <summary>
    /// 把任务列表的右边界收到 <paramref name="rightLimit"/> 之前；<paramref name="rightLimit"/> 不小于当前
    /// 右边界时表示"不需要收窄了"，按 <paramref name="original"/> 还原。
    /// 注意：目标宽度必须只跟"贴片需要多宽"挂钩（调用方保证），**不能**依赖 TrafficMonitor 等别人的
    /// 实时位置——否则它们跟着宽度重算位置、我们下一帧又跟着它们改宽度，形成振荡（§16 实测踩过）。
    /// Explorer 重建任务列表会换句柄，调用方每帧比对一次，换了就丢掉旧备份重新记。
    /// </summary>
    internal static void Reserve(IntPtr taskList, int rightLimit, ref Native.RECT? original)
    {
        if (taskList == IntPtr.Zero || !Native.IsWindow(taskList)) return;
        if (!Native.GetWindowRect(taskList, out var r)) return;

        var parent = GetParent(taskList);
        if (parent == IntPtr.Zero) return;

        if (rightLimit >= r.Right)
        {
            // 目标 ≥ 当前右端时分三种情况，**必须逐一区分**（2026-10-19 实测踩坑×2）：
            //   · rightLimit ≥ 原始右端 → 真的不需要限长了，完全还原；
            //   · 原始右端 > rightLimit ≥ 当前右端 → **放宽到新目标**：贴片变小（设备拔出）时，
            //     目标从 1657 变 1756，若保持现状按钮区就停在旧宽度，贴片右侧留出一截大空格；
            //   · rightLimit 恰好等于当前右端 → 已就位，别动（动了就和下一拍的收窄打架，
            //     容器在"限长↔全宽"之间以秒级抖动——用户看到任务栏在两个状态间不停跳动）。
            // 前两种都**保留 original**：原始宽度是唯一基准，丢了就会被下一拍当成"原始"重新记错。
            if (original is Native.RECT o)
            {
                if (rightLimit >= o.Right)
                {
                    if (r.Width != o.Width)
                    {
                        MoveToParent(parent, taskList, o.Left, o.Top, o.Width, o.Height);
                        original = null;
                    }
                }
                else if (rightLimit > r.Right)
                {
                    // 部分放宽：仍处于限长状态，只是贴片变小了，把多占的地方还给按钮区
                    MoveToParent(parent, taskList, r.Left, r.Top, rightLimit - r.Left, r.Height);
                }
            }
            return;
        }

        original ??= r;
        int width = rightLimit - r.Left;
        if (width < MinTaskListWidthPx) width = MinTaskListWidthPx;
        if (width == r.Width) return;   // 已经是这个宽度，别每帧都 MoveWindow
        MoveToParent(parent, taskList, r.Left, r.Top, width, r.Height);
    }

    /// <summary>窗口被父进程重建（Explorer 重启）后，旧的句柄与备份都不再有意义。</summary>
    internal static void Forget(ref Native.RECT? original) => original = null;

    // ---------- 内部 ----------

    private static bool IsOccupant(IntPtr hwnd, out string className)
    {
        className = ClassOf(hwnd);
        if (className.Length == 0) return false;
        if (ShellChrome.Contains(className)) return false;
        return IsWindowVisible(hwnd);
    }

    private static void Add(List<PixelRect> rects, IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        var rect = new PixelRect(r.Left, r.Top, r.Width, r.Height);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        rects.Add(rect);
    }

    private static IEnumerable<IntPtr> Children(IntPtr parent)
    {
        var child = GetWindow(parent, GW_CHILD);
        while (child != IntPtr.Zero)
        {
            yield return child;
            child = GetWindow(child, GW_HWNDNEXT);
        }
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        int n = GetClassNameW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : string.Empty;
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        int n = GetWindowTextW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : string.Empty;
    }

    private static void MoveToParent(IntPtr parent, IntPtr child, int x, int y, int w, int h)
    {
        var pt = new Native.POINT { X = x, Y = y };
        Native.ScreenToClient(parent, ref pt);
        MoveWindow(child, pt.X, pt.Y, w, h, true);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
}
