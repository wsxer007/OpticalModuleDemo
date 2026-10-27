using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpticalModuleDemo
{
    /// <summary>
    /// 光模块读写码上位机 —— Demo 主窗体。
    /// 三块功能对应岗位 JD 的三件事：读码/写码（A0h）、调测读数（A2h DDM）、通信日志与异常处理。
    /// 注意所有耗时操作都是 await 在后台完成的，界面刷新走 BeginInvoke，所以轮询时窗口不会卡死。
    /// </summary>
    public class MainForm : Form
    {
        private SimTransport sim;
        private BoardClient client;
        private CancellationTokenSource pollCts;
        private readonly List<string[]> ddmHistory = new List<string[]>();

        private ComboBox cboCh;
        private Button btnConnect;
        private Label lblStatus;
        private CheckBox chkSplit, chkNoise, chkPrefix, chkWProt;

        private TextBox txtIdf, txtVendor, txtOui, txtPn, txtRev, txtSn, txtDate;
        private Button btnReadCode, btnUnlock, btnWrite;

        private Button btnPoll, btnStop;
        private Label lblTemp, lblVcc, lblTx, lblBias, lblRx, lblTh;

        private TextBox txtLog;
        private Button btnExport, btnClear;
        private Label lblStat;

        public MainForm()
        {
            BuildUi();
        }

        // ------------------------------------------------------------------ UI

        private void BuildUi()
        {
            Text = "光模块读写码上位机 Demo（模拟板卡）";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(1120, 760);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            Label lblCh = MakeLabel("通道:", 14, 17);
            cboCh = new ComboBox();
            cboCh.DropDownStyle = ComboBoxStyle.DropDownList;
            cboCh.Items.Add("内置模拟板卡（无需硬件）");
            cboCh.SelectedIndex = 0;
            cboCh.Location = new Point(62, 13);
            cboCh.Width = 170;

            btnConnect = MakeButton("连接板卡", 244, 12, btnConnect_Click);
            lblStatus = MakeLabel("未连接", 352, 17);
            lblStatus.ForeColor = Color.Firebrick;
            lblStatus.Width = 150;

            chkSplit = MakeCheck("注入半包", 520, 15, chkInject_Changed);
            chkNoise = MakeCheck("注入丢帧", 628, 15, chkInject_Changed);
            chkPrefix = MakeCheck("注入脏字节", 736, 15, chkInject_Changed);
            chkWProt = MakeCheck("启用写保护", 862, 15, chkInject_Changed);
            chkWProt.Checked = true;

            // ① 读码 / 写码
            GroupBox gbCode = new GroupBox();
            gbCode.Text = "① 读码 / 写码（A0h 身份信息区：厂商名、料号、序列号、生产日期）";
            gbCode.Location = new Point(12, 48);
            gbCode.Size = new Size(540, 300);

            AddField(gbCode, "Identifier", 26, txtIdf = new TextBox(), 112, 130, "只读，模块类型码");
            txtIdf.ReadOnly = true;
            AddField(gbCode, "厂商名(16)", 60, txtVendor = new TextBox(), 112, 150, "偏移 20，长度 16");
            AddField(gbCode, "OUI(3字节)", 94, txtOui = new TextBox(), 112, 150, "偏移 36，写法如 00-11-22");
            AddField(gbCode, "料号PN(16)", 128, txtPn = new TextBox(), 112, 150, "偏移 40，长度 16");
            AddField(gbCode, "版本Rev(4)", 162, txtRev = new TextBox(), 112, 150, "偏移 56，长度 4");
            AddField(gbCode, "序列号SN(10)", 196, txtSn = new TextBox(), 112, 150, "偏移 60，长度 10");
            AddField(gbCode, "生产日期(8)", 230, txtDate = new TextBox(), 112, 150, "偏移 70，长度 8");

            btnReadCode = MakeButton(gbCode, "读码（回读显示）", 14, 262, 130, btnReadCode_Click);
            btnUnlock = MakeButton(gbCode, "① 先解锁", 156, 262, 110, btnUnlock_Click);
            btnWrite = MakeButton(gbCode, "② 写码并校验", 278, 262, 130, btnWrite_Click);

            // ② DDM
            GroupBox gbDdm = new GroupBox();
            gbDdm.Text = "② 数字诊断 DDM（A2h 实时量，按协议标度换算后判定阈值）";
            gbDdm.Location = new Point(564, 48);
            gbDdm.Size = new Size(544, 210);

            btnPoll = MakeButton(gbDdm, "启动轮询", 14, 30, 100, btnPoll_Click);
            btnStop = MakeButton(gbDdm, "停止", 126, 30, 70, btnStop_Click);

            lblTemp = MakeLabelIn(gbDdm, "温度: --", 16, 70, 250);
            lblVcc = MakeLabelIn(gbDdm, "供电电压: --", 16, 96, 250);
            lblTx = MakeLabelIn(gbDdm, "发射光功率: --", 16, 122, 250);
            lblBias = MakeLabelIn(gbDdm, "偏置电流: --", 16, 148, 250);
            lblRx = MakeLabelIn(gbDdm, "接收光功率: --", 16, 174, 250);
            lblTh = MakeLabelIn(gbDdm, "阈值: 未读取", 272, 70, 260);

            // ③ 日志
            GroupBox gbLog = new GroupBox();
            gbLog.Text = "③ 通信日志（帧的原始字节 + 解析结果 + 超时/校验/写保护过程）";
            gbLog.Location = new Point(12, 360);
            gbLog.Size = new Size(1096, 386);
            gbLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Both;
            txtLog.WordWrap = false;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.BackColor = Color.White;
            txtLog.Location = new Point(14, 26);
            txtLog.Size = new Size(1068, 306);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            gbLog.Controls.Add(txtLog);

            btnExport = MakeButton(gbLog, "导出测试数据 CSV", 14, 344, 150, btnExport_Click);
            btnExport.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            btnClear = MakeButton(gbLog, "清空日志", 176, 344, 90, btnClear_Click);
            btnClear.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            lblStat = MakeLabelIn(gbLog, "累计请求 0 次", 270, 349, 800);
            lblStat.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            Controls.Add(lblCh);
            Controls.Add(cboCh);
            Controls.Add(btnConnect);
            Controls.Add(lblStatus);
            Controls.Add(chkSplit);
            Controls.Add(chkNoise);
            Controls.Add(chkPrefix);
            Controls.Add(chkWProt);
            Controls.Add(gbCode);
            Controls.Add(gbDdm);
            Controls.Add(gbLog);

            btnConnect.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = true;
            return l;
        }

        private static Label MakeLabelIn(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 22);
            parent.Controls.Add(l);
            return l;
        }

        private static CheckBox MakeCheck(string text, int x, int y, EventHandler onChange)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Location = new Point(x, y);
            c.AutoSize = true;
            c.CheckedChanged += onChange;
            return c;
        }

        private static Button MakeButton(Control parent, string text, int x, int y, int w, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 30);
            b.Click += onClick;
            parent.Controls.Add(b);
            return b;
        }

        private static Button MakeButton(string text, int x, int y, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(96, 30);
            b.Click += onClick;
            return b;
        }

        private static void AddField(Control parent, string caption, int y, TextBox box, int x, int w, string hint)
        {
            Label l = new Label();
            l.Text = caption;
            l.Location = new Point(12, y + 4);
            l.AutoSize = true;
            box.Location = new Point(x, y);
            box.Size = new Size(w, 24);
            parent.Controls.Add(l);
            parent.Controls.Add(box);
            if (hint != null && hint.Length > 0)
            {
                Label h = new Label();
                h.Text = hint;
                h.ForeColor = Color.Gray;
                h.Location = new Point(x + w + 10, y + 4);
                h.AutoSize = true;
                parent.Controls.Add(h);
            }
        }

        // ---------------------------------------------------------------- 通用

        private void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        private void Log(string s)
        {
            Ui(delegate
            {
                txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
            });
        }

        private void RefreshStat()
        {
            Ui(delegate
            {
                if (client == null) { lblStat.Text = "累计请求 0 次"; return; }
                lblStat.Text = string.Format(
                    "累计请求 {0} 次 ｜ 超时重发 {1} 次 ｜ CRC 校验失败丢弃 {2} 帧 ｜ 重同步丢弃 {3} 字节 ｜ 模块拒绝写入 {4} 次 ｜ DDM 采集 {5} 条",
                    client.RequestCount, client.TimeoutCount, client.CrcErrorCount,
                    client.ResyncByteCount, sim != null ? sim.Module.DeniedWriteCount : 0, ddmHistory.Count);
            });
        }

        private void SetBusy(bool b)
        {
            btnConnect.Enabled = !b;
            btnReadCode.Enabled = !b;
            btnUnlock.Enabled = !b;
            btnWrite.Enabled = !b;
            btnExport.Enabled = !b;
            if (!b) RefreshStat();
        }

        private void chkInject_Changed(object sender, EventArgs e)
        {
            if (sim == null) return;
            sim.InjectSplit = chkSplit.Checked;
            sim.InjectNoise = chkNoise.Checked;
            sim.InjectPrefix = chkPrefix.Checked;
            sim.Module.WriteProtectEnabled = chkWProt.Checked;
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            ConnectAsync();
        }

        private async void ConnectAsync()
        {
            SetBusy(true);
            try
            {
                if (client == null)
                {
                    sim = new SimTransport();
                    sim.Module.WriteProtectEnabled = chkWProt.Checked;
                    sim.InjectSplit = chkSplit.Checked;
                    sim.InjectNoise = chkNoise.Checked;
                    sim.InjectPrefix = chkPrefix.Checked;
                    client = new BoardClient(sim);
                    client.OnLog += Log;
                    sim.Open();
                }
                string id = await client.PingAsync();
                Log("握手成功，板卡标识: " + id + "（相当于仪器控制里的 *IDN?）");
                Ui(delegate
                {
                    lblStatus.Text = "已连接：" + sim.Name;
                    lblStatus.ForeColor = Color.ForestGreen;
                });
            }
            catch (Exception ex)
            {
                Log("连接失败: " + ex.Message);
                Ui(delegate { lblStatus.Text = "未连接"; lblStatus.ForeColor = Color.Firebrick; });
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ------------------------------------------------------------ 读码/写码

        private async void btnReadCode_Click(object sender, EventArgs e)
        {
            if (!Ready()) return;
            SetBusy(true);
            try
            {
                Log("=== 读码：连续读 A0h 偏移 0..127，单帧上限 32 字节，所以自动拆成 4 帧 ===");
                byte[] a0 = await client.ReadBlockAsync((byte)SimulatedModule.PAGE_A0, 0, 128);
                txtIdf.Text = "0x" + a0[SimulatedModule.OFF_IDENTIFIER].ToString("X2");
                txtVendor.Text = SimulatedModule.GetAscii(a0, SimulatedModule.OFF_VENDOR_NAME, 16);
                txtOui.Text = HexOf(a0, SimulatedModule.OFF_VENDOR_OUI, 3);
                txtPn.Text = SimulatedModule.GetAscii(a0, SimulatedModule.OFF_VENDOR_PN, 16);
                txtRev.Text = SimulatedModule.GetAscii(a0, SimulatedModule.OFF_VENDOR_REV, 4);
                txtSn.Text = SimulatedModule.GetAscii(a0, SimulatedModule.OFF_VENDOR_SN, 10);
                txtDate.Text = SimulatedModule.GetAscii(a0, SimulatedModule.OFF_DATE_CODE, 8);
                Log("读码完成");
            }
            catch (Exception ex)
            {
                Log("读码失败: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void btnUnlock_Click(object sender, EventArgs e)
        {
            if (!Ready()) return;
            SetBusy(true);
            try
            {
                byte[] pwd = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
                bool ok = await client.UnlockAsync(pwd);
                Log(ok ? "解锁成功：写保护在 60 秒内放开（真实产线同样有解锁时效，防止忘记重新上锁）"
                       : "解锁失败：密码不正确");
            }
            catch (Exception ex)
            {
                Log("解锁失败: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void btnWrite_Click(object sender, EventArgs e)
        {
            if (!Ready()) return;
            SetBusy(true);
            try
            {
                Log("=== 写码：把界面上的字段编码成字节下发到 A0h ===");
                byte[] name = Fixed(txtVendor.Text, 16);
                byte[] oui = HexToBytes(txtOui.Text, 3);
                byte[] pn = Fixed(txtPn.Text, 16);
                byte[] rev = Fixed(txtRev.Text, 4);
                byte[] sn = Fixed(txtSn.Text, 10);
                byte[] date = Fixed(txtDate.Text, 8);

                await client.WriteBlockAsync((byte)SimulatedModule.PAGE_A0, SimulatedModule.OFF_VENDOR_NAME, name);
                await client.WriteBlockAsync((byte)SimulatedModule.PAGE_A0, SimulatedModule.OFF_VENDOR_OUI, oui);
                await client.WriteBlockAsync((byte)SimulatedModule.PAGE_A0, SimulatedModule.OFF_VENDOR_PN, pn);
                await client.WriteBlockAsync((byte)SimulatedModule.PAGE_A0, SimulatedModule.OFF_VENDOR_REV, rev);
                await client.WriteBlockAsync((byte)SimulatedModule.PAGE_A0, SimulatedModule.OFF_VENDOR_SN, sn);
                await client.WriteBlockAsync((byte)SimulatedModule.PAGE_A0, SimulatedModule.OFF_DATE_CODE, date);

                Log("写入指令全部下发完成，开始回读逐字节校验");
                byte[] back = await client.ReadBlockAsync((byte)SimulatedModule.PAGE_A0, 0, 128);

                int bad = 0;
                bad += Compare(back, "厂商名", SimulatedModule.OFF_VENDOR_NAME, name);
                bad += Compare(back, "OUI", SimulatedModule.OFF_VENDOR_OUI, oui);
                bad += Compare(back, "料号PN", SimulatedModule.OFF_VENDOR_PN, pn);
                bad += Compare(back, "版本Rev", SimulatedModule.OFF_VENDOR_REV, rev);
                bad += Compare(back, "序列号SN", SimulatedModule.OFF_VENDOR_SN, sn);
                bad += Compare(back, "生产日期", SimulatedModule.OFF_DATE_CODE, date);

                Log(bad == 0 ? "写码校验通过：回读与下发完全一致"
                             : "写码校验失败：" + bad + " 个字段不一致，产线上这一步失败要报警并让工位返工");
                ddmHistory.Add(new string[] { "写码", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), txtSn.Text,
                                               "", "", "", "", "", bad == 0 ? "PASS" : "FAIL", txtVendor.Text, txtPn.Text });
            }
            catch (BoardException be)
            {
                if (be.Status == Proto.ST_WPROT)
                    Log("写码被拒绝：写保护生效（只读区/未解锁）。先点【① 先解锁】再试 —— 这正是产线上写码流程必须先解锁的原因");
                else
                    Log("写码失败：" + be.Message);
            }
            catch (Exception ex)
            {
                Log("写码失败: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private int Compare(byte[] back, string field, int offset, byte[] expect)
        {
            for (int i = 0; i < expect.Length; i++)
            {
                if (back[offset + i] != expect[i])
                {
                    Log("  差异 [" + field + "] 偏移 " + (offset + i) + "：期望 0x" + expect[i].ToString("X2")
                        + " 实际 0x" + back[offset + i].ToString("X2"));
                    return 1;
                }
            }
            Log("  校验 [" + field + "] 一致");
            return 0;
        }

        // ----------------------------------------------------------------- DDM

        private async void btnPoll_Click(object sender, EventArgs e)
        {
            if (!Ready()) return;
            btnPoll.Enabled = false;
            pollCts = new CancellationTokenSource();
            try
            {
                Log("=== DDM 轮询开始（每 500ms 读一次 A2h 实时量）===");
                byte[] th = await client.ReadBlockAsync((byte)SimulatedModule.PAGE_A2, 0, 10);
                double tHi = SimulatedModule.ReadThreshold(th, 0);
                double tLo = SimulatedModule.ReadThreshold(th, 2);
                double vHi = SimulatedModule.ReadThreshold(th, 4);
                double vLo = SimulatedModule.ReadThreshold(th, 6);
                Ui(delegate
                {
                    lblTh.Text = string.Format("A2h 阈值区换算：温度 {0:F1} ~ {1:F1} ℃，电压 {2:F2} ~ {3:F2} V", tHi, tLo, vHi, vLo);
                });

                CancellationToken ct = pollCts.Token;
                while (!ct.IsCancellationRequested)
                {
                    byte[] d = await client.ReadBlockAsync((byte)SimulatedModule.PAGE_A2, SimulatedModule.OFF_REAL_VALUE, 10);

                    short tr = (short)((d[0] << 8) | d[1]);        // 带符号：漏了 & 0xFF 之外的符号处理就会算出 -200℃ 这类离谱值
                    double temp = tr / 256.0;
                    double vcc = ((d[2] << 8) | d[3]) / 10000.0;
                    double rxDbm = SimulatedModule.FromUw01ToDbm((d[4] << 8) | d[5]);
                    double bias = ((d[6] << 8) | d[7]) / 500.0;
                    double txDbm = SimulatedModule.FromUw01ToDbm((d[8] << 8) | d[9]);

                    SetReadout(lblTemp, string.Format("温度: {0:F2} ℃", temp), temp > tHi || temp < tLo);
                    SetReadout(lblVcc, string.Format("供电电压: {0:F3} V", vcc), vcc > vHi || vcc < vLo);
                    SetReadout(lblTx, string.Format("发射光功率: {0:F2} dBm", txDbm), false);
                    SetReadout(lblBias, string.Format("激光器偏置电流: {0:F2} mA", bias), false);
                    SetReadout(lblRx, string.Format("接收光功率: {0:F2} dBm", rxDbm), false);

                    ddmHistory.Add(new string[] { "DDM", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        txtSn.Text, temp.ToString("F2"), vcc.ToString("F3"), txDbm.ToString("F2"), rxDbm.ToString("F2"),
                        bias.ToString("F2"), "PASS" });

                    if (ddmHistory.Count % 20 == 0) RefreshStat();
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("轮询中断: " + ex.Message + "（真实产线这里必须落日志并提示操作员，不能静默停掉）");
            }
            finally
            {
                btnPoll.Enabled = true;
                RefreshStat();
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            if (pollCts != null)
            {
                pollCts.Cancel();
                pollCts = null;
                Log("DDM 轮询已停止");
            }
        }

        private void SetReadout(Label l, string text, bool alarm)
        {
            Ui(delegate
            {
                l.Text = text;
                l.ForeColor = alarm ? Color.Firebrick : Color.Black;
            });
        }

        // -------------------------------------------------------------- 工具方法

        private bool Ready()
        {
            if (client == null)
            {
                Log("请先点【连接板卡】");
                return false;
            }
            return true;
        }

        private static byte[] Fixed(string text, int size)
        {
            byte[] b = new byte[size];
            for (int i = 0; i < size; i++) b[i] = 0x20;
            string s = text == null ? "" : text;
            int n = Math.Min(s.Length, size);
            for (int i = 0; i < n; i++) b[i] = (byte)s[i];
            return b;
        }

        private static string HexOf(byte[] reg, int offset, int len)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < len; i++) sb.Append(reg[offset + i].ToString("X2")).Append('-');
            return sb.ToString().TrimEnd('-');
        }

        private static byte[] HexToBytes(string text, int size)
        {
            string s = (text == null ? "" : text).Replace("-", "").Replace(" ", "");
            byte[] b = new byte[size];
            for (int i = 0; i < size; i++)
            {
                int pos = i * 2;
                if (pos + 1 < s.Length)
                {
                    byte v;
                    if (byte.TryParse(s.Substring(pos, 2), System.Globalization.NumberStyles.HexNumber, null, out v)) b[i] = v;
                }
            }
            return b;
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            if (ddmHistory.Count == 0)
            {
                Log("还没有可导出的数据：先做一次写码校验或启动 DDM 轮询");
                return;
            }
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "CSV 文件|*.csv";
            dlg.FileName = "光模块测试数据_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
            if (dlg.ShowDialog() != DialogResult.OK) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("类型,时间,序列号,温度℃,电压V,Tx_dBm,Rx_dBm,偏置mA,判定,厂商名,料号PN");
            foreach (string[] r in ddmHistory)
            {
                sb.AppendLine(string.Join(",", Pad(r)));
            }
            try
            {
                // UTF8 带 BOM，Excel 打开中文表头不乱码
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                Log("已导出 " + ddmHistory.Count + " 条到 " + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log("导出失败: " + ex.Message + "（如果目标在网络共享目录，检查权限和是否断链）");
            }
        }

        private static string[] Pad(string[] r)
        {
            string[] o = new string[11];
            for (int i = 0; i < 11; i++) o[i] = (i < r.Length) ? r[i] : "";
            return o;
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            txtLog.Clear();
        }
    }
}
