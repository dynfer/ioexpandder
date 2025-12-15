using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace UsbCdcGui;

internal sealed class DeviceProtocol : IDisposable
{
    // Commands from embedded code:
    //   getData   = 0xAA
    //   getCals   = 0xBB
    //   writeCals = 0xCC
    private const byte CMD_GET_DATA   = 0xAA;
    private const byte CMD_GET_CALS   = 0xBB;
    private const byte CMD_WRITE_CALS = 0xCC;

    // Response IDs from embedded code:
    private const byte RSP_DATA            = 0x11;
    private const byte RSP_VOLTS           = 0x22;
    private const byte RSP_AV_CALS         = 0x33;
    private const byte RSP_AV_CALS_VOLTS   = 0x44;
    private const byte RSP_NTC_CALS        = 0x55;
    private const byte RSP_NTC_CALS_TEMPS  = 0x66;

    // Fixed sizes from embedded arrays (api.h/api.cpp)
    private const int SIZE_DATA_FRAME          = 21;  // 1 + 10*u16
    private const int SIZE_AV_CALS_FRAME       = 25;  // 1 + 6*(u16,u16)
    private const int SIZE_AV_CALS_V_FRAME     = 25;  // 1 + 6*(u16,u16)
    private const int SIZE_NTC_R_FRAME         = 49;  // 1 + 4*(u32,u32,u32)
    private const int SIZE_NTC_T_FRAME         = 25;  // 1 + 4*(i16,i16,i16)
    private const int SIZE_WRITE_CALS_BUFFER   = SIZE_AV_CALS_FRAME + SIZE_AV_CALS_V_FRAME + SIZE_NTC_R_FRAME + SIZE_NTC_T_FRAME; // 124

