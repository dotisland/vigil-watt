namespace VigilWatt.App.Services;

/// <summary>屏幕坐标下的像素矩形（左上角 + 宽高）。任务栏定位全程物理像素，包一个类型免得四处传四个 int。</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>
/// 功 能： 任务栏贴片的定位数学：零 WinRT、零 P/Invoke 的纯函数，直接链进测试项目（R8）。
/// 2026/10/10 21:34:00 xliu
/// 窗口类只负责"把值喂进来、把结果写进 AppWindow"——算错位置这种事必须能在单测里复现。
/// </summary>
public static class TaskbarLayout
{
    /// <summary>贴片与任务栏两端的最小间距（物理像素）。</summary>
    public const int EdgeInsetPx = 4;

    /// <summary>任务栏收起后的判定厚度：自动隐藏后只剩约 2px 的一条边，贴片必须跟着一起藏。</summary>
    public const int HiddenThicknessPx = 4;

    /// <summary>任务栏当前是否不可见（自动隐藏已收起）。</summary>
    public static bool IsTaskbarHidden(PixelRect taskbar) =>
        Math.Min(taskbar.Width, taskbar.Height) <= HiddenThicknessPx;

    /// <summary>横向任务栏（贴片左右排）还是竖向（上下排）。</summary>
    public static bool IsHorizontal(PixelRect taskbar) => taskbar.Width >= taskbar.Height;

    /// <summary>贴片高度（物理像素）：按任务栏厚度减上下留白，再夹在 [minDip, maxDip] 内。</summary>
    public static int PillHeight(PixelRect taskbar, double scale, double marginDip, double minDip, double maxDip)
    {
        double thicknessDip = (IsHorizontal(taskbar) ? taskbar.Height : taskbar.Width) / scale;
        return (int)Math.Ceiling(Math.Clamp(thicknessDip - marginDip * 2, minDip, maxDip) * scale);
    }

    /// <summary>
    /// 贴片左上角（物理像素）。默认位 = 紧贴托盘左边（竖向任务栏则是托盘上边），再叠加用户拖动出来的
    /// <paramref name="offset"/>；结果夹在任务栏内，并把"实际落点"反推回 <paramref name="appliedOffset"/>——
    /// 这样拖到头再往回拖不会有一段"空走"。
    /// </summary>
    public static (int X, int Y) Place(
        PixelRect taskbar, PixelRect tray, int pillW, int pillH, int offset, int gapPx, out int appliedOffset)
    {
        if (IsHorizontal(taskbar))
        {
            int anchor = (tray.Width > 0 ? tray.Left : taskbar.Right) - pillW - gapPx;
            int min = taskbar.Left + EdgeInsetPx;
            int max = Math.Max(min, taskbar.Right - pillW - EdgeInsetPx);
            int x = Math.Clamp(anchor + offset, min, max);
            appliedOffset = x - anchor;
            return (x, taskbar.Top + (taskbar.Height - pillH) / 2);
        }

        int anchorY = (tray.Height > 0 ? tray.Top : taskbar.Bottom) - pillH - gapPx;
        int minY = taskbar.Top + EdgeInsetPx;
        int maxY = Math.Max(minY, taskbar.Bottom - pillH - EdgeInsetPx);
        int y = Math.Clamp(anchorY + offset, minY, maxY);
        appliedOffset = y - anchorY;

        int centeredX = taskbar.Left + (taskbar.Width - pillW) / 2;
        if (pillW > taskbar.Width)
        {
            // 竖向任务栏只有 40~50px 宽、装不下横向胶囊：居中会让它一半跑到屏幕外，
            // 改挂在"桌面那一侧"——左停靠往右伸、右停靠往左伸
            centeredX = taskbar.Left <= 0 ? taskbar.Left : taskbar.Right - pillW;
        }
        return (centeredX, y);
    }

