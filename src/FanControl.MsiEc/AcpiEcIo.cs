using System.Diagnostics;

namespace FanControl.MsiEc;

/// <summary>
/// ACPI embedded controller access over PawnIO's LpcACPIEC module.
/// The module only allows raw byte I/O on ports 0x62/0x66; the EC read/write
/// handshake (commands 0x80/0x81, IBF/OBF polling) is implemented here.
/// All transactions are serialized through the system-wide "Access_EC" mutex
/// shared with other hardware-monitoring tools.
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
    private const int MutexTimeoutMs = 2000;
    private const int Retries = 2;

    private readonly IntPtr _handle;
    private readonly Mutex _mutex = new(false, @"Global\Access_EC");
    private bool _disposed;

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
                result[i] = ReadWithRetry(addresses[i]);
        }
        finally
        {
            _mutex.ReleaseMutex();
        }

        return result;
    }

    /// <summary>Writes address/value pairs; each write is read back and verified.</summary>
    public void WriteBytes(IReadOnlyList<(byte Address, byte Value)> writes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AcquireMutex();
        try
        {
            foreach (var (address, value) in writes)
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        WriteByteOnce(address, value);
                        var readBack = ReadWithRetry(address);
                        if (readBack == value)
                            break;
                        if (attempt >= Retries)
                            throw new IOException(
                                $"EC verify failed @0x{address:X2}: wrote 0x{value:X2}, read back 0x{readBack:X2}");
                    }
                    catch (TimeoutException) when (attempt < Retries)
                    {
                        Drain();
                    }
                }
            }
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
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
                Drain();
            }
        }
    }

    private byte ReadByteOnce(byte address)
    {
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC busy before read (IBF stuck)");
        OutB(PortCmd, CmdRead);
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC read timeout at address phase");
        OutB(PortData, address);
        if (!WaitFlag(StatObf, wantSet: true))
            throw new TimeoutException("EC read timeout at data phase");
        return InB(PortData);
    }

    private void WriteByteOnce(byte address, byte value)
    {
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC busy before write (IBF stuck)");
        OutB(PortCmd, CmdWrite);
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC write timeout at address phase");
        OutB(PortData, address);
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC write timeout at value phase");
        OutB(PortData, value);
        if (!WaitFlag(StatIbf, wantSet: false))
            throw new TimeoutException("EC write timeout at settle phase");
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
            if (++spins > 32)
                Thread.Sleep(1);
        }

        return false;
    }

    /// <summary>Flushes any stale byte left in the EC output buffer.</summary>
    private void Drain()
    {
        var guard = 0;
        while ((InB(PortCmd) & StatObf) != 0 && guard++ < 16)
            InB(PortData);
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
