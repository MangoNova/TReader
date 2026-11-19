using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TReader.Models;

namespace TReader.Services;

/// <summary>
/// EPUB 解析器：读取 OPF/Spine 并把章节 HTML 转换为纯文本。
/// </summary>
internal static class EpubParser
{
    private static readonly Regex BlockTagPattern = new(
        @"</?(?:br|p|div|h[1-6]|li|tr|blockquote|section|article|header|footer|nav|hr)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);

    public static Task<Novel> ParseAsync(string filePath)
    {
        return Task.Run(() =>
        {
            using var stream = File.OpenRead(filePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return Parse(archive, filePath);
        });
    }

    private static Novel Parse(ZipArchive archive, string filePath)
    {
        try
        {
            var entries = CreateEntryMap(archive);
            var containerXml = ReadRequiredEntry(entries, "META-INF/container.xml", "EPUB 缺少 META-INF/container.xml");
            var opfPath = ParseContainer(containerXml);
            var opfXml = ReadRequiredEntry(entries, opfPath, $"EPUB 缺少 OPF 文件：{opfPath}");
            var (bookTitle, documents) = ParseOpf(opfXml, opfPath, entries, filePath);

            if (documents.Count == 0)
            {
                throw new InvalidDataException("EPUB 未找到可读取的章节正文。");
            }

            var lines = new List<string>();
            var chapters = new List<Chapter>();

            foreach (var document in documents)
            {
                AppendDocument(lines, chapters, document.Title, HtmlToLines(document.Html));
            }

            return new Novel
            {
                Title = bookTitle,
                Lines = lines,
                Content = string.Join("\n", lines),
                Chapters = chapters.Count > 0
                    ? chapters
                    : new List<Chapter>
                    {
                        new() { Title = bookTitle, StartLine = 0, Index = 0 }
                    }
            };
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("EPUB 解析失败：文件格式无效。", ex);
        }
    }

    private static Dictionary<string, ZipArchiveEntry> CreateEntryMap(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var path = NormalizePath(entry.FullName.Replace('\\', '/'));
            if (!entries.ContainsKey(path))
            {
                entries[path] = entry;
            }
        }

        return entries;
    }

    private static string ReadRequiredEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path,
        string errorMessage)
    {
        if (!TryGetEntry(entries, path, out var entry))
        {
            throw new InvalidDataException(errorMessage);
        }

        return ReadEntryText(entry);
    }