    /// <summary>
    /// 避开已被占用的矩形之后的落点：从"紧贴托盘左边"起步，一旦和 <paramref name="occupied"/> 相交，
    /// 就把右边界收到该障碍物左边（再重试），直到不再相交或被任务栏左端夹住。
    /// 语义是"**宁可往左挪，也不叠在别人身上**"——让贴片和 TrafficMonitor、资讯和兴趣小组件共存。
    /// <paramref name="blocked"/> 为 true 表示已经被夹到任务栏左端、仍有重叠（调用方据此再让任务列表收窄）。
    /// </summary>
    public static (int X, int Y) PlaceAvoiding(
        PixelRect taskbar, PixelRect tray, IReadOnlyList<PixelRect> occupied,
        int pillW, int pillH, int offset, int gapPx, out int appliedOffset, out bool blocked)
    {
        blocked = false;

        // 竖向任务栏（贴片上下排）暂不参与避让：那条路上连"托盘矩形的宽"都可能为 0，先保持原行为
        if (!IsHorizontal(taskbar))
        {
            var (px, py) = Place(taskbar, tray, pillW, pillH, offset, gapPx, out appliedOffset);
            return (px, py);
        }

        int y = taskbar.Top + (taskbar.Height - pillH) / 2;
        int min = taskbar.Left + EdgeInsetPx;
        int anchor = (tray.Width > 0 ? tray.Left : taskbar.Right) - pillW - gapPx;

        // 只有两个候选位，二选一，不做逐个左移：
        //   ① 最右：紧贴托盘左边（默认位）
        //   ② 最左：最靠右那个占位窗口的左边
        // 逐个左移会让贴片漂到任务按钮中间，位置随开着的程序变，用户没法预期。
        int slotRight = anchor + pillW + offset;          // 候选 ① 的右边界
        int slotLeft = int.MinValue;                      // 候选 ② 的右边界

        int x = slotRight - pillW;
        foreach (var o in occupied)
        {
            if (o.Width <= 0 || o.Height <= 0) continue;
            // 只算同一条任务栏上的障碍物（多显示器时别的屏上的矩形不参与）
            if (o.Bottom <= taskbar.Top || o.Top >= taskbar.Bottom) continue;
            if (x >= o.Right || x + pillW <= o.Left) continue;   // 横向不相交
            if (o.Left - gapPx > slotLeft) slotLeft = o.Left - gapPx;
        }

        if (slotLeft != int.MinValue) x = slotLeft - pillW;       // ① 被占 → 用 ②
        if (x < min) { x = min; }                                 // ② 也放不下 → 贴任务栏左端
        blocked = x == min;

        appliedOffset = x - anchor;
        return (x, y);
    }

    /// <summary>
    /// 空隙列表：把 <paramref name="occupied"/> 合并成区间后，返回"任务栏可用带"里剩余的空档（左闭右开）。
    /// 可用带 = [任务栏左端+内缩, 托盘左边-gap]；只算与本条任务栏纵向相交的占位。
    /// 任务栏贴片、TrafficMonitor、BatteryBar、资讯和兴趣小组件都是往这一带里挤，所以空隙要能单独算出来。
    /// </summary>
    public static List<(int Left, int Right)> Gaps(
        PixelRect taskbar, PixelRect tray, IReadOnlyList<PixelRect> occupied, int gapPx)
    {
        var result = new List<(int Left, int Right)>();
        if (!IsHorizontal(taskbar))
        {
            int h = (tray.Height > 0 ? tray.Top : taskbar.Bottom) - gapPx;
            result.Add((taskbar.Top + EdgeInsetPx, Math.Max(taskbar.Top + EdgeInsetPx, h)));
            return result;
        }

        int bandLeft = taskbar.Left + EdgeInsetPx;
        int bandRight = (tray.Width > 0 ? tray.Left : taskbar.Right) - gapPx;
        if (bandRight <= bandLeft) return result;

        // 只保留与可用带横向相交、且与本条任务栏纵向相交的障碍；按左边界排序后合并
        var spans = occupied
            .Where(o => o.Width > 0 && o.Height > 0)
            .Where(o => o.Bottom > taskbar.Top && o.Top < taskbar.Bottom)
            .Where(o => o.Right > bandLeft && o.Left < bandRight)
            .Select(o => (Left: Math.Max(o.Left - gapPx, bandLeft), Right: Math.Min(o.Right + gapPx, bandRight)))
            .OrderBy(o => o.Left)
            .ToList();

        int cursor = bandLeft;
        foreach (var s in spans)
        {
            if (s.Left > cursor) result.Add((cursor, Math.Min(s.Left, bandRight)));
            if (s.Right > cursor) cursor = s.Right;
        }
        if (cursor < bandRight) result.Add((cursor, bandRight));

        return result.Where(g => g.Right > g.Left).ToList();
    }

    /// <summary>可用带里最宽的那个空档有多宽（一像素都没有时返回 0）。调用方据此决定贴片最多能画几台。</summary>
    public static int WidestGap(
        PixelRect taskbar, PixelRect tray, IReadOnlyList<PixelRect> occupied, int gapPx)
    {
        int best = 0;
        foreach (var g in Gaps(taskbar, tray, occupied, gapPx))
            if (g.Right - g.Left > best) best = g.Right - g.Left;
        return best;
    }

