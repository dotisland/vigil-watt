using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Hecate;

/// <summary>0x04 状态帧的解析结果类型。</summary>
internal enum HecateFrameKind
{
    /// <summary>不是本协议的状态帧（心跳帧 0xAA、鼠标移动帧等都归这里）。</summary>
    NotStatus,

    /// <summary>状态帧且电量落在 1~100。</summary>
    Status,

    /// <summary>状态帧但电量越界（0 / &gt;100，实测为休眠/唤醒瞬态）：不能用来更新读数。</summary>
    StatusInvalidPercent,
}

/// <summary>0x04 状态帧的解析结果。Kind 决定其余字段是否有意义。</summary>
internal readonly record struct HecateStatus(
    HecateFrameKind Kind, int Percent, ChargingState Charging, byte ChargeFlag);

/// <summary>
/// 功 能： HECATE（漫步者电竞）2.4G 鼠标状态帧（report 0x04，64 字节）的**纯帧解析**。
/// 不碰句柄、不写日志，便于单测与复用。
///
/// 帧布局（G5M Pro 实测，2026-10-18）：
/// <code>
/// 偏移  0     1   2   3     4..7   8      9        10..
///      0x04  00  00  0x1A  00..   电量%  充电标志  全零
/// </code>
/// 对照证据：充电中 10 分钟内 byte8 由 0x14→0x15→0x16（20%→21%→22%），
/// 与 HECATE Connect 显示值一致；byte9 在插线充电时为 0x01。
/// 另有心跳帧 <c>04 AA 00 AA 00 00 00 00 FF</c>（每 1~3 秒一条），本协议只用于判定链路还活着。
/// </summary>
internal static class HecateCodec
{
    /// <summary>状态帧的 HID report ID。</summary>
    public const byte StatusReportId = 0x04;

    /// <summary>厂商集合的输入报文长度（含 report ID）。HECATE 的 vendor collection 是 64 进 64 出。</summary>
    public const int ReportLength = 64;

    /// <summary>状态帧标志位所在偏移：0x1A 表示"这是状态帧"。</summary>
    public const int StatusMarkerIndex = 3;

    /// <summary>状态帧标志值。</summary>
    public const byte StatusMarker = 0x1A;

    /// <summary>电量百分比所在偏移。</summary>
    public const int PercentIndex = 8;

    /// <summary>充电标志所在偏移。</summary>
    public const int ChargeFlagIndex = 9;

    /// <summary>
    /// 解析一帧。<see cref="HecateStatus.Kind"/> 为 <see cref="HecateFrameKind.NotStatus"/> 时
    /// 说明这不是本协议的状态帧（其余字段无意义）。
    /// </summary>
    public static HecateStatus Parse(byte[] input)
    {
        if (input.Length < ReportLength) return NotStatus();
        if (input[0] != StatusReportId) return NotStatus();
        if (input[StatusMarkerIndex] != StatusMarker) return NotStatus();

        byte flag = input[ChargeFlagIndex];
        byte pct = input[PercentIndex];

        // 越界值只可能是休眠/唤醒瞬态（dongle 在设备关机瞬间推 0），不能拿去更新 UI。
        if (pct is 0 or > 100)
            return new HecateStatus(HecateFrameKind.StatusInvalidPercent, pct, ChargingState.Unknown, flag);

        // 充电标志三个值均已真机实测（HECATE G5M Pro）：
        //   0x01 = 充电中（插 USB 线，电量随时间上升）
        //   0x00 = 放电（拔线，同一电量下立即变 0x00，两次独立确认）
        //   0x02 = 满电（100% 时恒为 0x02）
        // 注意与英菲克相反（英菲克 0x01=放电），不可照抄；其余取值一律 Unknown，不猜。
        var charging = flag switch
        {
            0x01 => ChargingState.Charging,
            0x02 => ChargingState.Full,
            0x00 => ChargingState.Discharging,
            _ => ChargingState.Unknown,
        };
        if (pct >= 100) charging = ChargingState.Full;

        return new HecateStatus(HecateFrameKind.Status, pct, charging, flag);
    }

    /// <summary>是否为心跳帧（<c>04 AA 00 AA ...</c>）——只用于日志可读性，不参与电量解析。</summary>
    public static bool IsHeartbeat(byte[] input)
        => input.Length >= 8 && input[0] == StatusReportId && input[1] == 0xAA && input[3] == 0xAA;

    private static HecateStatus NotStatus() =>
        new(HecateFrameKind.NotStatus, 0, ChargingState.Unknown, 0);
}
