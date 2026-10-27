using System;
using System.Collections.Generic;

namespace OpticalModuleDemo
{
    /// <summary>
    /// 传输通道抽象。上位机代码只认这个接口，不认下面是串口、网口还是内置模拟。
    /// 这正是 VISA 在仪器控制里干的事：换物理接口，上层逻辑一行不改。
    /// </summary>
    public interface ITransport
    {
        bool IsOpen { get; }
        void Open();
        void Close();
        void Send(byte[] data);
        event Action<byte[], int> DataReceived;
        string Name { get; }
    }

    /// <summary>
    /// 模拟一块可插拔光模块的管理接口（EEPROM 内存映射 + 写保护 + 实时诊断量）。
    ///
    /// 【重要】本类里的字段偏移采用业内常见布局编写，Demo 用来理解结构和流程；
    /// 接真实模块前，请一定对照 SFF-8472 / SFF-8636 原文的 Memory Map 核对偏移与单位，
    /// 各模块厂商还常有私有页（Vendor Specific）和密码实现。
    /// </summary>
    public class SimulatedModule
    {
        public const int PAGE_A0 = 0xA0;   // Serial ID 区：厂商信息、料号、序列号（正常只读，写码要解锁）
        public const int PAGE_A2 = 0xA2;   // Digital Diagnostic 区：告警阈值 + 实时读数 + 校准系数

        public readonly byte[] A0 = new byte[256];
        public readonly byte[] A2 = new byte[256];
        private readonly Dictionary<int, byte[]> upperPages = new Dictionary<int, byte[]>();

        // 业内常见布局（务必按规范核对）：
        public const int OFF_IDENTIFIER = 0;    // 1 字节，SFP=0x03
        public const int OFF_VENDOR_NAME = 20;   // 16 字节
        public const int OFF_VENDOR_OUI = 36;    // 3 字节
        public const int OFF_VENDOR_PN = 40;     // 16 字节
        public const int OFF_VENDOR_REV = 56;    // 4 字节
        public const int OFF_VENDOR_SN = 60;     // 10 字节
        public const int OFF_DATE_CODE = 70;     // 8 字节
        public const int SERIAL_ID_RO_END = 95;  // 0..95 属于写保护的出厂信息区

        // A2 实时诊断量（本 Demo 采用的标度：温度 1/256℃ 带符号，电压 100uV，电流 2uA，光功率 0.1uW）
        public const int OFF_REAL_VALUE = 96;

        private byte[] password = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        private DateTime unlockUntil = DateTime.MinValue;

        private readonly Random rnd = new Random();
        private double tempC = 35.0;
        private double vccV = 3.30;
        private double txDbm = -2.0;
        private double rxDbm = -3.5;
        private double biasMa = 7.0;

        public bool WriteProtectEnabled = true;
        public int DeniedWriteCount;

        public SimulatedModule()
        {
            Seed(A0, "DEMO-FIBER", "SFP-25G-SR", "A1", "SN00000001", "20261027");
            // 阈值：温度高 75℃、低 -5℃；电压高 3.6V、低 2.9V
            SetThreshold(0, 75.0);    // A2[0..1]  温度高告警
            SetThreshold(2, -5.0);    // A2[2..3]  温度低告警
            SetThreshold(4, 3.60);    // A2[4..5]  电压高告警
            SetThreshold(6, 2.90);    // A2[6..7]  电压低告警
            SetThreshold(8, 12.0);    // A2[8..9]  偏置电流高告警(mA) —— 演示用，简化排布
        }

        private void Seed(byte[] reg, string vendor, string pn, string rev, string sn, string date)
        {
            reg[OFF_IDENTIFIER] = 0x03;
            PutAscii(reg, OFF_VENDOR_NAME, vendor, 16);
            reg[OFF_VENDOR_OUI] = 0x00; reg[OFF_VENDOR_OUI + 1] = 0x11; reg[OFF_VENDOR_OUI + 2] = 0x22;
            PutAscii(reg, OFF_VENDOR_PN, pn, 16);
            PutAscii(reg, OFF_VENDOR_REV, rev, 4);
            PutAscii(reg, OFF_VENDOR_SN, sn, 10);
            PutAscii(reg, OFF_DATE_CODE, date, 8);
        }

        public static void PutAscii(byte[] reg, int offset, string text, int size)
        {
            for (int i = 0; i < size; i++)
            {
                reg[offset + i] = (i < text.Length) ? (byte)text[i] : (byte)0x20;
            }
        }

        public static string GetAscii(byte[] reg, int offset, int size)
        {
            char[] c = new char[size];
            for (int i = 0; i < size; i++)
            {
                byte b = reg[offset + i];
                c[i] = (b == 0x00 || b == 0xFF) ? ' ' : (char)b;
            }
            return new string(c).Trim();
        }

