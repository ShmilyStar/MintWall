using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace MintWall.Startup
{
    /// <summary>
    /// MSIX startupTask 的启动壳：系统启动任务不支持传参数，这里显式用 --background 拉起主程序，
    /// 保证开机只进后台、不弹主界面；用户点图标仍走系统激活（无参数），才会加载界面。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            string exe = Path.Combine(dir, "MintWall.exe");
            if (!File.Exists(exe))
            {
                return;
            }

            ProcessStartInfo info = new ProcessStartInfo(exe, "--background");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WorkingDirectory = dir;

            try
            {
                Process.Start(info);
            }
            catch
            {
                // 启动壳只负责拉起主程序；失败时不做任何弹窗，避免开机被拦。
            }
        }
    }
}
