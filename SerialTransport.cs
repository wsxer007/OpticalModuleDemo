// ============================ 阶段二才用的文件 ============================
// 默认已被 OpticalModuleDemo.csproj 里的 DefaultItemExcludes 排除，不参与编译。
// 要用真实串口时：
//   1) VS 菜单【工具 → NuGet 包管理器 → 管理解决方案的 NuGet 程序包】搜索安装 System.IO.Ports
//      （如果你的项目是 .NET Framework 4.8，就不用装包，直接添加对 System.IO.Ports 的引用）
//   2) 删掉 csproj 里 DefaultItemExcludes 中的 SerialTransport.cs
//   3) 在 MainForm.ConnectAsync() 里把 new SimTransport() 换成 new SerialTransport(){PortName="COM4"}
// 没有板卡时可以用 com0com 建一对虚拟串口（COM3<->COM4），
// 再另开一个小程序把 COM3 收到的字节原样写回去，就能验证真实的串口行为。
// =====================================================================
using System;
using System.IO.Ports;

namespace OpticalModuleDemo
{
    public class SerialTransport : ITransport
    {
        private SerialPort port;

        public string PortName = "COM4";
        public int BaudRate = 115200;

        public event Action<byte[], int> DataReceived;

        public string Name { get { return "串口 " + PortName; } }

        public bool IsOpen { get { return port != null && port.IsOpen; } }

        public void Open()
        {
            if (IsOpen) return;                       // 打开前先判断，避免重复 Open 抛异常
            port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One);
            port.ReadTimeout = 500;
            port.WriteTimeout = 500;
            port.DataReceived += OnPortData;
            port.ErrorReceived += OnPortError;
            port.Open();   // 抛 UnauthorizedAccessException 一般是串口被别的程序（串口助手）占用了
        }

        public void Close()
        {
            try
            {
                if (port != null)
                {
                    port.DataReceived -= OnPortData;
                    port.ErrorReceived -= OnPortError;
                    if (port.IsOpen) port.Close();
                    port.Dispose();
                }
            }
            catch (Exception)
            {
                // 现场关串口偶尔会抛，必须吞掉并保证资源释放，否则程序退不出去
            }
            finally
            {
                port = null;
            }
        }

        public void Send(byte[] data)
        {
            SerialPort p = port;
            if (p == null || !p.IsOpen) return;
            p.Write(data, 0, data.Length);
        }

        private void OnPortData(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                SerialPort p = port;
                if (p == null) return;
                int n = p.BytesToRead;
                if (n <= 0) return;
                byte[] buf = new byte[n];
                int got = p.Read(buf, 0, n);
                Action<byte[], int> h = DataReceived;
                // 注意：SerialPort 的事件本来就在线程池线程上，所以上位机更新界面必须 Invoke
                if (h != null) h(buf, got);
            }
            catch (TimeoutException)
            {
            }
            catch (Exception)
            {
            }
        }

        private void OnPortError(object sender, SerialErrorReceivedEventArgs e)
        {
            // 真实项目这里要落日志：Overrun / Framing / ReceiveErrors / TransmitErrors
        }
    }
}
