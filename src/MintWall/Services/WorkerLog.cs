using System.IO;
using System.Text;

namespace MintWall.Services;

/// <summary>后台进程没有界面，留一份小滚动日志，便于确认换图是否在正常发生。</summary>
public static class WorkerLog
{
    private const long SizeLimit = 512 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string file = Path.Combine(DataPaths.Root, "worker.log");
                if (File.Exists(file) && new FileInfo(file).Length > SizeLimit)
                {
                    File.WriteAllText(file, string.Empty);
                }

                File.AppendAllText(file, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}