    private readonly SerialPort _sp;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public DeviceProtocol(string portName, int baudRate = 115200)
    {
        _sp = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 2000,
            WriteTimeout = 2000,
            DtrEnable = false,
            RtsEnable = false,
            NewLine = "\n"
        };
    }

    public bool IsOpen => _sp.IsOpen;

    public void Open()
    {
        if (_sp.IsOpen) return;
        _sp.Open();
        // some CDC firmwares reset on open; give it a moment
        try { _sp.DiscardInBuffer(); } catch { /* ignore */ }
    }

    public void Close()
    {
        if (!_sp.IsOpen) return;
        _sp.Close();
    }

    public void Dispose()
    {
        try { Close(); } catch { /* ignore */ }
        _sp.Dispose();
        _ioLock.Dispose();
    }

    // ---------- Public DTOs ----------
    internal sealed record LiveData(ushort[] Values, ushort[] Volts);
    internal sealed record AnalogCal(ushort LowCal, ushort HighCal, ushort LowVmv, ushort HighVmv);
    internal sealed record NtcCal(uint R1, uint R2, uint R3, short T1, short T2, short T3);
    internal sealed record DeviceCals(AnalogCal[] Analog, NtcCal[] Ntc);

    // ---------- Protocol ----------
    public async Task<LiveData> GetDataAsync(CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();

            // Keep it strict request/response.
            // If you ever get out of sync, Close/Open and retry.
            _sp.DiscardInBuffer();

            _sp.Write(new[] { CMD_GET_DATA }, 0, 1);

            var valsFrame  = await ReadExactAsync(SIZE_DATA_FRAME, ct).ConfigureAwait(false);
            var voltsFrame = await ReadExactAsync(SIZE_DATA_FRAME, ct).ConfigureAwait(false);

            if (valsFrame[0] != RSP_DATA)
                throw new InvalidDataException($"Bad dataResponse id: 0x{valsFrame[0]:X2}");
            if (voltsFrame[0] != RSP_VOLTS)
                throw new InvalidDataException($"Bad voltsResponse id: 0x{voltsFrame[0]:X2}");

            var values = ParseU16Array(valsFrame, 1, 10);
            var volts  = ParseU16Array(voltsFrame, 1, 10);

            return new LiveData(values, volts);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<DeviceCals> GetCalsAsync(CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            _sp.DiscardInBuffer();

            _sp.Write(new[] { CMD_GET_CALS }, 0, 1);

            var avCals     = await ReadExactAsync(SIZE_AV_CALS_FRAME, ct).ConfigureAwait(false);
            var avCalsVolt = await ReadExactAsync(SIZE_AV_CALS_V_FRAME, ct).ConfigureAwait(false);
            var ntcR       = await ReadExactAsync(SIZE_NTC_R_FRAME, ct).ConfigureAwait(false);
            var ntcT       = await ReadExactAsync(SIZE_NTC_T_FRAME, ct).ConfigureAwait(false);

            if (avCals[0] != RSP_AV_CALS) throw new InvalidDataException($"Bad avCalsResponse id: 0x{avCals[0]:X2}");
            if (avCalsVolt[0] != RSP_AV_CALS_VOLTS) throw new InvalidDataException($"Bad avCalsVoltResponse id: 0x{avCalsVolt[0]:X2}");
            if (ntcR[0] != RSP_NTC_CALS) throw new InvalidDataException($"Bad ntcCalsResponse id: 0x{ntcR[0]:X2}");
            if (ntcT[0] != RSP_NTC_CALS_TEMPS) throw new InvalidDataException($"Bad ntcCalsTempResponse id: 0x{ntcT[0]:X2}");

            var analog = new AnalogCal[6];
            for (int ch = 0; ch < 6; ch++)
            {
                int off = 1 + ch * 4;
                ushort lowCal  = ReadU16LE(avCals, off);
                ushort highCal = ReadU16LE(avCals, off + 2);

                ushort lowVmv  = ReadU16LE(avCalsVolt, off);
                ushort highVmv = ReadU16LE(avCalsVolt, off + 2);

                analog[ch] = new AnalogCal(lowCal, highCal, lowVmv, highVmv);
            }

            var ntc = new NtcCal[4];
            for (int ch = 0; ch < 4; ch++)
            {
                int offR = 1 + ch * 12;
                uint r1 = ReadU32LE(ntcR, offR);
                uint r2 = ReadU32LE(ntcR, offR + 4);
                uint r3 = ReadU32LE(ntcR, offR + 8);

                int offT = 1 + ch * 6;
                short t1 = ReadI16LE(ntcT, offT);
                short t2 = ReadI16LE(ntcT, offT + 2);
                short t3 = ReadI16LE(ntcT, offT + 4);

                ntc[ch] = new NtcCal(r1, r2, r3, t1, t2, t3);
            }

            return new DeviceCals(analog, ntc);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task WriteCalsAsync(DeviceCals cals, CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOpen();
            _sp.DiscardInBuffer();

            // Build buffer exactly like the device expects in api::writeCals():
            // offsets: 0,25,50,99 are frame IDs.
            byte[] buf = new byte[SIZE_WRITE_CALS_BUFFER];

            buf[0]  = RSP_AV_CALS;
            buf[25] = RSP_AV_CALS_VOLTS;
            buf[50] = RSP_NTC_CALS;
            buf[99] = RSP_NTC_CALS_TEMPS;

            // Analog cals (lowCal/highCal) -> starting at 1
            for (int ch = 0; ch < 6; ch++)
            {
                int off = 1 + ch * 4;
                WriteU16LE(buf, off,     cals.Analog[ch].LowCal);
                WriteU16LE(buf, off + 2, cals.Analog[ch].HighCal);
            }

            // Analog volts (lowV/highV) -> base 25, start at 25+1
            const int AV_VOLTS_BASE = 25;
            for (int ch = 0; ch < 6; ch++)
            {
                int off = AV_VOLTS_BASE + 1 + ch * 4;
                WriteU16LE(buf, off,     cals.Analog[ch].LowVmv);
                WriteU16LE(buf, off + 2, cals.Analog[ch].HighVmv);
            }

            // NTC resistances -> base 50, start at 50+1
            const int NTC_R_BASE = 50;
            for (int ch = 0; ch < 4; ch++)
            {
                int off = NTC_R_BASE + 1 + ch * 12;
                WriteU32LE(buf, off,     cals.Ntc[ch].R1);
                WriteU32LE(buf, off + 4, cals.Ntc[ch].R2);
                WriteU32LE(buf, off + 8, cals.Ntc[ch].R3);
            }

            // NTC temps -> base 99, start at 99+1 (signed int16)
            const int NTC_T_BASE = 99;
            for (int ch = 0; ch < 4; ch++)
            {
                int off = NTC_T_BASE + 1 + ch * 6;
                WriteI16LE(buf, off,     cals.Ntc[ch].T1);
                WriteI16LE(buf, off + 2, cals.Ntc[ch].T2);
                WriteI16LE(buf, off + 4, cals.Ntc[ch].T3);
            }

            // Send command byte, then payload
            _sp.Write(new[] { CMD_WRITE_CALS }, 0, 1);
            _sp.Write(buf, 0, buf.Length);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    // ---------- Helpers ----------
    private void EnsureOpen()
    {
        if (!_sp.IsOpen)
            throw new InvalidOperationException("Serial port not open.");
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        byte[] buf = new byte[count];
        int off = 0;

        // SerialPort.BaseStream supports ReadAsync in modern .NET
        Stream s = _sp.BaseStream;

        while (off < count)
        {
            int n = await s.ReadAsync(buf, off, count - off, ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("USB CDC disconnected while reading.");
            off += n;
        }

        return buf;
    }

    private static ushort[] ParseU16Array(byte[] frame, int startOffset, int count)
    {
        ushort[] outArr = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            int off = startOffset + i * 2;
            outArr[i] = ReadU16LE(frame, off);
        }
        return outArr;
    }

    private static ushort ReadU16LE(byte[] b, int off) =>
        (ushort)(b[off] | (b[off + 1] << 8));

    private static short ReadI16LE(byte[] b, int off) =>
        unchecked((short)ReadU16LE(b, off));

    private static uint ReadU32LE(byte[] b, int off) =>
        (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    private static void WriteU16LE(byte[] b, int off, ushort v)
    {
        b[off]     = (byte)(v & 0xFF);
        b[off + 1] = (byte)(v >> 8);
    }

    private static void WriteI16LE(byte[] b, int off, short v) =>
        WriteU16LE(b, off, unchecked((ushort)v));

    private static void WriteU32LE(byte[] b, int off, uint v)
    {
        b[off]     = (byte)(v & 0xFF);
        b[off + 1] = (byte)((v >> 8) & 0xFF);
        b[off + 2] = (byte)((v >> 16) & 0xFF);
        b[off + 3] = (byte)((v >> 24) & 0xFF);
    }
}