    /// <summary>
    /// 在空隙里落位：优先保持"紧贴托盘左边"的默认位；默认位被占就走能放下的**最靠右**的空档；
    /// 所有空档都放不下时退到最宽的那个（宁可挤进去，也不压到别人身上），并把 <paramref name="overlaps"/> 置 true。
    /// 相比"遇到障碍就往左跳一次"的两候选位版本，这里是按**真实空隙**挑位，多个工具共存时不会再叠。
    /// </summary>
    public static (int X, int Y) PlaceInGap(
        PixelRect taskbar, PixelRect tray, IReadOnlyList<PixelRect> occupied,
        int pillW, int pillH, int offset, int gapPx, out int appliedOffset, out bool overlaps)
    {
        overlaps = false;

        if (!IsHorizontal(taskbar))
        {
            var (px, py) = Place(taskbar, tray, pillW, pillH, offset, gapPx, out appliedOffset);
            return (px, py);
        }

        int y = taskbar.Top + (taskbar.Height - pillH) / 2;
        int bandLeft = taskbar.Left + EdgeInsetPx;
        int bandRight = (tray.Width > 0 ? tray.Left : taskbar.Right) - gapPx;
        int anchor = bandRight - pillW;                  // 默认位：右边界贴住可用带右端
        int preferred = anchor + offset;

        var gaps = Gaps(taskbar, tray, occupied, gapPx);

        // ① 默认位（含用户拖动偏移）落在某个空档里就直接用
        foreach (var g in gaps)
        {
            if (preferred >= g.Left && preferred + pillW <= g.Right)
            {
                appliedOffset = preferred - anchor;
                return (preferred, y);
            }
        }

        // ② 从右往左找第一个放得下的空档（离托盘最近）
        for (int i = gaps.Count - 1; i >= 0; i--)
        {
            var g = gaps[i];
            if (g.Right - g.Left >= pillW)
            {
                int x = g.Right - pillW;
                appliedOffset = x - anchor;
                return (x, y);
            }
        }

        // ③ 都放不下：塞进最宽的空档并标记重叠，调用方会先尝试"少画几台"再来
        int bestIdx = -1, bestW = -1;
        for (int i = 0; i < gaps.Count; i++)
        {
            int w = gaps[i].Right - gaps[i].Left;
            if (w > bestW) { bestW = w; bestIdx = i; }
        }

        overlaps = true;
        // 夹回可用带内：宁可叠住障碍物，也不能把尾巴伸进托盘区（那里是系统图标，盖了更糟）
        int maxFallback = Math.Max(bandLeft, bandRight - pillW);
        int fallback = bestIdx >= 0
            ? Math.Clamp(gaps[bestIdx].Left, bandLeft, maxFallback)
            : maxFallback;
        appliedOffset = fallback - anchor;
        return (fallback, y);
    }

    /// <summary>把记下偏移时的物理像素换算到当前 DPI；Dpi=0 或未变则原样返回（勿逐帧累乘，见 §16.5）。</summary>
    public static int ScaleOffset(int offsetPx, uint storedDpi, uint currentDpi)
    {
        if (storedDpi == 0 || currentDpi == 0 || storedDpi == currentDpi) return offsetPx;
        return (int)Math.Round(offsetPx * (double)currentDpi / storedDpi);
    }

    /// <summary>客户区 X 落在哪一格；分隔缝返回 -1。</summary>
    public static int HitTest(IReadOnlyList<(int X, int Width)> regions, int clientX)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            if (clientX >= r.X && clientX < r.X + r.Width) return i;
        }
        return -1;
    }

    /// <summary>
    /// 提示框左上角（屏幕坐标）：摆在任务栏朝桌面一侧，水平/竖直对准格子中心，再夹进工作区。
    /// </summary>
    public static (int X, int Y) PlaceTooltip(
        PixelRect taskbar, PixelRect chipScreen, int tipW, int tipH, int gapPx, PixelRect workArea)
    {
        int x, y;
        if (IsHorizontal(taskbar))
        {
            x = chipScreen.Left + (chipScreen.Width - tipW) / 2;
            // Top==0 视为顶栏，否则底栏（主屏底栏 Top 很大；副屏同理）
            y = taskbar.Top <= 0
                ? chipScreen.Bottom + gapPx
                : chipScreen.Top - tipH - gapPx;
        }
        else
        {
            y = chipScreen.Top + (chipScreen.Height - tipH) / 2;
            // Left==0 左停靠 → 提示往右伸到桌面；否则右停靠往左
            x = taskbar.Left <= 0
                ? chipScreen.Right + gapPx
                : chipScreen.Left - tipW - gapPx;
        }

        if (workArea.Width > 0 && workArea.Height > 0)
        {
            x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - tipW));
            y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - tipH));
        }
        return (x, y);
    }
}
