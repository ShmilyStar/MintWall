using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        bool apply = args.Contains("--apply");
        string url = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? "https://t.alcy.cc/ycy";

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("== 1. SkiaSharp API surface ==");
        ReportSkiaSurface();

        Console.WriteLine();
        Console.WriteLine($"== 2. Fetch {url} ==");
        byte[] body;
        string contentType;
        string finalUrl;
        try
        {
            using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
            {
                Timeout = TimeSpan.FromSeconds(45),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MintWall-Probe/1.0");
            using HttpResponseMessage resp = client.GetAsync(url).GetAwaiter().GetResult();
            body = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            contentType = resp.Content.Headers.ContentType?.MediaType ?? "?";
            finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            Console.WriteLine($"status={(int)resp.StatusCode} type={contentType} bytes={body.Length} final={finalUrl}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FETCH FAILED: " + ex.Message);
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("== 3. Decode + encode to jpg ==");
        string outPath = Path.Combine(Path.GetTempPath(), $"ws-probe-{Guid.NewGuid():N}.jpg");
        try
        {
            using SKBitmap bitmap = SKBitmap.Decode(body) ?? throw new InvalidOperationException("decode returned null");
            Console.WriteLine($"decoded {bitmap.Width}x{bitmap.Height} alphaType={bitmap.Info.AlphaType}");
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 90) ?? throw new InvalidOperationException("encode returned null");
            using (FileStream fs = File.Create(outPath))
            {
                data.SaveTo(fs);
            }

            Console.WriteLine($"saved {outPath} ({new FileInfo(outPath).Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("DECODE/ENCODE FAILED: " + ex);
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("== 4. IDesktopWallpaper interop ==");
        IDesktopWallpaper? api = DesktopWallpaperFactory.Create();
        if (api is null)
        {
            Console.WriteLine("COM activation failed");
            return 3;
        }

        api.GetMonitorDevicePathCount(out uint count);
        Console.WriteLine($"monitor count = {count}");
        string firstId = string.Empty;
        for (uint i = 0; i < count; i++)
        {
            string id = api.GetMonitorDevicePathAt(i);
            api.GetMonitorRECT(id, out RECT rect);
            string current;
            try
            {
                current = api.GetWallpaper(id);
            }
            catch (Exception ex)
            {
                current = "<error: " + ex.Message + ">";
            }

            if (i == 0)
            {
                firstId = id;
            }

            Console.WriteLine($"[{i}] id={Shorten(id)} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} size={rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
            Console.WriteLine($"    wallpaper={current}");
        }

        api.GetPosition(out DesktopWallpaperPosition position);
        Console.WriteLine($"position = {position}");

        StringBuilder legacy = new(520);
        bool read = NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETDESKWALLPAPER, legacy.Capacity, legacy, 0);
        Console.WriteLine($"legacy wallpaper ({read}) = {legacy}");

        if (!apply || firstId.Length == 0)
        {
            Console.WriteLine();
            Console.WriteLine("未修改壁纸（加 --apply 才会真正设置并自动还原）。");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("== 5. Apply + restore ==");
        string original = legacy.ToString();
        try
        {
            api.SetPosition(DesktopWallpaperPosition.Fill);
            api.SetWallpaper(null, outPath);
            Console.WriteLine("set to probe image OK");
            Console.WriteLine("now wallpaper = " + api.GetWallpaper(null));
            Thread.Sleep(1200);
        }
        finally
        {
            if (!string.IsNullOrEmpty(original) && File.Exists(original))
            {
                api.SetWallpaper(null, original);
                Console.WriteLine("restored original wallpaper: " + original);
            }
            else
            {
                Console.WriteLine("原始壁纸路径不可用，未还原：" + original);
            }
        }

        return 0;
    }

    private static string Shorten(string value) => value.Length <= 90 ? value : value[..90] + "...";

    private static void ReportSkiaSurface()
    {
        string[] probes =
        {
            "SKBitmap.Decode(Byte[])",
            "SKBitmap.Resize(SKImageInfo,SKFilterQuality)",
            "SKImage.FromBitmap(SKBitmap)",
            "SKImage.Encode(SKEncodedImageFormat,Int32)",
            "SKData.SaveTo(Stream)",
            "SKCodec.Create(SKStream)",
            "SKManagedStream..ctor(Stream)",
            "SKBitmap.Info",
        };

        foreach (string probe in probes)
        {
            int dot = probe.LastIndexOf('.');
            string memberName = probe[(dot + 1)..];
            string typeName = "SkiaSharp." + probe[..dot];
            Type? type = Type.GetType(typeName + ", SkiaSharp");
            if (type is null)
            {
                // ctor probes name the type differently
                type = Type.GetType("SkiaSharp." + probe.Split("..")[0] + ", SkiaSharp");
            }

            if (type is null)
            {
                Console.WriteLine($"  {probe,-45} TYPE MISSING");
                continue;
            }

            MemberInfo? hit = type.GetMember(memberName.Split('.')[0]).FirstOrDefault();
            Console.WriteLine($"  {probe,-45} {(hit is null ? "MISSING" : "ok")}");
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

internal enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 5,
    Fit = 6,
    Fill = 10,
    Span = 14,
}

[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-864BBB22F64B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorDevicePathAt(uint monitorIndex);

    void GetMonitorDevicePathCount(out uint count);

    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT rect);

    void SetBackgroundColor(uint color);

    void GetBackgroundColor(out uint color);

    void SetPosition(DesktopWallpaperPosition position);

    void GetPosition(out DesktopWallpaperPosition position);

    void SetSlideshow([MarshalAs(UnmanagedType.Interface)] object? items);

    void GetSlideshowOptions(out uint options, out uint direction);

    void GetSlideshowCurrentItem([MarshalAs(UnmanagedType.LPWStr)] out string item);

    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, uint direction);

    void GetStatus(out uint status);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorIDAt(uint monitorIndex);
}

internal static class DesktopWallpaperFactory
{
    public static IDesktopWallpaper? Create()
    {
        try
        {
            Type? type = Type.GetTypeFromCLSID(new Guid("44AAE637-3983-4031-9F66-D4C6358D8EE1"), throwOnError: false);
            return type is null ? null : (IDesktopWallpaper?)Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            Console.WriteLine("activation error: " + ex.Message);
            return null;
        }
    }
}

internal static class NativeMethods
{
    public const int SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(int action, int param, StringBuilder pvParam, int flags);
}
