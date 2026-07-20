using System.Diagnostics;

namespace FanControl.MsiEc;

/// <summary>
/// ACPI embedded controller access over PawnIO's LpcACPIEC module.
/// The module only allows raw byte I/O on ports 0x62/0x66; the EC read/write
/// handshake (commands 0x80/0x81, IBF/OBF polling) is implemented here.
///
/// Coexistence rules — the Windows ACPI driver uses the same ports and does
/// NOT honor the user-space Access_EC mutex, so:
///  - never read the data port unless the pending byte is known to be ours;
///    a response awaited by acpi.sys must be left for acpi.sys (stealing it
///    desyncs the kernel EC channel: ACPI event 15, broken battery status);
///  - if foreign data is pending, give the kernel a moment to consume it and
///    otherwise skip this cycle entirely;
///  - keep transactions rare and short (callers batch and throttle).
/// </summary>
internal sealed class AcpiEcIo : IDisposable
{
    private const int PortData = 0x62;
    private const int PortCmd = 0x66;
    private const byte StatObf = 0x01;
    private const byte StatIbf = 0x02;
    private const byte CmdRead = 0x80;
    private const byte CmdWrite = 0x81;

    private const int WaitTimeoutMs = 300;
    private const int ForeignDataWaitMs = 150;
    private const int MutexTimeoutMs = 2000;
    private const int Retries = 2;
    private const int RetryPauseMs = 30;
    private const int PacingEveryNOps = 3;
    private const int PacingPauseMs = 15;

    private readonly IntPtr _handle;
    private readonly Mutex _mutex = new(false, @"Global\Access_EC");
    private bool _disposed;
    private bool _protocolDirty;    // our previous transaction was abandoned mid-flight
    private int _foreignBusyStreak; // consecutive skips due to pending foreign data

    public uint LibVersion { get; }

    public AcpiEcIo()
    {
        Check(PawnIoNative.pawnio_version(out var version), "pawnio_version");
        LibVersion = version;

        Check(PawnIoNative.pawnio_open(out _handle), "pawnio_open");
        try
        {
            var blob = LoadModuleBlob();
            Check(PawnIoNative.pawnio_load(_handle, blob, (nuint)blob.Length), "pawnio_load");
        }
        catch
        {
            PawnIoNative.pawnio_close(_handle);
            throw;
        }
    }

    private static byte[] LoadModuleBlob()
    {
        using var stream = typeof(AcpiEcIo).Assembly.GetManifestResourceStream("FanControl.MsiEc.Modules.LpcACPIEC.bin")
            ?? throw new InvalidOperationException("Embedded resource LpcACPIEC.bin not found");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new IOException($"{what} failed (HRESULT 0x{hr:X8})");
    }

    public byte ReadByte(byte address) => ReadBytes(address)[0];

    public byte[] ReadBytes(params byte[] addresses)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new byte[addresses.Length];
        AcquireMutex();
        try
        {
            for (var i = 0; i < addresses.Length; i++)
            {
                result[i] = ReadWithRetry(addresses[i]);
                Pace(i);
            }
        }
        finally
        {
            _mutex.ReleaseMutex();
        }

