using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Windows.Forms;

// .NET SDKs can bring System.Threading into scope via implicit/global usings,
// which makes "Timer" ambiguous. Force WinForms timer here.
using Timer = System.Windows.Forms.Timer;

namespace UsbCdcConfigApp
{
    public sealed class MainForm : Form
    {
        // ---- Protocol (from firmware) ----
        private const byte CMD_GET_DATA   = 0xAA;
        private const byte CMD_GET_CALS   = 0xBB;
        private const byte CMD_WRITE_CALS = 0xCC;

        private const byte RSP_DATA        = 0x11; // 21 bytes
        private const byte RSP_VOLTS       = 0x22; // 21 bytes
        private const byte RSP_AV_CALS     = 0x33; // 25 bytes
        private const byte RSP_AV_CALS_V   = 0x44; // 25 bytes
        private const byte RSP_NTC_R       = 0x55; // 49 bytes
        private const byte RSP_NTC_T       = 0x66; // 25 bytes
        private const byte RSP_FACTORS     = 0x77; // 7 bytes

        private const int NUM_ANALOG = 6;
        private const int NUM_NTC = 4;
        private const int NUM_CHANNELS = NUM_ANALOG + NUM_NTC; // 10

        // ---- UI ----
        private readonly ComboBox _portCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        private readonly Button _refreshPorts = new() { Text = "Refresh" };
        private readonly Button _connectBtn = new() { Text = "Connect" };
        private readonly Label _status = new() { AutoSize = true, Text = "Disconnected" };

        private readonly Button _readCfgBtn = new() { Text = "Read config" };
        private readonly Button _writeCfgBtn = new() { Text = "Write config" };

        private readonly CheckBox _pollLive = new() { Text = "Poll live data", Checked = true, AutoSize = true };
        private readonly NumericUpDown _pollMs = new() { Minimum = 20, Maximum = 2000, Increment = 10, Value = 100, Width = 80 };

        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

        private readonly DataGridView _liveGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        private readonly DataGridView _analogGrid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        private readonly DataGridView _ntcGrid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };

        // ---- Serial + parsing ----
        private readonly SerialPort _sp = new();
        private readonly object _rxLock = new();
        private readonly List<byte> _rx = new();

        private readonly Timer _pollTimer = new();

        // DataGridViewComboBoxColumn is picky: the cell Value must match an item in the DataSource.
        // We therefore bind the column to a (Value, Text) list and always store the enum value.
        private sealed record ScalingItem(Scaling Value, string Text);

        private static readonly List<ScalingItem> ScalingItems = new()
        {
            new(Scaling.X1,     "X1 (/1)"),
            new(Scaling.X10,    "X10 (/10)"),
            new(Scaling.X100,   "X100 (/100)"),
            new(Scaling.X1000,  "X1000 (/1000)"),
            new(Scaling.X10000, "X10000 (/10000)")
        };

        // ---- Live state ----
        private readonly ushort[] _rawValues = new ushort[NUM_CHANNELS];
        private readonly ushort[] _mV = new ushort[NUM_CHANNELS];
        private readonly Scaling[] _factors = Enumerable.Repeat(Scaling.X1, NUM_ANALOG).ToArray();

        private bool _haveVals, _haveVolts;

        // ---- Config state ----
        private AnalogCal[] _analogCals = new AnalogCal[NUM_ANALOG];
        private NtcCal[] _ntcCals = new NtcCal[NUM_NTC];

        private bool _cfgGotAv, _cfgGotAvV, _cfgGotNtcR, _cfgGotNtcT, _cfgGotFactors;

