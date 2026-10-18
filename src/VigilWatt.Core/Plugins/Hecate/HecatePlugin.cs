using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Hecate;

/// <summary>
/// 功 能： HECATE（漫步者电竞）2.4G 鼠标插件 —— 纯被动实现，不向设备写任何字节。
/// 2026/10/18 16:10 协议由实测抓包确定（G5M Pro + 35BB:D00B 接收器）。
///
/// 厂商通道：VID 0x35BB 的 <c>usage_page 0xFF1C / usage 0x0092</c>，64 字节进、64 字节出。
/// 该通道每 1~3 秒推两类报文：
/// <list type="bullet">
/// <item>心跳 <c>04 AA 00 AA 00 00 00 00 FF</c>（不含电量）</item>
/// <item>状态 <c>04 00 00 1A ... 电量 充电标志</c>（本插件读数来源）</item>
/// </list>
/// 帧布局见 <see cref="HecateCodec"/>。
/// </summary>
public sealed class HecatePlugin : BatteryPluginBase
{
    /// <summary>漫步者/漫步者电竞的 USB 厂商号。</summary>
    public const ushort HecateVid = 0x35BB;

    /// <summary>厂商自定义集合的 usage_page（实测值，非 0xFF00 那个常见页）。</summary>
    public const ushort VendorUsagePage = 0xFF1C;

    /// <summary>厂商自定义集合的 usage。</summary>
    public const ushort VendorUsage = 0x0092;

    public override string Name => "Hecate";
    public override string DisplayName => "HECATE";
    public override DeviceKind Kind => DeviceKind.Mouse;

    /// <summary>纯被动零写入 → 允许 15 秒快发现节拍，鼠标休眠唤醒后最坏等 15 秒就进列表。</summary>
    public override bool SupportsFastDiscovery => true;

    /// <summary>探测失败不是错误，只说明"鼠标睡着/没连"：冷却压到 5 秒，保证每拍都能重探。</summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(5);

    /// <summary>本插件没有任何写入路径；置 false 作为纵深防御（<see cref="BatteryPluginBase.CanWrite"/> 恒 false）。</summary>
    protected override bool WritesEnabled => false;

    /// <summary>探测只被动听状态帧、不发任何字节——不受写入开关约束。</summary>
    protected override bool ProbeWrites => false;

    /// <summary>认领后后台只读状态帧，电量变化/插拔线立即 StatusPushed。</summary>
    protected override bool SupportsPassiveListen => true;

    /// <summary>
    /// 休眠态（percent=null）时设备**真实在线**（指针正常，只是固件没推电量），
    /// 必须保持在线让 UI 显示 "--" + 唤醒提示；标离线会被贴片/列表藏掉，用户就不知道该去唤醒了。
    /// </summary>
    protected override bool NullPercentMeansOffline => false;

    /// <summary>休眠态在 UI 上的提示字幕：全新 2.4G 链路的电量推送是休眠的，要官方软件发查询才唤醒（2026-10-18 实测定案）。</summary>
    public const string DormantProtocol = "2.4G（电量推送休眠：打开一次 HECATE Connect 唤醒）";
    private bool _dormantHintLogged;