        return result;
    }

    /// <summary>
    /// Writes address/value pairs. With <paramref name="verify"/> each write is
    /// read back and compared — reserve it for rare, critical writes; routine
    /// writes skip it to halve EC traffic.
    /// </summary>
    public void WriteBytes(IReadOnlyList<(byte Address, byte Value)> writes, bool verify = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AcquireMutex();
        try
        {
            for (var i = 0; i < writes.Count; i++)
            {
                var (address, value) = writes[i];
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        WriteByteOnce(address, value);
                        if (verify)
                        {
                            var readBack = ReadByteOnce(address);
                            if (readBack != value)
                            {
                                if (attempt >= Retries)
                                    throw new IOException(
                                        $"EC verify failed @0x{address:X2}: wrote 0x{value:X2}, read back 0x{readBack:X2}");
                                Thread.Sleep(RetryPauseMs);
                                continue;
                            }
                        }

                        break;
                    }
                    catch (TimeoutException) when (attempt < Retries)
                    {
                        Thread.Sleep(RetryPauseMs);
                    }
                }

                Pace(i);
            }
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    private static void Pace(int index)
    {
        if ((index + 1) % PacingEveryNOps == 0)
            Thread.Sleep(PacingPauseMs);
    }

    private void AcquireMutex()
    {
        bool acquired;
        try
        {
            acquired = _mutex.WaitOne(MutexTimeoutMs);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // previous holder died; the mutex is ours now
        }

        if (!acquired)
            throw new TimeoutException("Timed out waiting for the Access_EC mutex");
    }

    private byte ReadWithRetry(byte address)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return ReadByteOnce(address);
            }
            catch (TimeoutException) when (attempt < Retries)
            {
                Thread.Sleep(RetryPauseMs);
            }
        }
    }

    /// <summary>
    /// Handles a byte sitting in the EC output buffer before we start a
    /// transaction: after our own abandoned transaction it is ours to flush;
    /// otherwise it belongs to the ACPI driver — wait briefly for the kernel
    /// to take it and skip the cycle if it doesn't.
    /// </summary>
    private void PrepareForTransaction()
    {
        if ((InB(PortCmd) & StatObf) == 0)
        {
            _foreignBusyStreak = 0;
            return;
        }

        // After 3 refusals in a row nobody has claimed the byte — consider it
        // stale (e.g. left over from a crashed EC client) and flush after all.
        if (_protocolDirty || _foreignBusyStreak >= 3)
        {
            var guard = 0;
            while ((InB(PortCmd) & StatObf) != 0 && guard++ < 4)
                InB(PortData);
            _protocolDirty = false;
            _foreignBusyStreak = 0;
            return;
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ForeignDataWaitMs)
        {
            if ((InB(PortCmd) & StatObf) == 0)
            {
                _foreignBusyStreak = 0;
                return;
            }

            Thread.Sleep(5);
        }

        _foreignBusyStreak++;
        throw new TimeoutException("EC busy: pending data belongs to the ACPI driver");
    }

    private byte ReadByteOnce(byte address)
    {
        PrepareForTransaction();
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC busy before read (IBF stuck)");
        OutB(PortCmd, CmdRead);
        _protocolDirty = true; // in flight: if we bail now, the response byte is ours
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC read timeout at address phase");
        OutB(PortData, address);
        if (!WaitFlag(StatObf, wantSet: true))
            throw new TimeoutException("EC read timeout at data phase");
        var value = InB(PortData);
        _protocolDirty = false;
        return value;
    }

    private void WriteByteOnce(byte address, byte value)
    {
        PrepareForTransaction();
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC busy before write (IBF stuck)");
        OutB(PortCmd, CmdWrite);
        _protocolDirty = true;
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC write timeout at address phase");
        OutB(PortData, address);
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC write timeout at value phase");
        OutB(PortData, value);
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC write timeout at settle phase");
        _protocolDirty = false;
    }

    private bool WaitFlag(byte mask, bool wantSet)
    {
        var sw = Stopwatch.StartNew();
        var spins = 0;
        while (sw.ElapsedMilliseconds < WaitTimeoutMs)
        {
            var status = InB(PortCmd);
            if (((status & mask) != 0) == wantSet)
                return true;
            if (++spins > 16)
                Thread.Sleep(1);
        }

        return false;
    }

    private byte InB(int port)
    {
        var input = new ulong[] { (ulong)port };
        var output = new ulong[1];
        Check(PawnIoNative.pawnio_execute(_handle, "ioctl_pio_read", input, 1, output, 1, out _), "ioctl_pio_read");
        return (byte)output[0];
    }

    private void OutB(int port, byte value)
    {
        var input = new ulong[] { (ulong)port, value };
        Check(PawnIoNative.pawnio_execute(_handle, "ioctl_pio_write", input, 2, [], 0, out _), "ioctl_pio_write");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        PawnIoNative.pawnio_close(_handle);
        _mutex.Dispose();
    }
}
