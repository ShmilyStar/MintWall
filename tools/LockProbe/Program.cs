using System.Runtime.InteropServices;
using System.Threading;
using Windows.Storage;
using Windows.System.UserProfile;

string[] candidates = args.Length > 0
    ? args
    : new[]
    {
        @"C:\Users\Home\AppData\Local\WallpaperStudio\wallpapers\20261106-193338-820da4f5.jpg",
        @"C:\Users\Home\AppData\Local\WallpaperStudio\thumbs",
    };

foreach (string raw in candidates)
{
    if (!File.Exists(raw))
    {
        Console.WriteLine($"skip (not a file): {raw}");
        continue;
    }

    RunOne(Path.GetFullPath(raw));
}

static void RunOne(string path)
{
    Console.WriteLine();
    Console.WriteLine("=== " + path);

    Thread worker = new(() => Work(path));
    worker.SetApartmentState(ApartmentState.STA);
    worker.Start();
    worker.Join();
}

static void Work(string path)
{
    try
    {
        StorageFile file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"  storage file: type={file.FileType} attrs={file.Attributes}");

        bool ok = UserProfilePersonalizationSettings.Current
            .TrySetLockScreenImageAsync(file)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        Console.WriteLine($"  UserProfilePersonalizationSettings.TrySetLockScreenImageAsync -> {ok}");

        try
        {
            LockScreen.SetImageFileAsync(file).AsTask().GetAwaiter().GetResult();
            Console.WriteLine("  LockScreen.SetImageFileAsync -> returned without error");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  LockScreen.SetImageFileAsync threw: " + Describe(ex));
        }

        Console.WriteLine("  OriginalImageFile now: " + LockScreen.OriginalImageFile);
    }
    catch (Exception ex)
    {
        Console.WriteLine("  failed: " + Describe(ex));
    }
}

static string Describe(Exception ex)
{
    Exception root = ex;
    while (root.InnerException is not null)
    {
        root = root.InnerException;
    }

    return $"{root.GetType().Name} 0x{root.HResult:x8}: {root.Message}";
}
