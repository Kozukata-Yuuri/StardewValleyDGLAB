using QRCoder;
using StardewValleyDGLAB.Config;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace StardewValleyDGLAB.SocketConn
{
    internal class QrCode
    {
        /// <summary>
        /// 用于生成二维码
        /// </summary>
        public static void QRCode()
        {
            string modFolder = ConfigManager.ModPath;
            string subFolder = Path.Combine(modFolder, "QRCode");
            Directory.CreateDirectory(subFolder);
            string filePath = Path.Combine(subFolder, "QRCode.png");

            string ip;
            if (ModEntry.Config.AutoIP || string.IsNullOrEmpty(ModEntry.Config.IP))
            {
                ip = GetLocalIPv4();
            }
            else
            {
                ip = ModEntry.Config.IP;
            }
            try
            {
                // V4 协议的二维码格式:
                // https://dungeon-lab.cn/s/?v=1&action=socket&url={URL编码后的ws地址}
                // ws 地址里用 ?tid= 带上控制方 ID(DGLABServer.TargetId),App 靠它找到我们
                string wsUrl = $"ws://{ip}:{ModEntry.Config.Port}/v4?tid={DGLABServer.TargetId}";
                string qrText = "https://dungeon-lab.cn/s/?v=1&action=socket&url=" + Uri.EscapeDataString(wsUrl);
                // 生成二维码
                QRCodeGenerator qrGenerator = new QRCodeGenerator();
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrText, QRCodeGenerator.ECCLevel.Q);
                PngByteQRCode pngQrCode = new PngByteQRCode(qrCodeData);//
                byte[] pngData = pngQrCode.GetGraphic(20);
                // 保存图片
                File.WriteAllBytes(filePath, pngData);//将二维码图片保存到指定路径
            }
            catch (Exception ex)
            {
                return;
            }
            // 用默认程序打开图片
            filePath = Path.GetFullPath(filePath);//规范文件路径,同时确保是绝对路径
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true  // 依赖 ShellExecute，调用系统默认程序
                });
            }
            catch
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                }
                catch
                {
                }
            }
        }
        public static string GetLocalIPv4()//获取玩家电脑在局域网中的地址
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530); // 连接到公共 DNS，不实际发送数据
                    if (socket.LocalEndPoint is IPEndPoint endPoint)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                return null;
            }
            return null;
        }
        /// <summary>
        /// 打开二维码文件夹
        /// </summary>
        public static void OpenQRFolder()
        {
            string modFolder = ModEntry.ModFolderPath;
            string subFolder = Path.Combine(modFolder, "QRCode", "QRCode.png");
            try
            {
                Process.Start("explorer.exe", $"/select,\"{subFolder}\"");
            }
            catch
            {
            }
        }
    }
}
