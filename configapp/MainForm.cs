using System;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UsbCdcGui;

internal sealed class MainForm : Form
{
    private readonly ComboBox _cbPorts = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Button _btnRefreshPorts = new() { Text = "Refresh", Width = 80 };
    private readonly Button _btnConnect = new() { Text = "Connect", Width = 90 };
    private readonly Label _lblStatus = new() { AutoSize = true, Text = "Disconnected" };

    private readonly Button _btnReadCals = new() { Text = "Read config", Width = 110 };
    private readonly Button _btnWriteCals = new() { Text = "Write config", Width = 110 };

    private readonly DataGridView _gridLive = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false };
    private readonly DataGridView _gridAnalog = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false };
    private readonly DataGridView _gridNtc = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false };

    private DeviceProtocol? _proto;
    private CancellationTokenSource? _pollCts;

    // Channel naming: 6 analog + 4 temp/NTC (matches config.h)
    private static readonly string[] LiveNames =
    {
        "AI0","AI1","AI2","AI3","AI4","AI5",
        "NTC0","NTC1","NTC2","NTC3"
    };

    public MainForm()
    {
        Text = "USB CDC Monitor + Config (0xAA/0xBB/0xCC)";
        MinimumSize = new Size(1100, 700);
        Font = new Font("Segoe UI", 9F);

        BuildLayout();
        BuildGrids();

        _btnRefreshPorts.Click += (_, _) => RefreshPorts();
        _btnConnect.Click += async (_, _) => await ToggleConnectAsync();
        _btnReadCals.Click += async (_, _) => await ReadCalsAsync();
        _btnWriteCals.Click += async (_, _) => await WriteCalsAsync();

        Load += (_, _) => RefreshPorts();
        FormClosing += (_, _) => Disconnect();
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(10, 10, 10, 0),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        top.Controls.Add(new Label { Text = "COM:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        top.Controls.Add(_cbPorts);
        top.Controls.Add(_btnRefreshPorts);
        top.Controls.Add(_btnConnect);
        top.Controls.Add(new Label { Text = "   ", AutoSize = true });
        top.Controls.Add(_btnReadCals);
        top.Controls.Add(_btnWriteCals);
        top.Controls.Add(new Label { Text = "   Status:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        top.Controls.Add(_lblStatus);

        var splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 260
        };

        var liveGroup = new GroupBox { Text = "Live values (getData 0xAA) – raw + mV + V", Dock = DockStyle.Fill, Padding = new Padding(10) };
        liveGroup.Controls.Add(_gridLive);
        splitMain.Panel1.Controls.Add(liveGroup);

        var splitCfg = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 520
        };

        var analogGroup = new GroupBox { Text = "Analog calibration (6ch) – low/high Cal + low/high V (getCals 0xBB)", Dock = DockStyle.Fill, Padding = new Padding(10) };
        analogGroup.Controls.Add(_gridAnalog);
        splitCfg.Panel1.Controls.Add(analogGroup);

        var ntcGroup = new GroupBox { Text = "NTC calibration (4ch) – r1/r2/r3 + t1/t2/t3 (getCals 0xBB)", Dock = DockStyle.Fill, Padding = new Padding(10) };
        ntcGroup.Controls.Add(_gridNtc);
        splitCfg.Panel2.Controls.Add(ntcGroup);

        splitMain.Panel2.Controls.Add(splitCfg);

        Controls.Add(splitMain);
        Controls.Add(top);
    }

    private void BuildGrids()
    {
        // Live grid
        _gridLive.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridLive.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Channel", ReadOnly = true });
        _gridLive.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Raw (u16)", ReadOnly = true });
        _gridLive.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Voltage (mV)", ReadOnly = true });
        _gridLive.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Voltage (V)", ReadOnly = true });

        _gridLive.Rows.Clear();
        for (int i = 0; i < LiveNames.Length; i++)
        {
            _gridLive.Rows.Add(LiveNames[i], "-", "-", "-");
        }

        // Analog cals
        _gridAnalog.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridAnalog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ch", ReadOnly = true, FillWeight = 20 });
        _gridAnalog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "lowCal (u16)" });
        _gridAnalog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "highCal (u16)" });
        _gridAnalog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "lowV (mV)" });
        _gridAnalog.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "highV (mV)" });

        _gridAnalog.Rows.Clear();
        for (int ch = 0; ch < 6; ch++)
            _gridAnalog.Rows.Add($"AI{ch}", 0, 0, 0, 0);

        // NTC cals
        _gridNtc.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridNtc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ch", ReadOnly = true, FillWeight = 20 });
        _gridNtc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "r1 (Ω)" });
        _gridNtc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "r2 (Ω)" });
        _gridNtc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "r3 (Ω)" });
        _gridNtc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "t1 (°C, i16)" });
        _gridNtc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "t2 (°C, i16)" });
        _gridNtc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "t3 (°C, i16)" });

        _gridNtc.Rows.Clear();
        for (int ch = 0; ch < 4; ch++)
            _gridNtc.Rows.Add($"NTC{ch}", 0, 0, 0, 0, 0, 0);
    }

    private void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();

        string? prev = _cbPorts.SelectedItem as string;

        _cbPorts.Items.Clear();
        _cbPorts.Items.AddRange(ports);

        if (ports.Length > 0)
        {
            int idx = prev != null ? Array.IndexOf(ports, prev) : 0;
            _cbPorts.SelectedIndex = idx >= 0 ? idx : 0;
        }
    }

    private async Task ToggleConnectAsync()
    {
        if (_proto is null || !_proto.IsOpen)
        {
            if (_cbPorts.SelectedItem is not string port)
            {
                SetStatus("No COM port selected.");
                return;
            }

            try
            {
                _proto = new DeviceProtocol(port);
                _proto.Open();
                _btnConnect.Text = "Disconnect";
                SetStatus($"Connected to {port}");
                StartPolling();
                // Read config once on connect (optional)
                await ReadCalsAsync();
            }
            catch (Exception ex)
            {
                SetStatus("Connect failed: " + ex.Message);
                Disconnect();
            }
        }
        else
        {
            Disconnect();
        }
    }

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_proto is null || !_proto.IsOpen)
                        break;

                    var live = await _proto.GetDataAsync(ct);
                    BeginInvoke(() => UpdateLiveGrid(live));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    BeginInvoke(() => SetStatus("Polling error: " + ex.Message));
                }

                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { }
            }
        }, ct);
    }

    private void Disconnect()
    {
        try { _pollCts?.Cancel(); } catch { }
        _pollCts = null;

        try { _proto?.Dispose(); } catch { }
        _proto = null;

        _btnConnect.Text = "Connect";
        SetStatus("Disconnected");
    }

    private void UpdateLiveGrid(DeviceProtocol.LiveData live)
    {
        // live.Values and live.Volts are 10 elements each (u16 little endian)
        int n = Math.Min(LiveNames.Length, Math.Min(live.Values.Length, live.Volts.Length));

        for (int i = 0; i < n; i++)
        {
            ushort raw = live.Values[i];
            ushort mv = live.Volts[i];
            double v = mv / 1000.0;

            _gridLive.Rows[i].Cells[1].Value = raw.ToString();
            _gridLive.Rows[i].Cells[2].Value = mv.ToString();
            _gridLive.Rows[i].Cells[3].Value = v.ToString("0.000");
        }
    }

    private async Task ReadCalsAsync()
    {
        if (_proto is null || !_proto.IsOpen)
        {
            SetStatus("Not connected.");
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var cals = await _proto.GetCalsAsync(cts.Token);

            // Fill analog grid
            for (int ch = 0; ch < 6; ch++)
            {
                _gridAnalog.Rows[ch].Cells[1].Value = cals.Analog[ch].LowCal;
                _gridAnalog.Rows[ch].Cells[2].Value = cals.Analog[ch].HighCal;
                _gridAnalog.Rows[ch].Cells[3].Value = cals.Analog[ch].LowVmv;
                _gridAnalog.Rows[ch].Cells[4].Value = cals.Analog[ch].HighVmv;
            }

            // Fill ntc grid
            for (int ch = 0; ch < 4; ch++)
            {
                _gridNtc.Rows[ch].Cells[1].Value = cals.Ntc[ch].R1;
                _gridNtc.Rows[ch].Cells[2].Value = cals.Ntc[ch].R2;
                _gridNtc.Rows[ch].Cells[3].Value = cals.Ntc[ch].R3;
                _gridNtc.Rows[ch].Cells[4].Value = cals.Ntc[ch].T1;
                _gridNtc.Rows[ch].Cells[5].Value = cals.Ntc[ch].T2;
                _gridNtc.Rows[ch].Cells[6].Value = cals.Ntc[ch].T3;
            }

            SetStatus("Config read OK.");
        }
        catch (Exception ex)
        {
            SetStatus("Read config failed: " + ex.Message);
        }
    }

    private async Task WriteCalsAsync()
    {
        if (_proto is null || !_proto.IsOpen)
        {
            SetStatus("Not connected.");
            return;
        }

        try
        {
            var cals = BuildCalsFromUi();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await _proto.WriteCalsAsync(cals, cts.Token);

            // No ACK in firmware; re-read to verify
            await Task.Delay(150);
            await ReadCalsAsync();

            SetStatus("Config written.");
        }
        catch (Exception ex)
        {
            SetStatus("Write config failed: " + ex.Message);
        }
    }

    private DeviceProtocol.DeviceCals BuildCalsFromUi()
    {
        var analog = new DeviceProtocol.AnalogCal[6];
        for (int ch = 0; ch < 6; ch++)
        {
            ushort lowCal  = ParseU16(_gridAnalog.Rows[ch].Cells[1].Value, "lowCal", ch);
            ushort highCal = ParseU16(_gridAnalog.Rows[ch].Cells[2].Value, "highCal", ch);
            ushort lowV    = ParseU16(_gridAnalog.Rows[ch].Cells[3].Value, "lowV", ch);
            ushort highV   = ParseU16(_gridAnalog.Rows[ch].Cells[4].Value, "highV", ch);
            analog[ch] = new DeviceProtocol.AnalogCal(lowCal, highCal, lowV, highV);
        }

        var ntc = new DeviceProtocol.NtcCal[4];
        for (int ch = 0; ch < 4; ch++)
        {
            uint r1 = ParseU32(_gridNtc.Rows[ch].Cells[1].Value, "r1", ch);
            uint r2 = ParseU32(_gridNtc.Rows[ch].Cells[2].Value, "r2", ch);
            uint r3 = ParseU32(_gridNtc.Rows[ch].Cells[3].Value, "r3", ch);
            short t1 = ParseI16(_gridNtc.Rows[ch].Cells[4].Value, "t1", ch);
            short t2 = ParseI16(_gridNtc.Rows[ch].Cells[5].Value, "t2", ch);
            short t3 = ParseI16(_gridNtc.Rows[ch].Cells[6].Value, "t3", ch);
            ntc[ch] = new DeviceProtocol.NtcCal(r1, r2, r3, t1, t2, t3);
        }

        return new DeviceProtocol.DeviceCals(analog, ntc);
    }

    private static ushort ParseU16(object? v, string field, int ch)
    {
        if (v is null) throw new InvalidDataException($"AI{ch} {field} is empty.");
        if (ushort.TryParse(v.ToString(), out var x)) return x;
        throw new InvalidDataException($"AI{ch} {field} invalid (must be 0..65535).");
    }

    private static uint ParseU32(object? v, string field, int ch)
    {
        if (v is null) throw new InvalidDataException($"NTC{ch} {field} is empty.");
        if (uint.TryParse(v.ToString(), out var x)) return x;
        throw new InvalidDataException($"NTC{ch} {field} invalid (must be 0..4294967295).");
    }

    private static short ParseI16(object? v, string field, int ch)
    {
        if (v is null) throw new InvalidDataException($"NTC{ch} {field} is empty.");
        if (short.TryParse(v.ToString(), out var x)) return x;
        throw new InvalidDataException($"NTC{ch} {field} invalid (must be -32768..32767).");
    }

    private void SetStatus(string s) => _lblStatus.Text = s;
}

internal static class ControlExtensions
{
    public static void BeginInvoke(this Control c, Action a)
    {
        if (c.IsHandleCreated) c.BeginInvoke(a);
    }
}
