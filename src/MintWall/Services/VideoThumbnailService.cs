using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MintWall.Services;

/// <summary>
/// 视频首帧缩略图：独立进程里用软件渲染的 MediaElement 抓一帧存成 JPG，
/// 避免主界面为了缩略图改成软件渲染。
/// </summary>
public static class VideoThumbnailService
{
    private const int Width = 480;
    private const int Height = 270;

    /// <summary>--video-thumb 入口：抓帧成功返回 0。</summary>
    public static int Capture(string? videoPath, string? outPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath) || string.IsNullOrWhiteSpace(outPath))
        {
            return 2;
        }

        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            MediaElement? media = null;
            DispatcherTimer? timeout = null;

            app.Startup += (_, _) =>
            {
                media = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    ScrubbingEnabled = true,
                    Volume = 0,
                    IsMuted = true,
                    Stretch = Stretch.UniformToFill,
                    Source = new Uri(videoPath),
                };

                Window window = new()
                {
                    Width = Width,
                    Height = Height,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -4000,
                    Top = -4000,
                    Background = Brushes.Black,
                    Content = media,
                };
                window.Show();

                timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                timeout.Tick += (_, _) => app.Shutdown();
                timeout.Start();

                media.MediaFailed += (_, _) => app.Shutdown();
                media.MediaOpened += (_, _) =>
                {
                    double duration = media.NaturalDuration.HasTimeSpan
                        ? media.NaturalDuration.TimeSpan.TotalSeconds
                        : 3;
                    media.Position = TimeSpan.FromSeconds(Math.Min(1.0, Math.Max(0.1, duration / 3)));

                    DispatcherTimer shot = new() { Interval = TimeSpan.FromMilliseconds(450) };
                    shot.Tick += (_, _) =>
                    {
                        shot.Stop();
                        try
                        {
                            RenderTargetBitmap bitmap = new(Width, Height, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(media);
                            JpegBitmapEncoder encoder = new() { QualityLevel = 85 };
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            string temp = outPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                            using (FileStream stream = File.Create(temp))
                            {
                                encoder.Save(stream);
                            }

                            File.Move(temp, outPath, overwrite: true);
                        }
                        catch (IOException)
                        {
                        }
                        finally
                        {
                            app.Shutdown();
                        }
                    };
                    shot.Start();
                };

                media.Play();
            };

            app.Run();
            timeout?.Stop();
            return File.Exists(outPath) ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>
    /// --video-thumbs 入口：一个进程里按清单批量抓首帧。
    /// 每条视频起一次 WPF 进程的启动开销远大于抓帧本身，批量能把 10 条的首屏时间从十几秒压到几秒。
    /// 清单是 JSON：[{"video":"...","out":"..."}]，成功条数打到 stdout。
    /// </summary>
    public static int CaptureMany(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
        {
            return 2;
        }

        BatchJob[] jobs;
        try
        {
            jobs = JsonSerializer.Deserialize<BatchJob[]>(
                File.ReadAllText(requestPath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) ?? Array.Empty<BatchJob>();
        }
        catch (Exception)
        {
            return 2;
        }

        if (jobs.Length == 0)
        {
            Console.WriteLine(0);
            return 0;
        }

        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async (_, _) =>
            {
                int ok = 0;
                try
                {
                    MediaElement media = new()
                    {
                        LoadedBehavior = MediaState.Manual,
                        UnloadedBehavior = MediaState.Manual,
                        ScrubbingEnabled = true,
                        Volume = 0,
                        IsMuted = true,
                        Stretch = Stretch.UniformToFill,
                    };

                    Window window = new()
                    {
                        Width = Width,
                        Height = Height,
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Left = -4000,
                        Top = -4000,
                        Background = Brushes.Black,
                        Content = media,
                    };
                    window.Show();

                    foreach (BatchJob job in jobs)
                    {
                        if (string.IsNullOrWhiteSpace(job.Video) || string.IsNullOrWhiteSpace(job.Out))
                        {
                            continue;
                        }

                        if (await CaptureOneAsync(media, job.Video, job.Out))
                        {
                            ok++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERR " + ex.Message);
                }
                finally
                {
                    Console.WriteLine(ok);
                    app.Shutdown();
                }
            };

            app.Run();
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private sealed class BatchJob
    {
        public string Video { get; set; } = string.Empty;

        public string Out { get; set; } = string.Empty;
    }

    /// <summary>复用同一个 MediaElement 抓一条：换 Source → 等打开 → 定位 → 抓帧存 JPG。</summary>
    private static async Task<bool> CaptureOneAsync(MediaElement media, string videoPath, string outPath)
    {
        TaskCompletionSource<bool> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler onOpened = (_, _) => opened.TrySetResult(true);
        EventHandler<ExceptionRoutedEventArgs> onFailed = (_, _) => failed.TrySetResult(true);
        media.MediaOpened += onOpened;
        media.MediaFailed += onFailed;

        try
        {
            media.Source = new Uri(videoPath);
            media.Play();

            Task first = await Task.WhenAny(opened.Task, failed.Task, Task.Delay(TimeSpan.FromSeconds(8)));
            if (first != opened.Task)
            {
                return false;
            }

            double duration = media.NaturalDuration.HasTimeSpan
                ? media.NaturalDuration.TimeSpan.TotalSeconds
                : 3;
            media.Position = TimeSpan.FromSeconds(Math.Min(1.0, Math.Max(0.1, duration / 3)));
            await Task.Delay(450);

            RenderTargetBitmap bitmap = new(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(media);
            JpegBitmapEncoder encoder = new() { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string temp = outPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            using (FileStream stream = File.Create(temp))
            {
                encoder.Save(stream);
            }

            File.Move(temp, outPath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            media.MediaOpened -= onOpened;
            media.MediaFailed -= onFailed;
            media.Pause();
        }
    }

    /// <summary>
    /// --video-bars 入口：量一帧四边纯黑比例，输出 "top;bottom;left;right"（0~1）。
    /// 宿主进程的 RenderTargetBitmap 抓不到硬件合成的视频帧，所以黑边检测放在这个
    /// 本来就强制软件渲染的子进程里做，宿主解析结果再决定放大裁剪多少。
    /// </summary>
    public static int DetectBars(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return 2;
        }

        string result = "0;0;0;0";
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            MediaElement? media = null;
            DispatcherTimer? timeout = null;
            int attempt = 0;
            int validSamples = 0;
            double[]? bars = null;

            app.Startup += (_, _) =>
            {
                media = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    ScrubbingEnabled = true,
                    Volume = 0,
                    IsMuted = true,
                    // Fill：把整帧（含内嵌黑边）压进小窗口，黑边占比不变，方便量。
                    Stretch = Stretch.Fill,
                    Source = new Uri(videoPath),
                };

                Window window = new()
                {
                    Width = 240,
                    Height = 135,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -4000,
                    Top = -4000,
                    Background = Brushes.Black,
                    Content = media,
                };
                window.Show();

                timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                timeout.Tick += (_, _) => app.Shutdown();
                timeout.Start();

                media.MediaFailed += (_, _) => app.Shutdown();
                media.MediaOpened += (_, _) =>
                {
                    media.Position = TimeSpan.FromSeconds(1);
                    DispatcherTimer shot = new() { Interval = TimeSpan.FromMilliseconds(900) };
                    shot.Tick += (_, _) =>
                    {
                        attempt++;
                        double[]? measured = MeasureBars(media);
                        if (measured is not null)
                        {
                            // 多帧取每边最小值：黑边是常量，暗场景里贴边的暗色块是偶发的。
                            bars = bars is null ? measured : MergeBars(bars, measured);
                            validSamples++;
                            if (validSamples >= 2)
                            {
                                result = FormatBars(ClampSymmetric(bars));
                                shot.Stop();
                                app.Shutdown();
                                return;
                            }
                        }

                        if (attempt >= 4)
                        {
                            // 抓不到足够多的有效帧（全黑暗场景/解码失败）：有多少算多少。
                            if (bars is not null)
                            {
                                result = FormatBars(ClampSymmetric(bars));
                            }

                            shot.Stop();
                            app.Shutdown();
                            return;
                        }

                        media.Position = TimeSpan.FromSeconds(1 + attempt * 2.0);
                    };
                    shot.Start();
                };

                media.Play();
            };

            app.Run();
            timeout?.Stop();
        }
        catch (Exception)
        {
            return 1;
        }

        Console.WriteLine(result);
        return 0;
    }

    /// <summary>抓一帧量四边黑边行/列占比；全黑帧或暗场景返回 null，让调用方换个时间点再试。</summary>
    private static double[]? MeasureBars(MediaElement media)
    {
        const int width = 240;
        const int height = 135;
        try
        {
            RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(media);

            byte[] pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);

            // 用行/列平均亮度判断：视频黑电平常是 limited range（RGB≈16），阈值取 24；
            // 缩放插值还会把黑边内侧一行混成灰，逐像素判定会低估。
            double RowMean(int y)
            {
                double sum = 0;
                for (int x = 0; x < width; x += 2)
                {
                    int i = (y * width + x) * 4;
                    sum += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
                }

                return sum / (width / 2.0);
            }

            double ColumnMean(int x)
            {
                double sum = 0;
                for (int y = 0; y < height; y += 2)
                {
                    int i = (y * width + x) * 4;
                    sum += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
                }

                return sum / (height / 2.0);
            }

            int top = 0;
            while (top < height && RowMean(top) < 24)
            {
                top++;
            }

            int bottom = 0;
            while (bottom < height - top && RowMean(height - 1 - bottom) < 24)
            {
                bottom++;
            }

            int left = 0;
            while (left < width && ColumnMean(left) < 24)
            {
                left++;
            }

            int right = 0;
            while (right < width - left && ColumnMean(width - 1 - right) < 24)
            {
                right++;
            }

            if (top + bottom >= height || left + right >= width)
            {
                return null;
            }

            // 中间内容不能也是黑的，否则暗场景会被误判成整帧黑边。
            double sum = 0;
            int count = 0;
            for (int y = top; y < height - bottom; y += 2)
            {
                for (int x = left; x < width - right; x += 2)
                {
                    int i = (y * width + x) * 4;
                    sum += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
                    count++;
                }
            }

            if (count == 0 || sum / count < 25)
            {
                return null;
            }

            return new[]
            {
                (double)top / height,
                (double)bottom / height,
                (double)left / width,
                (double)right / width,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double[] MergeBars(double[] a, double[] b)
    {
        double[] merged = new double[4];
        for (int i = 0; i < 4; i++)
        {
            merged[i] = Math.Min(a[i], b[i]);
        }

        return merged;
    }

    /// <summary>
    /// 片源自带黑边基本是对称的；单边远大于另一边时按“内容边缘是暗色”处理取小值，
    /// 否则暗场景里贴边的一列黑树也会被当成黑边。
    /// </summary>
    private static double[] ClampSymmetric(double[] bars)
    {
        double top = bars[0];
        double bottom = bars[1];
        double left = bars[2];
        double right = bars[3];

        if (Math.Max(top, bottom) > 0 && Math.Abs(top - bottom) > Math.Max(top, bottom) * 0.5)
        {
            top = bottom = Math.Min(top, bottom);
        }

        if (Math.Max(left, right) > 0 && Math.Abs(left - right) > Math.Max(left, right) * 0.5)
        {
            left = right = Math.Min(left, right);
        }

        return new[] { top, bottom, left, right };
    }

    private static string FormatBars(double[] bars) => string.Join(';', new[]
    {
        bars[0].ToString("F4", CultureInfo.InvariantCulture),
        bars[1].ToString("F4", CultureInfo.InvariantCulture),
        bars[2].ToString("F4", CultureInfo.InvariantCulture),
        bars[3].ToString("F4", CultureInfo.InvariantCulture),
    });
}
