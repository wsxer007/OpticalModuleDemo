using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpticalModuleDemo
{
    /// <summary>
    /// 上位机的通信层：一条在途请求 + 超时重发 + 分帧连续读写。
    /// 这一层把"串口/网口"和"协议语义"隔开，界面上点按钮只需要 await 就行。
    /// </summary>
    public class BoardClient
    {
        private readonly ITransport transport;
        private readonly FrameParser parser = new FrameParser();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1); // 一次只允许一条请求在途
        private volatile TaskCompletionSource<Frame> pending;

        public event Action<string> OnLog;

        public int TimeoutCount;
        public int RequestCount;

        public int CrcErrorCount { get { return parser.CrcErrorCount; } }
        public int ResyncByteCount { get { return parser.ResyncCount; } }

        public BoardClient(ITransport t)
        {
            transport = t;
            transport.DataReceived += HandleBytes;
        }

        private void Log(string s)
        {
            Action<string> h = OnLog;
            if (h != null) h(s);
        }

        private void HandleBytes(byte[] data, int count)
        {
            List<byte[]> frames = parser.Feed(data, count);
            foreach (byte[] f in frames)
            {
                Frame fr = Proto.ParseWhole(f);
                Log("    <- 收帧 " + fr.Describe() + "  [" + Proto.Hex(f, f.Length) + "]");
                TaskCompletionSource<Frame> tcs = pending;
                if (tcs != null) tcs.TrySetResult(fr);
            }
            if (frames.Count == 0 && count > 0)
            {
                Log("    .. 收到 " + count + " 字节但凑不出完整帧（半包/脏数据），继续等待。累计重同步丢弃 "
                    + parser.ResyncCount + " 字节");
            }
        }

        public static string StatusName(byte st)
        {
            switch (st)
            {
                case Proto.ST_OK: return "正常";
                case Proto.ST_CRC: return "校验失败";
                case Proto.ST_WPROT: return "写保护拒绝";
                case Proto.ST_BADPARAM: return "参数非法";
                case Proto.ST_NOACK: return "从机无应答(无ACK)";
                default: return "未知状态 0x" + st.ToString("X2");
            }
        }

        /// <summary>发一条请求并等应答；超时会自动重试 retry 次。</summary>
        public async Task<Frame> RequestAsync(byte cmd, byte page, byte offset, byte[] payload, int timeoutMs, int retry)
        {
            await gate.WaitAsync();
            try
            {
                for (int attempt = 1; attempt <= retry; attempt++)
                {
                    TaskCompletionSource<Frame> tcs = new TaskCompletionSource<Frame>();
                    pending = tcs;

                    byte[] frame = Proto.Build(cmd, 0x50, page, offset, payload);
                    RequestCount++;
                    Log("  -> 发帧 CMD=0x" + cmd.ToString("X2") + " PAGE=0x" + page.ToString("X2")
                        + " OFFSET=" + offset + " 第" + attempt + "次 [" + Proto.Hex(frame, frame.Length) + "]");
                    transport.Send(frame);

                    Task winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                    if (winner == tcs.Task)
                    {
                        return await tcs.Task;
                    }
                    TimeoutCount++;
                    Log("  !! 等待应答超时(" + timeoutMs + "ms)，已重试 " + attempt + "/" + retry
                        + "。真实场景这里可能是波特率不匹配、板卡忙、线被拔了、或应答帧被干扰丢掉");
                }
                throw new TimeoutException("重试 " + retry + " 次后仍未收到有效应答");
            }
            finally
            {
                pending = null;
                gate.Release();
            }
        }

        public async Task<string> PingAsync()
        {
            Frame f = await RequestAsync(Proto.CMD_PING, 0, 0, null, 1000, 2);
            if (f.DevOrStatus != Proto.ST_OK) throw new Exception("握手失败：" + StatusName(f.DevOrStatus));
            return System.Text.Encoding.ASCII.GetString(f.Data);
        }

        public async Task<bool> UnlockAsync(byte[] pwd)
        {
            Frame f = await RequestAsync(Proto.CMD_UNLOCK, 0, 0, pwd, 1000, 2);
            return f.DevOrStatus == Proto.ST_OK;
        }

        /// <summary>连续读：一帧最多 MAX_DATA 字节，所以读 128 字节要自动拆成若干帧。</summary>
        public async Task<byte[]> ReadBlockAsync(byte page, byte offset, int count)
        {
            List<byte> all = new List<byte>();
            int pos = offset;
            int left = count;
            while (left > 0)
            {
                int n = Math.Min(left, Proto.MAX_DATA);
                Frame f = await RequestAsync(Proto.CMD_BULK_READ, page, (byte)pos, new byte[] { (byte)n }, 1000, 3);
                if (f.DevOrStatus != Proto.ST_OK) throw new Exception("读取失败：" + StatusName(f.DevOrStatus));
                all.AddRange(f.Data);
                pos += n;
                left -= n;
            }
            return all.ToArray();
        }

        public async Task WriteBlockAsync(byte page, byte offset, byte[] data)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                int n = Math.Min(data.Length - pos, 16);
                byte[] chunk = new byte[n];
                Array.Copy(data, pos, chunk, 0, n);
                Frame f = await RequestAsync(Proto.CMD_WRITE, page, (byte)(offset + pos), chunk, 1000, 3);
                if (f.DevOrStatus != Proto.ST_OK)
                    throw new BoardException(StatusName(f.DevOrStatus), f.DevOrStatus);
                pos += n;
            }
        }
    }

    /// <summary>把状态码带上，界面能区分"写保护"和其它错误。</summary>
    public class BoardException : Exception
    {
        public byte Status;
        public BoardException(string msg, byte status) : base(msg) { Status = status; }
    }
}
