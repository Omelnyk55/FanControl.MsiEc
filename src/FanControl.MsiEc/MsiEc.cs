namespace FanControl.MsiEc;

/// <summary>
/// MSI laptop EC register map and high-level operations.
/// Standard MSI layout, confirmed live on 14B3EMS1.102 (PS42 Modern 8MO):
/// the firmware string is readable at 0xA0 and all values below decode sanely.
/// </summary>
internal sealed class MsiEc(AcpiEcIo ec)
{
    public const byte RegFwVersion = 0xA0;      // 12 ASCII chars, e.g. "14B3EMS1.102"
    public const int FwVersionLength = 12;

    public const byte RegFanMode = 0xF4;
    public const byte FanModeAuto = 0x0D;
    public const byte FanModeAdvanced = 0x8D;   // EC follows the curve tables verbatim

    public const int RpmDivisor = 478000;
    public const int CurvePoints = 7;

    // Safety floors for the two hottest curve bands (≈ >80 °C and >95..100 °C):
    // even with a low flat curve, an overheating component always gets airflow.
    private const int HotBandMinDuty = 80;
    private const int HotBandIndex = 5;
    private const int MaxBandIndex = 6;

    /// <summary>Per-fan register block. Key is used in sensor ids — keep stable.</summary>
    public sealed record FanRegs(string Key, byte Temp, byte Percent, byte RpmHi, byte RpmLo, byte CurveBase);

    public static readonly FanRegs Cpu = new("Cpu", Temp: 0x68, Percent: 0x71, RpmHi: 0xCC, RpmLo: 0xCD, CurveBase: 0x72);
    public static readonly FanRegs Gpu = new("Gpu", Temp: 0x80, Percent: 0x89, RpmHi: 0xCA, RpmLo: 0xCB, CurveBase: 0x8A);

    public readonly record struct FanStatus(float Temp, float Percent, float Rpm);

    public sealed record Snapshot(byte FanMode, byte[] CpuCurve, byte[] GpuCurve);

    public string ReadFirmwareVersion()
    {
        var addresses = new byte[FwVersionLength];
        for (var i = 0; i < addresses.Length; i++)
            addresses[i] = (byte)(RegFwVersion + i);

        var bytes = ec.ReadBytes(addresses);
        var chars = bytes.Select(b => b is >= 32 and <= 126 ? (char)b : '?').ToArray();
        return new string(chars);
    }

    public FanStatus ReadStatus(FanRegs fan)
    {
        var b = ec.ReadBytes(fan.Temp, fan.Percent, fan.RpmHi, fan.RpmLo);
        var raw = (b[2] << 8) | b[3];
        var rpm = raw == 0 ? 0f : (float)Math.Round((double)RpmDivisor / raw);
        return new FanStatus(b[0], b[1], rpm);
    }

    public byte ReadFanMode() => ec.ReadByte(RegFanMode);

    public Snapshot CaptureSnapshot() => new(ReadFanMode(), ReadCurve(Cpu), ReadCurve(Gpu));

    private byte[] ReadCurve(FanRegs fan)
    {
        var addresses = new byte[CurvePoints];
        for (var i = 0; i < CurvePoints; i++)
            addresses[i] = (byte)(fan.CurveBase + i);
        return ec.ReadBytes(addresses);
    }

    /// <summary>
    /// Direct fan control: switches the EC to advanced mode and writes a flat
    /// curve so the requested duty applies at any temperature, except the two
    /// hottest bands which keep their safety floors.
    /// </summary>
    public void ApplyFlatSpeed(FanRegs fan, int percent)
    {
        var duty = (byte)Math.Clamp(percent, 0, 100);
        var writes = new List<(byte, byte)>(CurvePoints + 1);

        if (ReadFanMode() != FanModeAdvanced)
            writes.Add((RegFanMode, FanModeAdvanced));

        for (var i = 0; i < HotBandIndex; i++)
            writes.Add(((byte)(fan.CurveBase + i), duty));
        writes.Add(((byte)(fan.CurveBase + HotBandIndex), Math.Max(duty, (byte)HotBandMinDuty)));
        writes.Add(((byte)(fan.CurveBase + MaxBandIndex), (byte)100));

        ec.WriteBytes(writes);
    }

    /// <summary>Restores one fan's curve table captured at startup.</summary>
    public void RestoreCurve(FanRegs fan, byte[] speeds)
    {
        var writes = new List<(byte, byte)>(CurvePoints);
        for (var i = 0; i < CurvePoints; i++)
            writes.Add(((byte)(fan.CurveBase + i), speeds[i]));
        ec.WriteBytes(writes);
    }

    public void RestoreMode(byte mode) => ec.WriteBytes([(RegFanMode, mode)]);
}
