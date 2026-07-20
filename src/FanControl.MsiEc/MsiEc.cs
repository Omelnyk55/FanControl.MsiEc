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

    // Cooler Boost: MSI's "fans to max NOW" switch. Unlike curve-table writes,
    // it bypasses the EC's ~1 %/s internal PWM slew (measured: 3000→6200 RPM
    // in 2 s vs 56 s through the table path on 14B3EMS1.102).
    public const byte RegCoolerBoost = 0x98;
    private const byte CoolerBoostBit = 0x80;

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

    /// <summary>
    /// CoolerBoostBase is the boost register's non-boost bits captured while the
    /// EC was healthy — all later boost writes are absolute values derived from
    /// it, never read-modify-write of a live (possibly corrupted) read.
    /// </summary>
    public sealed record Snapshot(byte FanMode, byte[] CpuCurve, byte[] GpuCurve, byte CoolerBoostBase);

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

    public Snapshot CaptureSnapshot() =>
        new(ReadFanMode(), ReadCurve(Cpu), ReadCurve(Gpu), (byte)(ec.ReadByte(RegCoolerBoost) & ~CoolerBoostBit));

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
    /// hottest bands which keep their safety floors. Routine writes are not
    /// individually verified — a single canary read-back catches a dropped
    /// batch at a fraction of the EC traffic.
    /// </summary>
    public void ApplyFlatSpeed(FanRegs fan, int percent)
    {
        var duty = (byte)Math.Clamp(percent, 0, 100);

        if (ReadFanMode() != FanModeAdvanced)
            ec.WriteBytes([(RegFanMode, FanModeAdvanced)], verify: true);

        var writes = new List<(byte, byte)>(CurvePoints);
        for (var i = 0; i < HotBandIndex; i++)
            writes.Add(((byte)(fan.CurveBase + i), duty));
        writes.Add(((byte)(fan.CurveBase + HotBandIndex), Math.Max(duty, (byte)HotBandMinDuty)));
        writes.Add(((byte)(fan.CurveBase + MaxBandIndex), (byte)100));

        // A canary mismatch can be a transient collision — retry once before
        // declaring the write failed.
        for (var attempt = 0; ; attempt++)
        {
            ec.WriteBytes(writes);
            if (ec.ReadByte(fan.CurveBase) == duty)
                return;
            if (attempt >= 1)
                throw new IOException($"EC did not accept curve write @0x{fan.CurveBase:X2}");
            Thread.Sleep(100);
        }
    }

    /// <summary>Restores one fan's curve table captured at startup (verified).</summary>
    public void RestoreCurve(FanRegs fan, byte[] speeds)
    {
        var writes = new List<(byte, byte)>(CurvePoints);
        for (var i = 0; i < CurvePoints; i++)
            writes.Add(((byte)(fan.CurveBase + i), speeds[i]));
        ec.WriteBytes(writes, verify: true);
    }

    public void RestoreMode(byte mode) => ec.WriteBytes([(RegFanMode, mode)], verify: true);

    public bool ReadCoolerBoost() => (ec.ReadByte(RegCoolerBoost) & CoolerBoostBit) != 0;

    /// <summary>
    /// Writes an absolute boost register value composed from the snapshot's
    /// known-good base bits. Never read-modify-write: a corrupted live read
    /// must not be amplified into a corrupted write.
    /// </summary>
    public void SetCoolerBoost(bool on, byte baseBits)
    {
        var value = (byte)(on ? baseBits | CoolerBoostBit : baseBits);
        ec.WriteBytes([(RegCoolerBoost, value)]);
    }
}
