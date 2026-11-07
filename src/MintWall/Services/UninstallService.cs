using System.IO;

namespace MintWall.Services;

/// <summary>
/// 卸载清理：删掉程序自己的默认数据目录（设置、壁纸库、缓存、缩略图、保存归档），
/// 用户在工作区里自选的壁纸库/保存目录不在清理范围内。
/// </summary>
public static class UninstallService
{
    public static bool Purge()
    {
        StartupService.Remove();
        RemoveStartShortcuts();

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        bool ok = true;

        foreach (string name in new[] { "MintWall", "WallpaperStudio" })
        {
            string dir = Path.Combine(local, name);
            if (!DeleteDirectory(dir))
            {
                ok = false;
                Console.WriteLine("删除失败（可能程序还在运行）：" + dir);
            }
            else
            {
                Console.WriteLine("已删除：" + dir);
            }
        }

        Console.WriteLine("用户自选的壁纸库/保存目录不会被删除。");
        return ok;
    }

    private static void RemoveStartShortcuts()
    {
        string programs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        foreach (string name in new[] { "薄荷壁纸", "MintWall" })
        {
            string lnk = Path.Combine(programs, name + ".lnk");
            try
            {
                if (File.Exists(lnk))
                {
                    File.Delete(lnk);
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

    private static bool DeleteDirectory(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(150);
            }
        }

        return !Directory.Exists(path);
    }
}
