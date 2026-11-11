using System.IO;
using System.Net.Http;
using System.Text.Json;
using MintWall.Models;

namespace MintWall.Services;

/// <summary>在线视频源：直链下载，或先解析 JSON 拿到视频地址再下载。</summary>
public static class OnlineVideoService
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(180) };
        // 部分视频 CDN 会拒绝没有 User-Agent 的请求（会表现为下载失败/空文件）。
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) MintWall/1.5 Safari/537.36");
        return client;
    }

    /// <summary>抓取一个源的视频到 targetDir；keep=true（自动保存）时不参与缓存清理。</summary>
    public static async Task<string> AcquireAsync(
        VideoSource source, string targetDir, bool keep, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            throw new InvalidOperationException("这个源还没有填写地址。");
        }

        bool explicitJson = source.Kind == SourceKind.Json || !string.IsNullOrWhiteSpace(source.JsonFieldPath);
        string mediaUrl = explicitJson
            ? await ResolveJsonUrlAsync(source, token)
            : await ResolveAutoUrlAsync(source, token);

        using HttpRequestMessage request = new(HttpMethod.Get, mediaUrl);
        ApplyHeaders(request, source.Headers);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(targetDir);
        string file = Path.Combine(
            targetDir,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]
            + GuessExtension(mediaUrl, response.Content.Headers.ContentType?.MediaType));

        await using (Stream input = await response.Content.ReadAsStreamAsync(token))
        await using (FileStream output = File.Create(file))
        {
            await input.CopyToAsync(output, token);
        }

        if (new FileInfo(file).Length < 64 * 1024)
        {
            File.Delete(file);
            throw new InvalidOperationException("下载到的文件太小，可能不是视频；确认地址指向的是视频文件。");
        }

        if (!keep)
        {
            PruneCache(targetDir, file);
        }

        return file;
    }

    /// <summary>自动判断：响应是视频就直接用，是 JSON 就从中找视频地址。</summary>
    private static async Task<string> ResolveAutoUrlAsync(VideoSource source, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, source.Url);
        ApplyHeaders(request, source.Headers);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (IsJson(mediaType))
        {
            string body = await response.Content.ReadAsStringAsync(token);
            return ExtractVideoUrl(body, source.JsonFieldPath);
        }

        if (IsHtml(mediaType))
        {
            throw new InvalidOperationException("这个地址返回的是网页，不是视频；请填视频直链或返回视频地址的 JSON 接口。");
        }

        // 视频直链（或 octet-stream/无类型）
        return source.Url;
    }

    private static async Task<string> ResolveJsonUrlAsync(VideoSource source, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, source.Url);
        ApplyHeaders(request, source.Headers);
        using HttpResponseMessage response = await Http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(token);
        return ExtractVideoUrl(json, source.JsonFieldPath);
    }

    private static string ExtractVideoUrl(string json, string fieldPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("这个地址返回的内容不是有效的 JSON。");
        }

        using (document)
        {
            string? url = ReadField(document.RootElement, fieldPath) ?? FindVideoUrl(document.RootElement);
            if (string.IsNullOrWhiteSpace(url))
            {
                string hint = json.Contains("imgurl", StringComparison.OrdinalIgnoreCase)
                    ? "（响应里有 imgurl，这是图片接口，不是视频源）"
                    : string.Empty;
                throw new InvalidOperationException($"JSON 里没有找到视频地址{hint}；请填视频直链或指定 JSON 字段路径。");
            }

            return url;
        }
    }

    private static bool IsJson(string? mediaType) =>
        mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool IsHtml(string? mediaType) =>
        mediaType is not null
        && (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase));

    private static string? ReadField(JsonElement element, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        JsonElement current = element;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out int index))
            {
                if (index < 0 || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? FindVideoUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string value = element.GetString() ?? string.Empty;
            return IsVideoLike(value) ? value : null;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                string? found = FindVideoUrl(child);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string? found = FindVideoUrl(property.Value);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool IsVideoLike(string value) =>
        value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        && VideoLibraryStore.Extensions.Any(ext => value.Contains(ext, StringComparison.OrdinalIgnoreCase));

    private static string GuessExtension(string url, string? mediaType)
    {
        string path = url.Split('?', '#')[0];
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (VideoLibraryStore.Extensions.Contains(ext))
        {
            return ext;
        }

        return mediaType switch
        {
            "video/x-ms-wmv" => ".wmv",
            "video/quicktime" => ".mov",
            _ => ".mp4",
        };
    }

    private static void ApplyHeaders(HttpRequestMessage request, string headers)
    {
        foreach (string line in headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Length > 0 && value.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    /// <summary>缓存只留最近两个文件，避免在线视频越攒越多。</summary>
    private static void PruneCache(string dir, string keep)
    {
        try
        {
            foreach (FileInfo file in new DirectoryInfo(dir).GetFiles()
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(2))
            {
                if (!string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase))
                {
                    file.Delete();
                }
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
