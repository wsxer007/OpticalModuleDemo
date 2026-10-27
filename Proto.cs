using System;
using System.Collections.Generic;

namespace OpticalModuleDemo
{
    /// <summary>
    /// 自定义帧协议：PC(上位机) <-> 测试板(下位机)。
    /// 真实项目里这一段是 PC 通过 USB/串口/网口发给板卡，板卡上的 MCU 再去产生 I2C 时序读光模块。
    /// 所以上位机侧真正要写的代码，就是"怎么把一条请求打包成帧、怎么把回来的字节流还原成帧"。
    ///
    /// 帧格式（请求与应答完全同构，解析器只写一份）：
    ///   [0] 0xAA  [1] 0x55          帧头 SOF
    ///   [2] CMD                     命令码
    ///   [3] DEV                     请求=模块 I2C 地址(0x50/0x51)；应答=状态码
    ///   [4] PAGE                    页（0xA0/0xA2 表示 SFP 的两块区域；其它值表示 Upper Page 号）
    ///   [5] OFFSET                  区域起始偏移
    ///   [6] LEN                     后面 DATA 的字节数
    ///   [7..] DATA[LEN]
    ///   [7+LEN] CRC_H [8+LEN] CRC_L 覆盖 [0 .. 7+LEN-1]，CRC-16/CCITT-FALSE
    /// 整帧长度 = 7 + LEN + 2
    /// </summary>
    public static class Proto
    {
        public const byte SOF1 = 0xAA;
        public const byte SOF2 = 0x55;
        public const int HEADER_LEN = 7;
        public const int MAX_DATA = 32;      // 单帧最多带 32 字节，和真实板卡常见的限制一致，逼着上位机做"分帧连续读"

        // 命令码
        public const byte CMD_READ = 0x01;    // 读 1 字节
        public const byte CMD_WRITE = 0x02;   // 写若干字节
        public const byte CMD_UNLOCK = 0x03;  // 写码前解锁
        public const byte CMD_BULK_READ = 0x04; // 连续读
        public const byte CMD_PING = 0x05;    // 握手，等价于问仪器 *IDN?

        // 状态码（放在应答帧的 DEV 位置）
        public const byte ST_OK = 0x00;
        public const byte ST_CRC = 0x01;       // 校验失败
        public const byte ST_WPROT = 0x02;     // 写保护：没解锁 / 只读区
        public const byte ST_BADPARAM = 0x03;  // 参数非法
        public const byte ST_NOACK = 0x04;     // I2C 上从机不应答

        public static ushort Crc16(byte[] data, int offset, int length)
        {
            // CRC-16/CCITT-FALSE: poly=0x1021, init=0xFFFF
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= (ushort)(data[offset + i] << 8);
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                    else crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }

        public static byte[] Build(byte cmd, byte devOrStatus, byte page, byte offset, byte[] payload)
        {
            if (payload == null) payload = new byte[0];
            if (payload.Length > MAX_DATA) throw new ArgumentException("单帧数据超过 " + MAX_DATA + " 字节");
            byte[] f = new byte[HEADER_LEN + payload.Length + 2];
            f[0] = SOF1;
            f[1] = SOF2;
            f[2] = cmd;
            f[3] = devOrStatus;
            f[4] = page;
            f[5] = offset;
            f[6] = (byte)payload.Length;
            Array.Copy(payload, 0, f, HEADER_LEN, payload.Length);
            ushort crc = Crc16(f, 0, f.Length - 2);
            f[f.Length - 2] = (byte)((crc >> 8) & 0xFF);
            f[f.Length - 1] = (byte)(crc & 0xFF);
            return f;
        }

        /// <summary>解析一条已经通过 CRC 校验的完整帧。</summary>
        public static Frame ParseWhole(byte[] f)
        {
            Frame fr = new Frame();
            fr.Cmd = f[2];
            fr.DevOrStatus = f[3];
            fr.Page = f[4];
            fr.Offset = f[5];
            int len = f[6];
            fr.Data = new byte[len];
            if (len > 0) Array.Copy(f, HEADER_LEN, fr.Data, 0, len);
            return fr;
        }

        public static string Hex(byte[] data, int count)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++) sb.Append(data[i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }

    public class Frame
    {
        public byte Cmd;
        public byte DevOrStatus;
        public byte Page;
        public byte Offset;
        public byte[] Data;

        public string Describe()
        {
            return "CMD=0x" + Cmd.ToString("X2") +
                   " DEV/ST=0x" + DevOrStatus.ToString("X2") +
                   " PAGE=0x" + Page.ToString("X2") +
                   " OFFSET=" + Offset +
                   " LEN=" + Data.Length;
        }
    }

    /// <summary>
    /// 流式帧解析器 —— 整个 Demo 最核心的一个类，也是面试最容易讲清楚"我写过"的地方。
    /// 串口一次到达的字节数不保证等于一帧：可能半帧、可能两帧粘一起、可能混进噪声字节。
    /// 做法：字节先进缓冲区，按"帧头 + 长度字段"判断够不够一帧，够则取出并校 CRC，不够就等下一次。
    /// </summary>
    public class FrameParser
    {
        private readonly List<byte> buf = new List<byte>();

        public int CrcErrorCount;   // 累计校验失败次数
        public int ResyncCount;     // 累计丢弃的垃圾字节数（重同步）

        public List<byte[]> Feed(byte[] data, int count)
        {
            List<byte[]> frames = new List<byte[]>();
            for (int i = 0; i < count; i++) buf.Add(data[i]);

            while (buf.Count > 0)
            {
                // 1) 找帧头第一个字节
                if (buf[0] != Proto.SOF1)
                {
                    buf.RemoveAt(0);
                    ResyncCount++;
                    continue;
                }
                // 2) 第二个字节还没到就等；到了但不是 0x55 说明是假帧头，丢掉一字节重来
                if (buf.Count >= 2 && buf[1] != Proto.SOF2)
                {
                    buf.RemoveAt(0);
                    ResyncCount++;
                    continue;
                }
                // 3) 帧头 + 定长部分还没收齐
                if (buf.Count < Proto.HEADER_LEN) break;

                int len = buf[6];
                int total = Proto.HEADER_LEN + len + 2;
                // 4) 整帧还没收齐 —— 这就是"半包"，关键是不能丢，必须留着等
                if (buf.Count < total) break;

                byte[] f = buf.GetRange(0, total).ToArray();
                ushort calc = Proto.Crc16(f, 0, total - 2);
                ushort got = (ushort)((f[total - 2] << 8) | f[total - 1]);
                if (calc != got)
                {
                    // 校验失败：丢帧头重同步，并把这一帧记为丢弃（上层会因超时发不到而重发请求）
                    CrcErrorCount++;
                    buf.RemoveRange(0, 2);
                    continue;
                }
                frames.Add(f);
                buf.RemoveRange(0, total);
            }

            // 防呆：万一协议完全错乱，缓冲区不能无限增长
            if (buf.Count > 8192) buf.Clear();
            return frames;
        }

        public void Reset()
        {
            buf.Clear();
        }
    }
}