    /// <summary>
    /// 离线判定：
    /// <b>Confirm = 10 秒</b>：状态帧实测 1.5~3 秒一条，10 秒约 3~6 个周期，避免正常丢帧误判。
    /// <b>Silence = 60 秒</b>：接口彻底静音（鼠标关机/接收器被拔）才判离线。
    /// <b>CountPollFailures = false</b>：零写入，<see cref="BatteryPluginBase.PollDevice"/> 恒返回 null
    /// 是协议形态（没有查询命令），不是失联。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Confirm = TimeSpan.FromSeconds(10),
        Silence = TimeSpan.FromSeconds(60),
        CountPollFailures = false,
    };

    /// <summary>
    /// 候选接口：HECATE 的 VID + 厂商 usage_page，且输入/输出都是 64 字节。
    /// 接收器上另有键盘(21B/2B)、消费者控制(3B)、系统控制(2B) 等集合，靠 usage_page 天然排除。
    /// </summary>
    protected override bool IsCandidate(HidDeviceInfo info)
        => info.VendorId == HecateVid
           && info.UsagePage == VendorUsagePage
           && info.Usage == VendorUsage
           && info.InputReportLength >= HecateCodec.ReportLength
           && info.OutputReportLength >= HecateCodec.ReportLength;

    /// <summary>探测 = 被动听 6 秒状态帧；听不到返回 null 进冷却（鼠标休眠时就是这种状态）。</summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        => ProbeAsync(handle).GetAwaiter().GetResult();

    protected override async Task<(string Protocol, int? Percent, ChargingState Charging)?> ProbeAsync(
        HidDeviceHandle handle)
    {
        var read = await ListenStatusAsync(handle, TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        if (!read.HasValue)
        {
            // 听不到帧 ≠ 设备不在：全新 2.4G 链路的电量推送是休眠的，要 HECATE Connect 发一次查询才唤醒。
            // 仍然认领（在位不可读，UI 显示 "--"），让用户看得见设备；并提示一次唤醒办法。
            if (!_dormantHintLogged)
            {
                _dormantHintLogged = true;
                Log.Info("HECATE 设备在位但电量推送未激活（固件行为）：打开一次 HECATE Connect 即可唤醒，无需保持运行");
            }
            return (DormantProtocol, null, ChargingState.Unknown);
        }
        _dormantHintLogged = false;
        return ("2.4G", read.Value.Percent, read.Value.Charging);
    }

    /// <summary>读数由后台被动监听负责；轮询不再占 ReadInput（避免与监听抢同一悬空读）。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice _) => null;

    /// <summary>解析被动 input：0x04 + 标志 0x1A → 电量/充电；心跳与其它帧忽略（但基类已按"收到报文"续命）。</summary>
    protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice dev, byte[] input)
    {
        var status = HecateCodec.Parse(input);
        if (status.Kind == HecateFrameKind.NotStatus)
        {
            if (Log.IsEnabled(LogLevel.Debug) && HecateCodec.IsHeartbeat(input))
                Log.Debug($"HECATE 心跳: {Log.Hex(input)}");
            return null;
        }

        // 休眠态认领时 Protocol 带提示字幕，唤醒后恢复正式值（同一 Status 对象，Upsert 会带走）
        if (dev.Status.Protocol != "2.4G")
            dev.Status.Protocol = "2.4G";

        // 1.5~3 秒一帧，逐帧打 Info 会把日志冲爆 → 只在电量/充电变化时记一条。
        Log.InfoOnChange($"hecate-status:{dev.Status.Id}",
            $"{status.ChargeFlag}|{status.Percent}|{status.Charging}",
            $"HECATE 状态帧: 电量={status.Percent}% 充电标志=0x{status.ChargeFlag:X2} " +
            $"充电状态={status.Charging} 原始={Log.Hex(input)}");

        if (status.Kind == HecateFrameKind.StatusInvalidPercent)
        {
            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug($"HECATE 电量越界/归零（疑似休眠瞬态），丢弃: {Log.Hex(input)}");
            return null;
        }

        return (status.Percent, status.Charging, Offline: false);
    }

    /// <summary>在截止时间前收 input report，解析状态帧。</summary>
    private static async Task<(int Percent, ChargingState Charging)?> ListenStatusAsync(
        HidDeviceHandle handle, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var slice = remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500);
            if (slice <= TimeSpan.Zero) break;
            var input = await handle.ReadInputAsync(slice).ConfigureAwait(false);
            if (input == null) continue;
            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"HECATE 收到报文: {Log.Hex(input)}");

            var status = HecateCodec.Parse(input);
            if (status.Kind == HecateFrameKind.Status) return (status.Percent, status.Charging);
        }
        return null;
    }

    /// <summary>产品串本身就是完整型号（如 "HECATE G5M Pro"），直接用；读不到才退回品牌 + VID/PID。</summary>
    protected override string BuildDisplayName(HidDeviceInfo info)
        => string.IsNullOrWhiteSpace(info.Product) ? $"HECATE ({info.VidPid})" : info.Product;
}