        public MainForm()
        {
            Text = "USB CDC Config App";
            Width = 1100;
            Height = 700;
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();

            _sp.BaudRate = 115200;
            _sp.DataBits = 8;
            _sp.Parity = Parity.None;
            _sp.StopBits = StopBits.One;
            _sp.Handshake = Handshake.None;
            _sp.ReadTimeout = 200;
            _sp.WriteTimeout = 200;
            _sp.DataReceived += (_, __) => OnSerialData();

            _pollTimer.Tick += (_, __) => PollTick();
            _pollTimer.Interval = (int)_pollMs.Value;
            _pollMs.ValueChanged += (_, __) => _pollTimer.Interval = (int)_pollMs.Value;

            _refreshPorts.Click += (_, __) => RefreshPorts();
            _connectBtn.Click += (_, __) => ToggleConnect();

            _readCfgBtn.Click += (_, __) => RequestConfig();
            _writeCfgBtn.Click += (_, __) => WriteConfigToDevice();

            _pollLive.CheckedChanged += (_, __) =>
            {
                if (!_sp.IsOpen) return;
                if (_pollLive.Checked) _pollTimer.Start();
                else _pollTimer.Stop();
            };

            FormClosing += (_, __) =>
            {
                try { _pollTimer.Stop(); } catch { }
                try { if (_sp.IsOpen) _sp.Close(); } catch { }
            };

            InitModels();
            InitGrids();

            // Prevent the default WinForms “DataGridView Default Error Dialog”.
            // We'll handle invalid values ourselves (and keep the UI usable).
            _analogGrid.DataError += (_, e) =>
            {
                e.ThrowException = false;
                // Common case: factor value isn't in the list yet (e.g. out-of-range byte, or typed text).
                if (e.ColumnIndex >= 0 && _analogGrid.Columns[e.ColumnIndex].Name == "factor")
                    SetStatus("Invalid scaling factor value.");
            };

            // Force the scaling factor to be selected from the list (prevents free-typed strings).
            _analogGrid.EditingControlShowing += (_, e) =>
            {
                if (_analogGrid.CurrentCell?.OwningColumn?.Name != "factor") return;
                if (e.Control is ComboBox cb)
                {
                    cb.DropDownStyle = ComboBoxStyle.DropDownList;
                }
            };

            // Keep live scaling in sync with edits in the config grid.
            // (ComboBox edits don't always commit immediately unless we commit the cell.)
            _analogGrid.CurrentCellDirtyStateChanged += (_, __) =>
            {
                if (_analogGrid.IsCurrentCellDirty)
                    _analogGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            _analogGrid.CellValueChanged += (_, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (e.RowIndex >= NUM_ANALOG) return;
                if (_analogGrid.Columns[e.ColumnIndex].Name != "factor") return;

                var cellVal = _analogGrid.Rows[e.RowIndex].Cells["factor"].Value;
                var s = ParseScaling(cellVal);
                _analogCals[e.RowIndex].Factor = s;
                _factors[e.RowIndex] = s;
                UpdateLiveUi();
            };

            RefreshPorts();
        }

        private void BuildUi()
        {
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(8, 8, 8, 0),
                WrapContents = false,
                AutoScroll = true
            };

            top.Controls.Add(new Label { Text = "Port:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            top.Controls.Add(_portCombo);
            top.Controls.Add(_refreshPorts);
            top.Controls.Add(_connectBtn);

            top.Controls.Add(new Label { Text = " | ", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            top.Controls.Add(_pollLive);
            top.Controls.Add(new Label { Text = "Period [ms]:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            top.Controls.Add(_pollMs);

            top.Controls.Add(new Label { Text = " | ", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            top.Controls.Add(_readCfgBtn);
            top.Controls.Add(_writeCfgBtn);

            top.Controls.Add(new Label { Text = " | ", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            top.Controls.Add(_status);

            Controls.Add(_tabs);
            Controls.Add(top);

            var liveTab = new TabPage("Live") { Padding = new Padding(6) };
            liveTab.Controls.Add(_liveGrid);

            var cfgTab = new TabPage("Config") { Padding = new Padding(6) };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 280
            };

            var analogBox = new GroupBox { Text = "Analog calibration (6)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            analogBox.Controls.Add(_analogGrid);

            var ntcBox = new GroupBox { Text = "NTC calibration (4)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            ntcBox.Controls.Add(_ntcGrid);

            split.Panel1.Controls.Add(analogBox);
            split.Panel2.Controls.Add(ntcBox);

            cfgTab.Controls.Add(split);

            _tabs.TabPages.Add(liveTab);
            _tabs.TabPages.Add(cfgTab);

            // Factor tooltip
            var tip = new ToolTip();
            tip.SetToolTip(_writeCfgBtn, "Sends all calibration blocks to the device (incl. scaling factors).");
            tip.SetToolTip(_readCfgBtn, "Requests calibration blocks from the device.");
            tip.SetToolTip(_pollLive, "Continuously requests live values from the device.");
            tip.SetToolTip(_pollMs, "Polling interval for live data requests.");
        }

        private void InitModels()
        {
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                _analogCals[i] = new AnalogCal
                {
                    LowV = 500,
                    HighV = 4650,
                    LowCal = 20,
                    HighCal = 300,
                    Factor = Scaling.X1
                };
            }

            for (int i = 0; i < NUM_NTC; i++)
            {
                _ntcCals[i] = new NtcCal
                {
                    R1 = 32000,
                    R2 = 16000,
                    R3 = 2000,
                    T1 = -40,
                    T2 = 18,
                    T3 = 70
                };
            }
        }

        private void InitGrids()
        {
            // Live grid columns
            _liveGrid.Columns.Clear();
            _liveGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ch", HeaderText = "Channel", FillWeight = 110 });
            _liveGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "v", HeaderText = "Voltage [V]" });
            _liveGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "raw", HeaderText = "Raw value (uint16)" });
            _liveGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "factor", HeaderText = "Factor" });
            _liveGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "disp", HeaderText = "Displayed value (raw / factor)" });

            _liveGrid.Rows.Clear();
            for (int i = 0; i < NUM_CHANNELS; i++)
            {
                string name = i < NUM_ANALOG ? $"AI{i}" : $"NTC{i - NUM_ANALOG}";
                _liveGrid.Rows.Add(name, "—", "—", "—", "—");
            }

            // Analog grid columns
            _analogGrid.Columns.Clear();
            _analogGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ch", HeaderText = "Channel", ReadOnly = true, FillWeight = 70 });

            _analogGrid.Columns.Add(MakeIntCol("lowV", "LowV [mV]"));
            _analogGrid.Columns.Add(MakeIntCol("highV", "HighV [mV]"));
            _analogGrid.Columns.Add(MakeIntCol("lowCal", "LowCal"));
            _analogGrid.Columns.Add(MakeIntCol("highCal", "HighCal"));

            var factorCol = new DataGridViewComboBoxColumn
            {
                Name = "factor",
                HeaderText = "Factor",
                DataSource = ScalingItems,
                DisplayMember = nameof(ScalingItem.Text),
                ValueMember = nameof(ScalingItem.Value),
                ValueType = typeof(Scaling),
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
                FlatStyle = FlatStyle.Flat
            };
            _analogGrid.Columns.Add(factorCol);

            _analogGrid.Rows.Clear();
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                _analogGrid.Rows.Add($"AI{i}", _analogCals[i].LowV, _analogCals[i].HighV, _analogCals[i].LowCal, _analogCals[i].HighCal, _analogCals[i].Factor);
            }

            // Add tooltip specifically for factor column
            _analogGrid.CellFormatting += (_, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (_analogGrid.Columns[e.ColumnIndex].Name == "factor")
                {
                    _analogGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText =
                        "Scaling is used to transmit values with more precision.\n" +
                        "Displayed value = raw / (1,10,100,1000,10000).\n" +
                        "It should not change how you interpret voltage; it only changes displayed value.";
                }
            };

            // NTC grid columns
            _ntcGrid.Columns.Clear();
            _ntcGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ch", HeaderText = "Sensor", ReadOnly = true, FillWeight = 70 });

            _ntcGrid.Columns.Add(MakeUIntCol("r1", "R1 [ohm]"));
            _ntcGrid.Columns.Add(MakeUIntCol("r2", "R2 [ohm]"));
            _ntcGrid.Columns.Add(MakeUIntCol("r3", "R3 [ohm]"));

            _ntcGrid.Columns.Add(MakeIntCol("t1", "T1 [°C]"));
            _ntcGrid.Columns.Add(MakeIntCol("t2", "T2 [°C]"));
            _ntcGrid.Columns.Add(MakeIntCol("t3", "T3 [°C]"));

            _ntcGrid.Rows.Clear();
            for (int i = 0; i < NUM_NTC; i++)
            {
                _ntcGrid.Rows.Add($"NTC{i}", _ntcCals[i].R1, _ntcCals[i].R2, _ntcCals[i].R3, _ntcCals[i].T1, _ntcCals[i].T2, _ntcCals[i].T3);
            }
        }

        private static DataGridViewTextBoxColumn MakeIntCol(string name, string header) =>
            new()
            {
                Name = name,
                HeaderText = header,
                ValueType = typeof(int)
            };

        private static DataGridViewTextBoxColumn MakeUIntCol(string name, string header) =>
            new()
            {
                Name = name,
                HeaderText = header,
                ValueType = typeof(uint)
            };

        private void RefreshPorts()
        {
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();

            _portCombo.Items.Clear();
            _portCombo.Items.AddRange(ports);

            if (ports.Length > 0)
                _portCombo.SelectedIndex = 0;

            SetStatus(_sp.IsOpen ? $"Connected ({_sp.PortName})" : "Disconnected");
        }

        private void ToggleConnect()
        {
            if (_sp.IsOpen)
            {
                try
                {
                    _pollTimer.Stop();
                    _sp.Close();
                    _connectBtn.Text = "Connect";
                    SetStatus("Disconnected");
                }
                catch (Exception ex)
                {
                    SetStatus("Close failed: " + ex.Message);
                }
                return;
            }

            if (_portCombo.SelectedItem is not string port)
            {
                SetStatus("Select a port first.");
                return;
            }

            try
            {
                _sp.PortName = port;
                _sp.Open();

                _connectBtn.Text = "Disconnect";
                SetStatus($"Connected ({port})");

                if (_pollLive.Checked)
                    _pollTimer.Start();
            }
            catch (Exception ex)
            {
                SetStatus("Open failed: " + ex.Message);
            }
        }

        private void PollTick()
        {
            if (!_sp.IsOpen) return;
            try { _sp.Write(new[] { CMD_GET_DATA }, 0, 1); }
            catch (Exception ex) { SetStatus("TX error: " + ex.Message); }
        }

        private void RequestConfig()
        {
            if (!_sp.IsOpen)
            {
                SetStatus("Not connected.");
                return;
            }

            _cfgGotAv = _cfgGotAvV = _cfgGotNtcR = _cfgGotNtcT = _cfgGotFactors = false;

            try
            {
                _sp.Write(new[] { CMD_GET_CALS }, 0, 1);
                SetStatus("Requested config...");
            }
            catch (Exception ex)
            {
                SetStatus("TX error: " + ex.Message);
            }
        }

        private void WriteConfigToDevice()
        {
            if (!_sp.IsOpen)
            {
                SetStatus("Not connected.");
                return;
            }

            try
            {
                ReadConfigFromUi();
                var payload = BuildWriteCalsPayload();
                _sp.Write(new[] { CMD_WRITE_CALS }, 0, 1);
                _sp.Write(payload, 0, payload.Length);
                SetStatus("Config sent.");
            }
            catch (Exception ex)
            {
                SetStatus("Write config failed: " + ex.Message);
            }
        }

        private void ReadConfigFromUi()
        {
            // Analog
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                var row = _analogGrid.Rows[i];
                _analogCals[i] = new AnalogCal
                {
                    LowV = ParseU16(row.Cells["lowV"].Value, "LowV"),
                    HighV = ParseU16(row.Cells["highV"].Value, "HighV"),
                    LowCal = ParseU16(row.Cells["lowCal"].Value, "LowCal"),
                    HighCal = ParseU16(row.Cells["highCal"].Value, "HighCal"),
                    Factor = ParseScaling(row.Cells["factor"].Value)
                };
            }

            // NTC
            for (int i = 0; i < NUM_NTC; i++)
            {
                var row = _ntcGrid.Rows[i];
                _ntcCals[i] = new NtcCal
                {
                    R1 = ParseU32(row.Cells["r1"].Value, "R1"),
                    R2 = ParseU32(row.Cells["r2"].Value, "R2"),
                    R3 = ParseU32(row.Cells["r3"].Value, "R3"),
                    T1 = ParseI16(row.Cells["t1"].Value, "T1"),
                    T2 = ParseI16(row.Cells["t2"].Value, "T2"),
                    T3 = ParseI16(row.Cells["t3"].Value, "T3"),
                };
            }

            // Update factors used for live display
            for (int i = 0; i < NUM_ANALOG; i++)
                _factors[i] = _analogCals[i].Factor;
        }