        private void SetThreshold(int offset, double value)
        {
            // 本 Demo 阈值统一按 1/256 的定点数存放，演示"阈值也是编码值"这件事
            int raw = (int)Math.Round(value * 256.0);
            A2[offset] = (byte)((raw >> 8) & 0xFF);
            A2[offset + 1] = (byte)(raw & 0xFF);
        }

        public static double ReadThreshold(byte[] reg, int offset)
        {
            short raw = (short)((reg[offset] << 8) | reg[offset + 1]);
            return raw / 256.0;
        }

        public bool IsUnlocked
        {
            get { return DateTime.Now < unlockUntil; }
        }

        public bool TryUnlock(byte[] pwd)
        {
            if (pwd == null || pwd.Length != 8) return false;
            for (int i = 0; i < 8; i++) if (pwd[i] != password[i]) return false;
            unlockUntil = DateTime.Now.AddSeconds(60);   // 解锁有时效，产线上防止忘记上锁
            return true;
        }

        /// <summary>每次被读取时让读数漂移一下，模拟模块在工作、DDM 是实时量。</summary>
        private void RefreshRealValues()
        {
            tempC += (rnd.NextDouble() - 0.5) * 0.6;
            vccV += (rnd.NextDouble() - 0.5) * 0.01;
            txDbm += (rnd.NextDouble() - 0.5) * 0.2;
            rxDbm += (rnd.NextDouble() - 0.5) * 0.2;
            biasMa += (rnd.NextDouble() - 0.5) * 0.15;
            if (tempC > 80.0) tempC = 20.0;      // 故意让它偶尔超阈值，演示告警变红

            WriteSigned(A2, OFF_REAL_VALUE + 0, tempC * 256.0);            // 温度，1/256 ℃
            WriteUnsigned(A2, OFF_REAL_VALUE + 2, vccV * 10000.0);          // 电压，100 uV
            WriteUnsigned(A2, OFF_REAL_VALUE + 4, ToUw01(rxDbm));           // 接收光功率，0.1 uW
            WriteUnsigned(A2, OFF_REAL_VALUE + 6, biasMa * 500.0);          // 偏置电流，2 uA
            WriteUnsigned(A2, OFF_REAL_VALUE + 8, ToUw01(txDbm));           // 发射光功率，0.1 uW
        }

        private static double ToUw01(double dbm)
        {
            // dBm -> mW -> uW -> 0.1uW 单位
            double mw = Math.Pow(10.0, dbm / 10.0);
            return mw * 10000.0;
        }

        public static double FromUw01ToDbm(double raw)
        {
            double mw = raw / 10000.0;
            if (mw <= 0) return -40.0;
            return 10.0 * Math.Log10(mw);
        }

        private static void WriteSigned(byte[] r, int off, double v)
        {
            short s = (short)Math.Round(v);
            r[off] = (byte)((s >> 8) & 0xFF);
            r[off + 1] = (byte)(s & 0xFF);
        }

        private static void WriteUnsigned(byte[] r, int off, double v)
        {
            int i2 = (int)Math.Round(v);
            if (i2 < 0) i2 = 0;
            if (i2 > 65535) i2 = 65535;
            r[off] = (byte)((i2 >> 8) & 0xFF);
            r[off + 1] = (byte)(i2 & 0xFF);
        }

        private byte[] Region(int page)
        {
            if (page == PAGE_A0) return A0;
            if (page == PAGE_A2) return A2;
            byte[] up;
            if (!upperPages.TryGetValue(page, out up))
            {
                up = new byte[256];
                upperPages[page] = up;
            }
            return up;
        }

        // ---- 下位机被访问的入口：返回状态码 ----

        public byte Read(int page, int offset, int len, out byte[] data)
        {
            if (len <= 0 || len > Proto.MAX_DATA) { data = new byte[0]; return Proto.ST_BADPARAM; }
            if (offset + len > 256) { data = new byte[0]; return Proto.ST_BADPARAM; }
            if (page == PAGE_A2 && offset <= OFF_REAL_VALUE && offset + len >= OFF_REAL_VALUE + 10)
            {
                RefreshRealValues();
            }
            byte[] reg = Region(page);
            data = new byte[len];
            Array.Copy(reg, offset, data, 0, len);
            return Proto.ST_OK;
        }

        public byte Write(int page, int offset, byte[] data)
        {
            if (data == null || data.Length == 0) return Proto.ST_BADPARAM;
            if (offset + data.Length > 256) return Proto.ST_BADPARAM;

            // 写保护：A0h 的出厂信息区（0..95）必须先解锁，这正是"写码"这件事的真实难点之一
            if (WriteProtectEnabled && page == PAGE_A0 && offset <= SERIAL_ID_RO_END)
            {
                if (!IsUnlocked)
                {
                    DeniedWriteCount++;
                    return Proto.ST_WPROT;   // 真实模块还会更狠：直接屏蔽写入但不报错（华为文档就提到这种情况）
                }
            }
            byte[] reg = Region(page);
            Array.Copy(data, 0, reg, offset, data.Length);
            return Proto.ST_OK;
        }
    }