    private static bool TryGetEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path,
        out ZipArchiveEntry entry)
    {
        if (entries.TryGetValue(NormalizePath(path), out var foundEntry))
        {
            entry = foundEntry;
            return true;
        }

        entry = null!;
        return false;
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ParseContainer(string containerXml)
    {
        var document = XDocument.Parse(containerXml);
        var rootfilePath = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")
            ?.Attribute("full-path")
            ?.Value;

        if (string.IsNullOrWhiteSpace(rootfilePath))
        {
            throw new InvalidDataException("EPUB container.xml 中未找到 rootfile。");
        }

        return NormalizePath(Uri.UnescapeDataString(rootfilePath.Trim()).Replace('\\', '/'));
    }

    private static (string Title, List<EpubDocument> Documents) ParseOpf(
        string opfXml,
        string opfPath,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string filePath)
    {
        var document = XDocument.Parse(opfXml);
        var opfDirectory = GetDirectory(opfPath);

        var metadataTitle = document.Descendants()
            .Where(element => element.Name.LocalName == "title")
            .Select(element => element.Value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var bookTitle = string.IsNullOrWhiteSpace(metadataTitle)
            ? Path.GetFileNameWithoutExtension(filePath)
            : metadataTitle;

        var manifest = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "item"))
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            manifest[id] = new ManifestItem(
                href,
                item.Attribute("media-type")?.Value,
                item.Attribute("properties")?.Value);
        }

        var spineIds = document.Descendants()
            .Where(element => element.Name.LocalName == "itemref")
            .Select(element => element.Attribute("idref")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        var orderedItems = new List<ManifestItem>();
        foreach (var id in spineIds)
        {
            if (manifest.TryGetValue(id, out var item) && IsHtmlItem(item))
            {
                orderedItems.Add(item);
            }
        }

        if (orderedItems.Count == 0)
        {
            orderedItems = manifest.Values.Where(IsHtmlItem).ToList();
        }

        var documents = new List<EpubDocument>();
        foreach (var item in orderedItems)
        {
            var entryPath = ResolvePath(opfDirectory, item.Href);
            if (!TryGetEntry(entries, entryPath, out var entry))
            {
                continue;
            }

            var html = ReadEntryText(entry);
            var fallbackTitle = $"第{documents.Count + 1}章";
            documents.Add(new EpubDocument(ExtractDocumentTitle(html, fallbackTitle), html));
        }

        return (bookTitle, documents);
    }

    private static bool IsHtmlItem(ManifestItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.MediaType) &&
            item.MediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(item.Href.Split('#')[0]).ToLowerInvariant();
        return extension is ".html" or ".xhtml" or ".htm";
    }

    private static string ExtractDocumentTitle(string html, string fallback)
    {
        var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
        {
            var title = CleanInlineText(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        match = Regex.Match(html, @"<h[1-3][^>]*>(.*?)</h[1-3]>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
        {
            var title = CleanInlineText(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        return fallback;
    }

    private static string CleanInlineText(string value)
    {
        var text = TagPattern.Replace(value, string.Empty);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static List<string> HtmlToLines(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new List<string>();
        }

        var text = Regex.Replace(html, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(
            text,
            @"<(script|style)\b[^>]*>.*?</\1>",
            " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(
            text,
            @"<head\b[^>]*>.*?</head>",
            " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = BlockTagPattern.Replace(text, "\n");
        text = TagPattern.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');

        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = Regex.Replace(rawLine, @"[ \t]+", " ").Trim();
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0))
            {
                continue;
            }

            lines.Add(line);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static void AppendDocument(
        List<string> lines,
        List<Chapter> chapters,
        string documentTitle,
        IReadOnlyList<string> documentLines)
    {
        if (documentLines.Count == 0)
        {
            return;
        }

        if (lines.Count > 0 && lines[^1].Length > 0)
        {
            lines.Add(string.Empty);
        }

        var headingOffsets = new List<int>();
        for (var i = 0; i < documentLines.Count; i++)
        {
            if (NovelParser.ChapterPattern.IsMatch(documentLines[i]))
            {
                headingOffsets.Add(i);
            }
        }

        if (headingOffsets.Count == 0 || headingOffsets[0] > 0)
        {
            chapters.Add(new Chapter
            {
                Title = documentTitle,
                StartLine = lines.Count,
                Index = chapters.Count
            });
        }

        if (headingOffsets.Count == 0)
        {
            lines.AddRange(documentLines);
            return;
        }

        var cursor = 0;
        foreach (var offset in headingOffsets)
        {
            for (; cursor < offset; cursor++)
            {
                lines.Add(documentLines[cursor]);
            }

            chapters.Add(new Chapter
            {
                Title = documentLines[offset],
                StartLine = lines.Count,
                Index = chapters.Count
            });

            lines.Add(documentLines[cursor]);
            cursor++;
        }

        for (; cursor < documentLines.Count; cursor++)
        {
            lines.Add(documentLines[cursor]);
        }
    }

    private static string ResolvePath(string baseDirectory, string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return string.Empty;
        }

        var path = Uri.UnescapeDataString(href.Trim()).Replace('\\', '/');
        var fragmentIndex = path.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            path = path[..fragmentIndex];
        }

        if (path.StartsWith('/'))
        {
            return NormalizePath(path);
        }

        var combined = string.IsNullOrEmpty(baseDirectory) ? path : $"{baseDirectory.TrimEnd('/')}/{path}";
        return NormalizePath(combined);
    }

    private static string NormalizePath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else
            {
                segments.Add(segment);
            }
        }

        return string.Join("/", segments);
    }

    private static string GetDirectory(string path)
    {
        var index = path.Replace('\\', '/').LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private sealed record ManifestItem(string Href, string? MediaType, string? Properties);

    private sealed record EpubDocument(string Title, string Html);
}