        private byte[] BuildWriteCalsPayload()
        {
            // Firmware expects a single 131-byte buffer:
            //  0..24   : 0x33 + 6*(lowCal u16, highCal u16)
            // 25..49   : 0x44 + 6*(lowV u16, highV u16)
            // 50..98   : 0x55 + 4*(r1 u32, r2 u32, r3 u32)
            // 99..123  : 0x66 + 4*(t1 i16, t2 i16, t3 i16)
            // 124..130 : 0x77 + 6*(factor u8)
            byte[] b = new byte[25 + 25 + 49 + 25 + 7];

            b[0] = RSP_AV_CALS;
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                int off = 1 + i * 4;
                WriteU16(b, off + 0, _analogCals[i].LowCal);
                WriteU16(b, off + 2, _analogCals[i].HighCal);
            }

            b[25] = RSP_AV_CALS_V;
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                int off = 26 + i * 4;
                WriteU16(b, off + 0, _analogCals[i].LowV);
                WriteU16(b, off + 2, _analogCals[i].HighV);
            }

            b[50] = RSP_NTC_R;
            for (int i = 0; i < NUM_NTC; i++)
            {
                int off = 51 + i * 12;
                WriteU32(b, off + 0, _ntcCals[i].R1);
                WriteU32(b, off + 4, _ntcCals[i].R2);
                WriteU32(b, off + 8, _ntcCals[i].R3);
            }

            b[99] = RSP_NTC_T;
            for (int i = 0; i < NUM_NTC; i++)
            {
                int off = 100 + i * 6;
                WriteI16(b, off + 0, _ntcCals[i].T1);
                WriteI16(b, off + 2, _ntcCals[i].T2);
                WriteI16(b, off + 4, _ntcCals[i].T3);
            }

            b[124] = RSP_FACTORS;
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                b[125 + i] = (byte)_analogCals[i].Factor;
            }

            return b;
        }

        private void OnSerialData()
        {
            try
            {
                int n = _sp.BytesToRead;
                if (n <= 0) return;

                byte[] buf = new byte[n];
                int got = _sp.Read(buf, 0, n);
                if (got <= 0) return;

                lock (_rxLock)
                {
                    _rx.AddRange(buf.AsSpan(0, got).ToArray());
                }

                ParseRx();
            }
            catch
            {
                // Ignore transient read exceptions.
            }
        }

        private void ParseRx()
        {
            List<byte> packet = new();

            while (true)
            {
                byte id;
                int need;

                lock (_rxLock)
                {
                    if (_rx.Count == 0) return;

                    id = _rx[0];
                    need = PacketLen(id);
                    if (need == 0)
                    {
                        // resync: unknown byte
                        _rx.RemoveAt(0);
                        continue;
                    }

                    if (_rx.Count < need) return;

                    packet.Clear();
                    packet.AddRange(_rx.GetRange(0, need));
                    _rx.RemoveRange(0, need);
                }

                HandlePacket(packet.ToArray());
            }
        }

        private static int PacketLen(byte id) => id switch
        {
            RSP_DATA => 21,
            RSP_VOLTS => 21,
            RSP_AV_CALS => 25,
            RSP_AV_CALS_V => 25,
            RSP_NTC_R => 49,
            RSP_NTC_T => 25,
            RSP_FACTORS => 7,
            _ => 0
        };

        private void HandlePacket(byte[] p)
        {
            switch (p[0])
            {
                case RSP_DATA:
                    ParseValues(p);
                    break;
                case RSP_VOLTS:
                    ParseVolts(p);
                    break;
                case RSP_AV_CALS:
                    ParseAvCals(p);
                    break;
                case RSP_AV_CALS_V:
                    ParseAvCalsV(p);
                    break;
                case RSP_NTC_R:
                    ParseNtcR(p);
                    break;
                case RSP_NTC_T:
                    ParseNtcT(p);
                    break;
                case RSP_FACTORS:
                    ParseFactors(p);
                    break;
            }
        }

        private void ParseValues(byte[] p)
        {
            // p[1..20] : 10x u16 little-endian
            for (int ch = 0; ch < NUM_CHANNELS; ch++)
                _rawValues[ch] = ReadU16(p, 1 + ch * 2);

            _haveVals = true;
            UpdateLiveUi();
        }

        private void ParseVolts(byte[] p)
        {
            // p[1..20] : 10x u16 little-endian (mV)
            for (int ch = 0; ch < NUM_CHANNELS; ch++)
                _mV[ch] = ReadU16(p, 1 + ch * 2);

            _haveVolts = true;
            UpdateLiveUi();
        }

        private void ParseAvCals(byte[] p)
        {
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                int off = 1 + i * 4;
                _analogCals[i].LowCal = ReadU16(p, off + 0);
                _analogCals[i].HighCal = ReadU16(p, off + 2);
            }
            _cfgGotAv = true;
            MaybeApplyConfigToUi();
        }

        private void ParseAvCalsV(byte[] p)
        {
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                int off = 1 + i * 4;
                _analogCals[i].LowV = ReadU16(p, off + 0);
                _analogCals[i].HighV = ReadU16(p, off + 2);
            }
            _cfgGotAvV = true;
            MaybeApplyConfigToUi();
        }

        private void ParseNtcR(byte[] p)
        {
            for (int i = 0; i < NUM_NTC; i++)
            {
                int off = 1 + i * 12;
                _ntcCals[i].R1 = ReadU32(p, off + 0);
                _ntcCals[i].R2 = ReadU32(p, off + 4);
                _ntcCals[i].R3 = ReadU32(p, off + 8);
            }
            _cfgGotNtcR = true;
            MaybeApplyConfigToUi();
        }

        private void ParseNtcT(byte[] p)
        {
            for (int i = 0; i < NUM_NTC; i++)
            {
                int off = 1 + i * 6;
                _ntcCals[i].T1 = ReadI16(p, off + 0);
                _ntcCals[i].T2 = ReadI16(p, off + 2);
                _ntcCals[i].T3 = ReadI16(p, off + 4);
            }
            _cfgGotNtcT = true;
            MaybeApplyConfigToUi();
        }

        private void ParseFactors(byte[] p)
        {
            for (int i = 0; i < NUM_ANALOG; i++)
            {
                // Clamp: firmware could send out-of-range bytes (or noise) and the ComboBox would crash.
                byte fb = p[1 + i];
                if (fb > (byte)Scaling.X10000) fb = (byte)Scaling.X1;
                var f = (Scaling)fb;
                _analogCals[i].Factor = f;
                _factors[i] = f;
            }
            _cfgGotFactors = true;
            MaybeApplyConfigToUi();
        }

        private void MaybeApplyConfigToUi()
        {
            // Factors packet may be absent depending on firmware; treat as optional.
            if (!_cfgGotAv || !_cfgGotAvV || !_cfgGotNtcR || !_cfgGotNtcT) return;

            BeginInvoke(new Action(() =>
            {
                // Analog rows
                for (int i = 0; i < NUM_ANALOG; i++)
                {
                    var row = _analogGrid.Rows[i];
                    row.Cells["lowV"].Value = _analogCals[i].LowV;
                    row.Cells["highV"].Value = _analogCals[i].HighV;
                    row.Cells["lowCal"].Value = _analogCals[i].LowCal;
                    row.Cells["highCal"].Value = _analogCals[i].HighCal;
                    row.Cells["factor"].Value = _analogCals[i].Factor;
                }

                // NTC rows
                for (int i = 0; i < NUM_NTC; i++)
                {
                    var row = _ntcGrid.Rows[i];
                    row.Cells["r1"].Value = _ntcCals[i].R1;
                    row.Cells["r2"].Value = _ntcCals[i].R2;
                    row.Cells["r3"].Value = _ntcCals[i].R3;
                    row.Cells["t1"].Value = _ntcCals[i].T1;
                    row.Cells["t2"].Value = _ntcCals[i].T2;
                    row.Cells["t3"].Value = _ntcCals[i].T3;
                }

                SetStatus(_cfgGotFactors ? "Config loaded (incl. factors)." : "Config loaded (factors not received).");
            }));
        }

        private void UpdateLiveUi()
        {
            if (!_haveVals || !_haveVolts) return;

            BeginInvoke(new Action(() =>
            {
                for (int ch = 0; ch < NUM_CHANNELS; ch++)
                {
                    double v = _mV[ch] / 1000.0;

                    string factorText;
                    double divisor = 1.0;

                    if (ch < NUM_ANALOG)
                    {
                        var f = _factors[ch];
                        factorText = f.ToString();
                        divisor = ScalingDivisor(f);
                    }
                    else
                    {
                        factorText = "—";
                    }

                    double disp;
                    if (ch < NUM_ANALOG)
                    {
                        disp = _rawValues[ch] / divisor;
                    }
                    else
                    {
                        disp = (int)_rawValues[ch] - 100;
                    }

                    var row = _liveGrid.Rows[ch];
                    row.Cells["v"].Value = v.ToString("0.000");
                    row.Cells["raw"].Value = _rawValues[ch].ToString();
                    row.Cells["factor"].Value = factorText;
                    row.Cells["disp"].Value = disp.ToString("0.###");
                }
            }));
        }

        private static Scaling ParseScaling(object? v)
        {
            if (v is null) return Scaling.X1;
            if (v is Scaling s) return s;

            if (v is byte b)
                return (Scaling)Math.Clamp((int)b, 0, (int)Scaling.X10000);

            if (v is int i)
                return (Scaling)Math.Clamp(i, 0, (int)Scaling.X10000);

            var str = v.ToString()?.Trim() ?? string.Empty;
            if (str.Length == 0) return Scaling.X1;

            // If user typed a numeric enum value.
            if (int.TryParse(str, out var n))
                return (Scaling)Math.Clamp(n, 0, (int)Scaling.X10000);

            // If user typed/selected a display string like "X100 (/100)".
            if (str.Contains("10000", StringComparison.OrdinalIgnoreCase)) return Scaling.X10000;
            if (str.Contains("1000", StringComparison.OrdinalIgnoreCase)) return Scaling.X1000;
            if (str.Contains("100", StringComparison.OrdinalIgnoreCase)) return Scaling.X100;
            if (str.Contains("10", StringComparison.OrdinalIgnoreCase)) return Scaling.X10;

            // Fallback: exact enum name.
            if (Enum.TryParse<Scaling>(str, ignoreCase: true, out var parsed)) return parsed;
            return Scaling.X1;
        }

        private static double ScalingDivisor(Scaling s) => s switch
        {
            Scaling.X1 => 10000.0,
            Scaling.X10 => 1000.0,
            Scaling.X100 => 100.0,
            Scaling.X1000 => 10.0,
            Scaling.X10000 => 1.0,
            _ => 1.0
        };

        private void SetStatus(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => _status.Text = text));
                return;
            }
            _status.Text = text;
        }

        // ---- Encoding helpers ----
        private static ushort ReadU16(byte[] b, int off) =>
            (ushort)(b[off] | (b[off + 1] << 8));

        private static short ReadI16(byte[] b, int off) =>
            unchecked((short)ReadU16(b, off));

        private static uint ReadU32(byte[] b, int off) =>
            (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

        private static void WriteU16(byte[] b, int off, ushort v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)(v >> 8);
        }

        private static void WriteI16(byte[] b, int off, short v) =>
            WriteU16(b, off, unchecked((ushort)v));

        private static void WriteU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        // ---- UI parsing helpers ----
        private static ushort ParseU16(object? v, string name)
        {
            if (v is null) throw new InvalidOperationException($"{name} is empty");
            if (!ushort.TryParse(v.ToString(), out var x)) throw new InvalidOperationException($"{name} must be 0..65535");
            return x;
        }

        private static uint ParseU32(object? v, string name)
        {
            if (v is null) throw new InvalidOperationException($"{name} is empty");
            if (!uint.TryParse(v.ToString(), out var x)) throw new InvalidOperationException($"{name} must be 0..4294967295");
            return x;
        }

        private static short ParseI16(object? v, string name)
        {
            if (v is null) throw new InvalidOperationException($"{name} is empty");
            if (!short.TryParse(v.ToString(), out var x)) throw new InvalidOperationException($"{name} must be -32768..32767");
            return x;
        }

        // ---- Data types ----
        private struct AnalogCal
        {
            public ushort LowV;
            public ushort HighV;
            public ushort LowCal;
            public ushort HighCal;
            public Scaling Factor;
        }

        private struct NtcCal
        {
            public uint R1, R2, R3;
            public short T1, T2, T3;
        }

        public enum Scaling : byte
        {
            X1 = 0,
            X10 = 1,
            X100 = 2,
            X1000 = 3,
            X10000 = 4
        }
    }
}
