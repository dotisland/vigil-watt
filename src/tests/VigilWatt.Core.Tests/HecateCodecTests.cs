using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.Hecate;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： HecateCodec —— HECATE 2.4G 状态帧（report 0x04，64 字节）的纯解析。
/// 2026/10/18 16:10 用例帧全部取自真机抓包（G5M Pro + 35BB:D00B 接收器，插线充电中）。
/// </summary>
public class HecateCodecTests
{
    /// <summary>真机 16:03：充电中，电量 22%，充电标志 0x01。</summary>
    private static readonly byte[] StatusFrame = BuildFrame(percent: 0x16, chargeFlag: 0x01);

    /// <summary>真机：周期心跳帧，不含电量。</summary>
    private static readonly byte[] HeartbeatFrame =
        Convert.FromHexString("04AA00AA00000000FF" + new string('0', (64 - 9) * 2));

    private static byte[] BuildFrame(byte percent, byte chargeFlag, byte marker = 0x1A)
    {
        var f = new byte[64];
        f[0] = 0x04;          // report ID
        f[3] = marker;        // 状态帧标志
        f[8] = percent;
        f[9] = chargeFlag;
        return f;
    }

    private static byte[] WithByte(byte[] frame, int index, byte value)
    {
        var copy = (byte[])frame.Clone();
        copy[index] = value;
        return copy;
    }

    [Fact]
    public void Parses_charging_status_frame()
    {
        var s = HecateCodec.Parse(StatusFrame);

        Assert.Equal(HecateFrameKind.Status, s.Kind);
        Assert.Equal(22, s.Percent);
        Assert.Equal(ChargingState.Charging, s.Charging);
        Assert.Equal(0x01, s.ChargeFlag);
    }

    [Fact]
    public void Heartbeat_is_not_a_status_frame()
    {
        var s = HecateCodec.Parse(HeartbeatFrame);

        Assert.Equal(HecateFrameKind.NotStatus, s.Kind);
        Assert.True(HecateCodec.IsHeartbeat(HeartbeatFrame));
    }

    [Fact]
    public void Wrong_report_id_is_not_status()
    {
        Assert.Equal(HecateFrameKind.NotStatus, HecateCodec.Parse(WithByte(StatusFrame, 0, 0x05)).Kind);
    }

    [Fact]
    public void Wrong_marker_is_not_status()
    {
        Assert.Equal(HecateFrameKind.NotStatus, HecateCodec.Parse(WithByte(StatusFrame, 3, 0x00)).Kind);
    }

    [Fact]
    public void Short_frame_is_not_status()
    {
        Assert.Equal(HecateFrameKind.NotStatus, HecateCodec.Parse(new byte[8]).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(0xFF)]
    public void Out_of_range_percent_is_rejected(byte percent)
    {
        var s = HecateCodec.Parse(BuildFrame(percent, 0x01));

        Assert.Equal(HecateFrameKind.StatusInvalidPercent, s.Kind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Boundary_percent_is_accepted(byte percent)
    {
        var s = HecateCodec.Parse(BuildFrame(percent, 0x01));

        Assert.Equal(HecateFrameKind.Status, s.Kind);
        Assert.Equal(percent, s.Percent);
    }

    [Fact]
    public void Full_battery_reports_full_regardless_of_flag()
    {
        var s = HecateCodec.Parse(BuildFrame(100, 0x00));

        Assert.Equal(ChargingState.Full, s.Charging);
    }

    [Theory]
    [InlineData(0x00, ChargingState.Discharging)]
    [InlineData(0x01, ChargingState.Charging)]
    [InlineData(0x02, ChargingState.Full)]
    [InlineData(0x07, ChargingState.Unknown)]
    public void Charge_flag_mapping(byte flag, ChargingState expected)
    {
        var s = HecateCodec.Parse(BuildFrame(50, flag));

        Assert.Equal(expected, s.Charging);
    }
}
