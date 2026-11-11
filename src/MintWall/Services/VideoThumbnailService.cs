using System.IO;
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
}