    /// <summary>
    /// 内置模拟通道：把"测试板 + MCU + I2C + 光模块"压缩成一个进程内的对象。
    /// 同时提供三个故障注入开关，用来证明上位机侧的解析与超时重发是真的 robust。
    /// </summary>
    public class SimTransport : ITransport
    {
        public event Action<byte[], int> DataReceived;
        public SimulatedModule Module = new SimulatedModule();

        // 故障注入（UI 上可勾选）
        public bool InjectSplit = false;    // 应答拆成两次到达 -> 模拟半包
        public bool InjectNoise = false;    // 随机丢一帧应答 -> 模拟干扰/校验失败，逼出超时重发
        public bool InjectPrefix = false;   // 应答前塞几个垃圾字节 -> 模拟脏数据，逼出重同步

        public string Name { get { return "内置模拟板卡"; } }
        public bool IsOpen { get; private set; }

        public void Open() { IsOpen = true; }
        public void Close() { IsOpen = false; }

        public void Send(byte[] data)
        {
            if (!IsOpen) return;
            // 真实串口必然异步回来，这里也用线程池，避免把 UI 线程拖成同步调用
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                System.Threading.Thread.Sleep(15);   // 模拟总线往返耗时
                HandleRequest(data);
            });
        }

        private readonly Random rnd = new Random();

        private void HandleRequest(byte[] raw)
        {
            // 板卡 MCU 侧同样要解析帧，逻辑和上位机一致，这里借用同一个解析器
            FrameParser p = new FrameParser();
            List<byte[]> reqs = p.Feed(raw, raw.Length);
            foreach (byte[] r in reqs)
            {
                Frame req = Proto.ParseWhole(r);
                byte[] resp = Reply(req);
                if (resp == null) continue;

                if (InjectNoise && rnd.Next(0, 4) == 0) continue;  // 1/4 概率整帧不回，制造超时

                if (InjectPrefix)
                {
                    byte[] dirty = new byte[resp.Length + 3];
                    dirty[0] = 0x00; dirty[1] = 0xFF; dirty[2] = 0x3A;
                    Array.Copy(resp, 0, dirty, 3, resp.Length);
                    resp = dirty;
                }

                if (InjectSplit && resp.Length > 8)
                {
                    int cut = 3 + rnd.Next(1, resp.Length - 4);
                    byte[] a = new byte[cut];
                    byte[] b = new byte[resp.Length - cut];
                    Array.Copy(resp, 0, a, 0, cut);
                    Array.Copy(resp, cut, b, 0, b.Length);
                    Raise(a, a.Length);
                    System.Threading.Thread.Sleep(8);
                    Raise(b, b.Length);
                }
                else
                {
                    Raise(resp, resp.Length);
                }
            }
        }

        private void Raise(byte[] data, int count)
        {
            Action<byte[], int> h = DataReceived;
            if (h != null) h(data, count);
        }

        private byte[] Reply(Frame req)
        {
            byte cmd = req.Cmd;

            if (cmd == Proto.CMD_PING)
            {
                byte[] id = System.Text.Encoding.ASCII.GetBytes("SIM-BOARD-1.0");
                return Proto.Build(cmd, Proto.ST_OK, 0, 0, id);
            }

            if (cmd == Proto.CMD_UNLOCK)
            {
                bool ok = Module.TryUnlock(req.Data);
                return Proto.Build(cmd, ok ? Proto.ST_OK : Proto.ST_BADPARAM, 0, 0, null);
            }

            // I2C 从机地址不对 -> 没有 ACK
            byte dev = req.DevOrStatus;
            if (dev != 0x50 && dev != 0x51)
            {
                return Proto.Build(cmd, Proto.ST_NOACK, req.Page, req.Offset, null);
            }

            if (cmd == Proto.CMD_BULK_READ || cmd == Proto.CMD_READ)
            {
                // CMD_READ: 读 1 字节；CMD_BULK_READ: 请求的 DATA 里放 1 个字节，值是"想读多少个字节"
                int len = (cmd == Proto.CMD_READ) ? 1 : (req.Data.Length >= 1 ? req.Data[0] : 0);
                byte[] data;
                byte st = Module.Read(req.Page, req.Offset, len, out data);
                return Proto.Build(cmd, st, req.Page, req.Offset, data);
            }

            if (cmd == Proto.CMD_WRITE)
            {
                byte st = Module.Write(req.Page, req.Offset, req.Data);
                return Proto.Build(cmd, st, req.Page, req.Offset, null);
            }

            return Proto.Build(cmd, Proto.ST_BADPARAM, req.Page, req.Offset, null);
        }
    }
}
