using System.Net;
using System.Text.RegularExpressions;

namespace Aegis.Infrastructure.Chat;

internal static class ResearchFaviconHtml
{
    private const int MaximumDeclarations = 40;
    private const RegexOptions Parsing = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Singleline | RegexOptions.NonBacktracking;
    private static readonly Regex Tags = new(
        """<!--.*?(?:-->|\z)|<script(?:\s[^>]*?)?>.*?(?:</script\s*>|\z)|<style(?:\s[^>]*?)?>.*?(?:</style\s*>|\z)|<template(?:\s[^>]*?)?>.*?(?:</template\s*>|\z)|<(?:link|base)\s(?:[^<>"']|"[^"]*"|'[^']*')*>|</head\s*>|<body(?:\s|>)""",
        Parsing, TimeSpan.FromMilliseconds(150));
    private static readonly Regex Attributes = new(
        """\s+([a-z0-9_:-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?""",
        Parsing, TimeSpan.FromMilliseconds(100));
    private static readonly char[] HtmlWhitespace = [' ', '\t', '\n', '\f', '\r'];

    internal sealed record Candidates(IReadOnlyList<Uri> Icons, IReadOnlyList<Uri> TouchIcons);
    private sealed record Declaration(string Href, bool Touch, int Score, int Order);

    internal static Candidates Read(Uri document, string html, Func<Uri, bool> allowed)
    {
        Uri? baseUrl = null;
        var found = new List<Declaration>();
        var order = 0;
        foreach (Match tag in Tags.Matches(html))
        {
            if (tag.Value.StartsWith("</head", StringComparison.OrdinalIgnoreCase) ||
                tag.Value.StartsWith("<body", StringComparison.OrdinalIgnoreCase)) break;
            var isBase = tag.Value.StartsWith("<base", StringComparison.OrdinalIgnoreCase);
            if (!isBase && !tag.Value.StartsWith("<link", StringComparison.OrdinalIgnoreCase)) continue;

            var attrs = ParseAttributes(tag.Value);
            if (!attrs.TryGetValue("href", out var href) || href.Length > 2048) continue;
            if (isBase)
            {
                if (baseUrl is null && ResearchFaviconNetwork.ResolveLink(document, href) is { } candidate && allowed(candidate))
                    baseUrl = candidate;
                continue;
            }
            if (found.Count == MaximumDeclarations || !attrs.TryGetValue("rel", out var rel)) continue;
            var relations = rel.Split(HtmlWhitespace, StringSplitOptions.RemoveEmptyEntries);
            var icon = relations.Contains("icon", StringComparer.OrdinalIgnoreCase);
            var touch = relations.Contains("apple-touch-icon", StringComparer.OrdinalIgnoreCase) ||
                relations.Contains("apple-touch-icon-precomposed", StringComparer.OrdinalIgnoreCase);
            if ((!icon && !touch) || !IsSupportedType(attrs.GetValueOrDefault("type"))) continue;
            var media = attrs.GetValueOrDefault("media")?.Trim().ToLowerInvariant();
            if (media is "print" or "speech") continue;
            var score = IconSizeScore(attrs.GetValueOrDefault("sizes")) + (media is null or "" or "all" or "screen" ? 0 : 1024);
            found.Add(new(href, !icon && touch, score, order++));
        }

        var source = baseUrl ?? document;
        IReadOnlyList<Uri> Resolve(bool touch) => found.Where(item => item.Touch == touch)
            .OrderBy(item => item.Score).ThenByDescending(item => item.Order)
            .Select(item => ResearchFaviconNetwork.ResolveLink(source, item.Href))
            .OfType<Uri>().Where(uri => allowed(uri) && !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .Distinct().Take(touch ? 1 : 4).ToArray();
        return new(Resolve(false), Resolve(true));
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Attributes.Matches(tag))
        {
            var groups = attribute.Groups;
            var value = groups[2].Success ? groups[2].Value : groups[3].Success ? groups[3].Value : groups[4].Value;
            attributes.TryAdd(groups[1].Value, WebUtility.HtmlDecode(value));
        }
        return attributes;
    }

    private static bool IsSupportedType(string? value) => value?.Split(';', 2)[0].Trim().ToLowerInvariant() is
        null or "" or "image/png" or "image/x-icon" or "image/vnd.microsoft.icon" or
        "image/gif" or "image/webp" or "image/jpeg";

    private static int IconSizeScore(string? sizes)
    {
        if (sizes is null) return 32;
        var best = 32;
        foreach (var size in sizes.Split(HtmlWhitespace, StringSplitOptions.RemoveEmptyEntries))
        {
            if (size.Equals("any", StringComparison.OrdinalIgnoreCase)) return 0;
            var x = size.IndexOfAny(['x', 'X']);
            if (x > 0 && int.TryParse(size.AsSpan(0, x), out var width) && int.TryParse(size.AsSpan(x + 1), out var height) &&
                width is > 1 and <= 512 && height is > 1 and <= 512)
                best = Math.Min(best, Math.Abs(32 - Math.Min(width, height)));
        }
        return best;
    }
}
