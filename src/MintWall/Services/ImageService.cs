using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using MintWall.Models;

namespace MintWall.Services;

public sealed record AcquiredImage(
    string FilePath,
    string Format,
    int Width,
    int Height,
    long Bytes,
    string SourceUrl,
    IReadOnlyList<string> Trace);

/// <summary>
/// Fetches wallpaper bytes from an API endpoint, tolerating the two shapes real wallpaper APIs use:
/// a body that already is the image (possibly behind a redirect) and a JSON body naming an image URL.
/// </summary>
public static class ImageService
{
    private static readonly HttpClient Http = CreateClient();

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 MintWall/1.0";

    private const int MaxRedirects = 10;

    private const long MaxBodyBytes = 64L * 1024 * 1024;

    /// <summary>壁纸接口正常只返回几 MB；失效链接跳到视频/大文件时及时止损，别把几百 MB 读进内存。</summary>
    private static async Task<byte[]> ReadBodyLimitedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long declared && declared > MaxBodyBytes)
        {
            throw new HttpRequestException($"接口声明的内容超过 64 MB（{FormatSize(declared)}），已停止下载。");
        }

        using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new HttpRequestException("接口返回的内容超过 64 MB，已停止下载。");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static HttpClient CreateClient()
    {
        // 自己跟随跳转：.NET 默认拒绝 https → http 的降级重定向，而部分壁纸站正是这么跳的。
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    public static async Task<AcquiredImage> AcquireAsync(
        SourceProfile source,
        string targetDir,
        EncodePreference encode,
        CancellationToken ct)
    {
        List<string> trace = new();

        if (source.Kind == SourceKind.Spotlight)
        {
            string? local = SpotlightService.LocateCurrent();
            if (local is null)
            {
                throw new InvalidOperationException("这台机器上没找到 Windows 聚焦的本地图，先添加一个网络壁纸源。");
            }

            byte[] spotlightBody = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
            trace.Add($"本机聚焦 {Path.GetFileName(local)} {FormatSize(spotlightBody.LongLength)}");
            return SaveToLibrary(spotlightBody, SpotlightService.SourceUrl, targetDir, encode, trace);
        }

        if (source.Kind == SourceKind.Bing)
        {
            FetchResult archive = await FetchAsync(BingService.ArchiveUrl, null, ct, trace).ConfigureAwait(false);
            (string standard, string? uhd) = BingService.ReadImageUrls(archive.Body)
                ?? throw new InvalidOperationException("必应归档接口没有返回当天的图片地址。");

            if (uhd is not null)
            {
                try
                {
                    FetchResult big = await FetchAsync(uhd, null, ct, trace).ConfigureAwait(false);
                    return SaveToLibrary(big.Body, big.FinalUrl, targetDir, encode, trace);
                }
                catch (HttpRequestException)
                {
                    trace.Add("必应 UHD 不可用，改用标准尺寸");
                }
            }

            FetchResult image = await FetchAsync(standard, null, ct, trace).ConfigureAwait(false);
            return SaveToLibrary(image.Body, image.FinalUrl, targetDir, encode, trace);
        }

        FetchResult first = await FetchAsync(source.Url, source.Headers, ct, trace).ConfigureAwait(false);

        string? imageUrl = null;
        byte[] imageBytes = first.Body;

        bool looksJson = IsJson(first);
        if (source.Kind == SourceKind.Json || (source.Kind == SourceKind.Auto && looksJson))
        {
            imageUrl = ExtractImageUrl(Encoding.UTF8.GetString(first.Body), source.JsonFieldPath);
            if (imageUrl is null)
            {
                throw new InvalidOperationException("响应是 JSON，但没找到可用的图片链接。可填写字段路径，例如 data.url。");
            }

            FetchResult second = await FetchAsync(imageUrl, source.Headers, ct, trace).ConfigureAwait(false);
            if (IsJson(second))
            {
                throw new InvalidOperationException("接口返回的仍是 JSON 而不是图片，请检查字段路径是否正确。");
            }

            imageBytes = second.Body;
            first = second;
        }
        else if (source.Kind == SourceKind.Auto && IsPlainTextUrl(first))
        {
            imageUrl = ExtractPlainTextUrl(Encoding.UTF8.GetString(first.Body));
            if (imageUrl is null)
            {
                throw new InvalidOperationException("接口返回的文本不是单个图片链接，请检查接口或改用 JSON 字段路径。");
            }

            FetchResult second = await FetchAsync(imageUrl, source.Headers, ct, trace).ConfigureAwait(false);
            imageBytes = second.Body;
            first = second;
        }

        return SaveToLibrary(imageBytes, first.FinalUrl, targetDir, encode, trace);
    }

    public static async Task<FetchResult> FetchAsync(string url, string? headersText, CancellationToken ct, List<string>? trace = null)
    {
        Uri current = ParseUri(url);

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            ApplyHeaders(request, headersText);

            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                {
                    throw new HttpRequestException($"接口跳转到了不支持的地址：{next}");
                }

                trace?.Add($"{(int)response.StatusCode} 跳转 → {next}");
                current = next;
                continue;
            }

            byte[] body = await ReadBodyLimitedAsync(response.Content, ct).ConfigureAwait(false);
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? current.ToString();

            trace?.Add($"{(int)response.StatusCode} {contentType} {FormatSize(body.LongLength)}  {finalUrl}");

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"接口返回 {(int)response.StatusCode}（{finalUrl}）");
            }

            return new FetchResult(body, contentType, finalUrl, (int)response.StatusCode);
        }

        throw new HttpRequestException($"跳转次数超过 {MaxRedirects} 次（起始地址 {url}）");
    }

    private static Uri ParseUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"不是合法的 http(s) 地址：{url}");
        }

        return uri;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    private static void ApplyHeaders(HttpRequestMessage request, string? headersText)
    {
        if (string.IsNullOrWhiteSpace(headersText))
        {
            return;
        }

        foreach (string rawLine in headersText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int separator = line.IndexOfAny(new[] { ':', '=' });
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            try
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
            catch
            {
                // Ignore header lines the client reserves (Content-Type and friends live on the body).
            }
        }
    }

    public static bool IsJson(FetchResult result) =>
        result.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || (result.Body.Length > 0 && (result.Body[0] == (byte)'{' || result.Body[0] == (byte)'['));

    private static bool IsPlainTextUrl(FetchResult result) =>
        result.ContentType.Contains("text", StringComparison.OrdinalIgnoreCase)
        && Encoding.UTF8.GetString(result.Body, 0, Math.Min(result.Body.Length, 4)).TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractPlainTextUrl(string text)
    {
        string candidate = text.Trim();
        return Uri.TryCreate(candidate, UriKind.Absolute, out _) ? candidate : null;
    }

    public static string? ExtractImageUrl(string json, string fieldPath)
    {
        if (!string.IsNullOrWhiteSpace(fieldPath))
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement cursor = doc.RootElement;
            foreach (string segment in fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (cursor.ValueKind == JsonValueKind.Array && int.TryParse(segment, out int index)
                    && index >= 0 && index < cursor.GetArrayLength())
                {
                    cursor = cursor[index];
                    continue;
                }

                if (cursor.ValueKind != JsonValueKind.Object || !cursor.TryGetProperty(segment, out JsonElement next))
                {
                    throw new InvalidOperationException($"JSON 里找不到字段路径 “{fieldPath}”。");
                }

                cursor = next;
            }

            string value = cursor.ValueKind == JsonValueKind.String ? cursor.GetString() ?? string.Empty : cursor.ToString();
            return NormalizeUrl(value);
        }

        using JsonDocument auto = JsonDocument.Parse(json);
        string? best = WalkForImageUrl(auto.RootElement, 0);
        return best is null ? null : NormalizeUrl(best);
    }

    private static string? WalkForImageUrl(JsonElement element, int depth)
    {
        if (depth > 5)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return LooksLikeImageUrl(element.GetString());
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    string? found = WalkForImageUrl(child, depth + 1);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValueKind.Object:
                List<string> preferred = new();
                List<string> fallback = new();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? value = LooksLikeImageUrl(property.Value.GetString());
                    if (value is null)
                    {
                        continue;
                    }

                    if (property.Name.Contains("url", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("img", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("pic", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("image", StringComparison.OrdinalIgnoreCase))
                    {
                        preferred.Add(value);
                    }
                    else
                    {
                        fallback.Add(value);
                    }
                }

                if (preferred.Count > 0)
                {
                    return preferred[0];
                }

                if (fallback.Count > 0)
                {
                    return fallback[0];
                }

                foreach (JsonElement child in element.EnumerateObject().Select(p => p.Value))
                {
                    string? found = WalkForImageUrl(child, depth + 1);
                    if (found is not null)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static string? LooksLikeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string path = Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed) ? parsed.AbsolutePath : trimmed;
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "jpg" or "jpeg" or "png" or "webp" or "bmp" or "gif" or "avif" or "heic" => trimmed,
            _ => null,
        };
    }

    private static string? NormalizeUrl(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("//"))
        {
            return "https:" + trimmed;
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    public static AcquiredImage SaveToLibrary(byte[] body, string sourceUrl, string libraryDir, EncodePreference encode, List<string> trace)
    {
        Directory.CreateDirectory(libraryDir);
        string sniffed = SniffFormat(body);

        SKBitmap? bitmap = null;
        try
        {
            bitmap = SKBitmap.Decode(body);
        }
        catch
        {
            bitmap = null;
        }

        if (bitmap is null)
        {
            throw new InvalidOperationException($"无法解码图片内容（识别为 {sniffed}，{body.Length} 字节）。该接口可能返回了 HTML 而非图片。");
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        bool hasAlpha = bitmap.Info.AlphaType != SKAlphaType.Opaque && sniffed is not ("jpeg" or "jpg" or "bmp");
        string extension = ChooseExtension(encode, hasAlpha, sniffed);

        string fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.{extension}";
        string target = Path.Combine(libraryDir, fileName);

        try
        {
            bool sameFormat = extension == sniffed || (extension == "jpg" && sniffed == "jpeg");
            if (sameFormat)
            {
                File.WriteAllBytes(target, body);
            }
            else
            {
                using SKImage image = SKImage.FromBitmap(bitmap);
                SKEncodedImageFormat format = extension == "png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
                using SKData data = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? 92 : 100)
                    ?? throw new InvalidOperationException("图片重新编码失败。");
                using FileStream stream = File.Create(target);
                data.SaveTo(stream);
            }
        }
        finally
        {
            // 写盘/转码失败时也要释放全尺寸位图，定时任务反复失败不能把原生内存堆起来。
            bitmap.Dispose();
        }

        return new AcquiredImage(target, extension, width, height, new FileInfo(target).Length, sourceUrl, trace);
    }

    private static string ChooseExtension(EncodePreference encode, bool hasAlpha, string sniffed) => encode switch
    {
        EncodePreference.Png => "png",
        EncodePreference.Jpeg => "jpg",
        _ => hasAlpha ? "png" : sniffed switch
        {
            "jpeg" => "jpg",
            "jpg" or "png" => sniffed,
            _ => "jpg",
        },
    };

    public static string SniffFormat(byte[] body)
    {
        if (body.Length < 12)
        {
            return "unknown";
        }

        if (body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF)
        {
            return "jpg";
        }

        if (body[0] == 0x89 && body[1] == 0x50 && body[2] == 0x4E && body[3] == 0x47)
        {
            return "png";
        }

        if (body[0] == 'R' && body[1] == 'I' && body[2] == 'F' && body[3] == 'F' && body[8] == 'W' && body[9] == 'E' && body[10] == 'B' && body[11] == 'P')
        {
            return "webp";
        }

        if (body[0] == 'B' && body[1] == 'M')
        {
            return "bmp";
        }

        if (body[0] == 'G' && body[1] == 'I' && body[2] == 'F')
        {
            return "gif";
        }

        if ((body[0] == 0x49 && body[1] == 0x49) || (body[0] == 0x4D && body[1] == 0x4D))
        {
            return "tiff";
        }

        return "unknown";
    }

    public sealed record FetchResult(byte[] Body, string ContentType, string FinalUrl, int StatusCode);
}
