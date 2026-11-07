using System.Net.Http;
using System.Text;
using SkiaSharp;
using MintWall.Models;

namespace MintWall.Services;

/// <summary>
/// Walks a candidate endpoint without touching the desktop, so a wrong URL is obvious before it is saved.
/// </summary>
public sealed record ProbeResult(string Report, byte[]? ImageBytes, int Width, int Height);

public sealed class ProbeService
{
    public async Task<ProbeResult> ProbeAsync(SourceProfile source, EncodePreference encode, CancellationToken ct)
    {
        StringBuilder report = new();
        List<string> trace = new();
        report.AppendLine($"源名称：{source.Name}");
        report.AppendLine($"接口地址：{source.Url}");
        report.AppendLine($"解析方式：{source.Kind}{(source.Kind == SourceKind.Json && !string.IsNullOrWhiteSpace(source.JsonFieldPath) ? "（字段 " + source.JsonFieldPath + "）" : string.Empty)}");
        report.AppendLine(new string('-', 60));

        ImageService.FetchResult first;
        if (source.Kind == SourceKind.Bing)
        {
            ImageService.FetchResult archive = await ImageService.FetchAsync(BingService.ArchiveUrl, null, ct, trace).ConfigureAwait(false);
            report.AppendLine($"第 1 跳（归档 JSON）：HTTP 200，{ImageService.FormatSize(archive.Body.Length)}");
            (string standard, string? uhd) = BingService.ReadImageUrls(archive.Body)
                ?? throw new InvalidOperationException("必应归档接口没有返回当天的图片地址。");
            report.AppendLine($"        解析到图片链接 = {standard}");
            if (uhd is not null)
            {
                try
                {
                    first = await ImageService.FetchAsync(uhd, null, ct, trace).ConfigureAwait(false);
                    report.AppendLine($"第 2 跳（UHD）：HTTP 200，{ImageService.FormatSize(first.Body.Length)}");
                }
                catch (HttpRequestException)
                {
                    first = await ImageService.FetchAsync(standard, null, ct, trace).ConfigureAwait(false);
                    report.AppendLine($"第 2 跳（标准尺寸）：HTTP 200，{ImageService.FormatSize(first.Body.Length)}");
                }
            }
            else
            {
                first = await ImageService.FetchAsync(standard, null, ct, trace).ConfigureAwait(false);
                report.AppendLine($"第 2 跳：HTTP 200，{ImageService.FormatSize(first.Body.Length)}");
            }
        }
        else
        {
            first = await ImageService.FetchAsync(source.Url, source.Headers, ct, trace).ConfigureAwait(false);
            report.AppendLine($"第 1 跳：HTTP {first.StatusCode}，Content-Type = {first.ContentType}，{ImageService.FormatSize(first.Body.Length)}");
        }

        report.AppendLine($"        最终地址 = {first.FinalUrl}");

        foreach (string line in trace)
        {
            report.AppendLine("        轨迹：" + line);
        }

        string format = ImageService.SniffFormat(first.Body);
        byte[] imageBytes = first.Body;

        if (source.Kind is SourceKind.Json or SourceKind.Auto && ImageService.IsJson(first))
        {
            string json = Encoding.UTF8.GetString(first.Body);
            report.AppendLine("        响应是 JSON，正在寻找图片链接…");
            string? imageUrl;
            try
            {
                imageUrl = ImageService.ExtractImageUrl(json, source.JsonFieldPath);
            }
            catch (InvalidOperationException ex)
            {
                report.AppendLine("        失败：" + ex.Message);
                report.AppendLine();
                report.AppendLine("JSON 片段：");
                report.AppendLine(json.Length > 800 ? json[..800] + " …" : json);
                return new ProbeResult(report.ToString(), null, 0, 0);
            }

            if (imageUrl is null)
            {
                report.AppendLine("        没找到形如 *.jpg/.png/.webp 的链接。请手动填写字段路径，例如 data.url");
                report.AppendLine();
                report.AppendLine("JSON 片段：");
                report.AppendLine(json.Length > 800 ? json[..800] + " …" : json);
                return new ProbeResult(report.ToString(), null, 0, 0);
            }

            report.AppendLine($"        解析到图片链接 = {imageUrl}");
            ImageService.FetchResult second = await ImageService.FetchAsync(imageUrl, source.Headers, ct, trace).ConfigureAwait(false);
            report.AppendLine($"第 2 跳：HTTP 200，Content-Type = {second.ContentType}，{ImageService.FormatSize(second.Body.Length)}");
            imageBytes = second.Body;
        }

        using SKBitmap? bitmap = SKBitmap.Decode(imageBytes);
        if (bitmap is null)
        {
            report.AppendLine();
            report.AppendLine("结果：解码失败。返回内容不是图片（识别为 " + format + "）。");
            string preview = Encoding.UTF8.GetString(imageBytes, 0, Math.Min(imageBytes.Length, 400));
            report.AppendLine("内容前 400 字符：");
            report.AppendLine(preview);
            return new ProbeResult(report.ToString(), null, 0, 0);
        }

        bool hasAlpha = bitmap.Info.AlphaType != SKAlphaType.Opaque && format is not ("jpg" or "jpeg" or "bmp");
        string saved = encode switch
        {
            EncodePreference.Png => "png",
            EncodePreference.Jpeg => "jpg",
            _ => hasAlpha ? "png" : format is "jpeg" or "jpg" or "png" ? (format == "jpeg" ? "jpg" : format) : "jpg",
        };

        report.AppendLine();
        report.AppendLine("结果：可用。");
        report.AppendLine($"        尺寸 = {bitmap.Width} × {bitmap.Height}");
        report.AppendLine($"        原始格式 = {format}，入库格式 = {saved}");
        report.AppendLine($"        提示 = {(bitmap.Width >= 1920 ? "分辨率足够" : "分辨率偏低，全屏可能会有锯齿")}");
        return new ProbeResult(report.ToString(), imageBytes, bitmap.Width, bitmap.Height);
    }
}
