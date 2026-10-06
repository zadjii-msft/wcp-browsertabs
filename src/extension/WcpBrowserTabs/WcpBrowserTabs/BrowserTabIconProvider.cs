// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions.Toolkit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage.Streams;

namespace WcpBrowserTabs;

/// <summary>
/// Resolves an icon for each browser tab. UI Automation does not expose tab favicons,
/// so favicons are looked up by page title in snapshots of the browser profile
/// history and favicon databases. The browser executable icon is used as a fallback.
/// </summary>
internal sealed class BrowserTabIconProvider
{
    private static readonly TimeSpan SnapshotRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SourceRescanInterval = TimeSpan.FromMinutes(5);
    private const int MaxCachedTitles = 2000;
    private const int MaxTitleCandidates = 5;
    private const int MaxQueryParameters = 500;

    private static readonly (string RelativePath, string Browser)[] ChromiumUserDataRoots =
    [
        (@"Google\Chrome\User Data", "Chrome"),
        (@"Google\Chrome Beta\User Data", "Chrome"),
        (@"Google\Chrome Dev\User Data", "Chrome"),
        (@"Google\Chrome SxS\User Data", "Chrome"),
        (@"Microsoft\Edge\User Data", "Edge"),
        (@"Microsoft\Edge Beta\User Data", "Edge"),
        (@"Microsoft\Edge Dev\User Data", "Edge"),
        (@"Microsoft\Edge SxS\User Data", "Edge"),
    ];

    private readonly object gate = new();
    private readonly Dictionary<string, CachedTitleIcon> titleIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IconInfo> iconsByContentHash = new(StringComparer.Ordinal);
    private readonly Dictionary<int, IconInfo?> executableIcons = [];
    private List<FaviconSource> sources = [];
    private DateTimeOffset lastSourceScanUtc = DateTimeOffset.MinValue;

    public static BrowserTabIconProvider Shared { get; } = new();

    public IReadOnlyDictionary<BrowserTab, IconInfo> GetIcons(IReadOnlyList<BrowserTab> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            ResolveFavicons(tabs, now);

            var result = new Dictionary<BrowserTab, IconInfo>();
            foreach (var tab in tabs)
            {
                var icon = titleIcons.TryGetValue(GetTitleKey(tab), out var cached) ? cached.Icon : null;
                icon ??= GetExecutableIcon(tab.ProcessId);
                if (icon is not null)
                {
                    result[tab] = icon;
                }
            }

            return result;
        }
    }

    internal static IReadOnlyList<string> GetTitleCandidates(string? title)
    {
        var candidates = new List<string>();
        var current = title?.Trim() ?? string.Empty;

        // Browsers append status text (for example " - Memory usage - 120 MB") to the
        // accessible tab name, so also try the title with trailing " - " segments removed.
        while (current.Length > 0 && candidates.Count < MaxTitleCandidates)
        {
            candidates.Add(current);

            var separator = current.LastIndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                break;
            }

            current = current[..separator].TrimEnd();
        }

        return candidates;
    }

    internal static bool IsSupportedRasterImage(ReadOnlySpan<byte> data)
    {
        return data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])
            || data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00])
            || data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
            || data.StartsWith("GIF8"u8)
            || data.StartsWith("BM"u8)
            || (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8));
    }

    private static string GetTitleKey(BrowserTab tab) => $"{tab.Browser}\u001f{tab.TabTitle}";

    private void ResolveFavicons(IReadOnlyList<BrowserTab> tabs, DateTimeOffset now)
    {
        var pendingByBrowser = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            if (titleIcons.TryGetValue(GetTitleKey(tab), out var cached)
                && (cached.Icon is not null || now - cached.CheckedUtc < SnapshotRefreshInterval))
            {
                continue;
            }

            if (!pendingByBrowser.TryGetValue(tab.Browser, out var titles))
            {
                titles = new HashSet<string>(StringComparer.Ordinal);
                pendingByBrowser[tab.Browser] = titles;
            }

            titles.Add(tab.TabTitle);
        }

        if (pendingByBrowser.Count == 0)
        {
            return;
        }

        if (titleIcons.Count > MaxCachedTitles)
        {
            titleIcons.Clear();
        }

        RescanSourcesIfNeeded(now);

        foreach (var (browser, titles) in pendingByBrowser)
        {
            var candidatesByTitle = titles.ToDictionary(title => title, GetTitleCandidates, StringComparer.Ordinal);
            var allCandidates = candidatesByTitle.Values.SelectMany(candidates => candidates).Distinct(StringComparer.Ordinal).ToArray();
            var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            foreach (var source in sources.Where(source => string.Equals(source.Browser, browser, StringComparison.Ordinal)))
            {
                try
                {
                    source.RefreshSnapshotIfNeeded(now);
                    source.FindFavicons(allCandidates, found);
                }
                catch
                {
                    // A locked or unreadable profile must not prevent icons from other profiles.
                }
            }

            foreach (var (title, candidates) in candidatesByTitle)
            {
                IconInfo? icon = null;
                foreach (var candidate in candidates)
                {
                    if (found.TryGetValue(candidate, out var bytes))
                    {
                        icon = GetOrCreateIcon(bytes);
                        break;
                    }
                }

                titleIcons[$"{browser}\u001f{title}"] = new CachedTitleIcon(icon, now);
            }
        }
    }

    private void RescanSourcesIfNeeded(DateTimeOffset now)
    {
        if (now - lastSourceScanUtc < SourceRescanInterval)
        {
            return;
        }

        lastSourceScanUtc = now;
        var discovered = new List<FaviconSource>();

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var (relativePath, browser) in ChromiumUserDataRoots)
            {
                var userData = Path.Combine(localAppData, relativePath);
                if (!Directory.Exists(userData))
                {
                    continue;
                }

                foreach (var profile in Directory.EnumerateDirectories(userData))
                {
                    var history = Path.Combine(profile, "History");
                    var favicons = Path.Combine(profile, "Favicons");
                    if (File.Exists(history) && File.Exists(favicons))
                    {
                        discovered.Add(new FaviconSource(browser, isFirefox: false, history, favicons));
                    }
                }
            }
        }
        catch
        {
            // Ignore inaccessible profile roots.
        }

        try
        {
            var firefoxProfiles = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(firefoxProfiles))
            {
                foreach (var profile in Directory.EnumerateDirectories(firefoxProfiles))
                {
                    var places = Path.Combine(profile, "places.sqlite");
                    var favicons = Path.Combine(profile, "favicons.sqlite");
                    if (File.Exists(places) && File.Exists(favicons))
                    {
                        discovered.Add(new FaviconSource("Firefox", isFirefox: true, places, favicons));
                    }
                }
            }
        }
        catch
        {
            // Ignore inaccessible profile roots.
        }

        // Keep existing sources so their snapshot state is preserved.
        var existing = sources.ToDictionary(source => source.HistoryPath, StringComparer.OrdinalIgnoreCase);
        sources = discovered
            .Select(source => existing.TryGetValue(source.HistoryPath, out var current) ? current : source)
            .ToList();
    }

    private IconInfo GetOrCreateIcon(byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (iconsByContentHash.TryGetValue(hash, out var icon))
        {
            return icon;
        }

        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }

        var data = new IconData(RandomAccessStreamReference.CreateFromStream(stream));
        icon = new IconInfo(data, data);
        iconsByContentHash[hash] = icon;
        return icon;
    }

    private IconInfo? GetExecutableIcon(int processId)
    {
        if (executableIcons.TryGetValue(processId, out var icon))
        {
            return icon;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            icon = string.IsNullOrWhiteSpace(path) ? null : new IconInfo(path);
        }
        catch
        {
            icon = null;
        }

        executableIcons[processId] = icon;
        return icon;
    }

    private sealed record CachedTitleIcon(IconInfo? Icon, DateTimeOffset CheckedUtc);

    private sealed class FaviconSource
    {
        private const string ChromiumQuery = """
            SELECT u.title, b.image_data
            FROM urls u
            JOIN favicons.icon_mapping m ON m.page_url = u.url
            JOIN favicons.favicon_bitmaps b ON b.icon_id = m.icon_id
            WHERE u.title IN ({0}) AND length(b.image_data) > 0
            ORDER BY u.last_visit_time DESC, b.width DESC
            """;

        private const string FirefoxQuery = """
            SELECT p.title, i.data
            FROM moz_places p
            JOIN favicons.moz_pages_w_icons pw ON pw.page_url = p.url
            JOIN favicons.moz_icons_to_pages ip ON ip.page_id = pw.id
            JOIN favicons.moz_icons i ON i.id = ip.icon_id
            WHERE p.title IN ({0}) AND i.data IS NOT NULL
            ORDER BY p.last_visit_date DESC, i.width DESC
            """;

        private readonly bool isFirefox;
        private readonly string faviconsPath;
        private readonly string snapshotDirectory;
        private DateTimeOffset lastSnapshotUtc = DateTimeOffset.MinValue;
        private string? lastSourceStamp;

        public FaviconSource(string browser, bool isFirefox, string historyPath, string faviconsPath)
        {
            Browser = browser;
            HistoryPath = historyPath;
            this.isFirefox = isFirefox;
            this.faviconsPath = faviconsPath;

            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(historyPath.ToUpperInvariant())))[..16];
            snapshotDirectory = Path.Combine(Path.GetTempPath(), "WcpBrowserTabs", "favicons", id);
        }

        public string Browser { get; }

        public string HistoryPath { get; }

        private string HistorySnapshot => Path.Combine(snapshotDirectory, "history.db");

        private string FaviconsSnapshot => Path.Combine(snapshotDirectory, "favicons.db");

        public void RefreshSnapshotIfNeeded(DateTimeOffset now)
        {
            var hasSnapshot = File.Exists(HistorySnapshot) && File.Exists(FaviconsSnapshot);
            if (hasSnapshot && now - lastSnapshotUtc < SnapshotRefreshInterval)
            {
                return;
            }

            var stamp = GetSourceStamp();
            if (hasSnapshot && string.Equals(stamp, lastSourceStamp, StringComparison.Ordinal))
            {
                lastSnapshotUtc = now;
                return;
            }

            Directory.CreateDirectory(snapshotDirectory);
            CopyDatabase(HistoryPath, HistorySnapshot);
            CopyDatabase(faviconsPath, FaviconsSnapshot);
            lastSnapshotUtc = now;
            lastSourceStamp = stamp;
        }

        public void FindFavicons(IReadOnlyList<string> titles, Dictionary<string, byte[]> found)
        {
            var remaining = titles.Where(title => !found.ContainsKey(title)).ToArray();
            if (remaining.Length == 0)
            {
                return;
            }

            using var connection = WinSqliteConnection.Open(HistorySnapshot);
            connection.Execute("ATTACH DATABASE ?1 AS favicons", FaviconsSnapshot);

            foreach (var chunk in remaining.Chunk(MaxQueryParameters))
            {
                var placeholders = string.Join(",", Enumerable.Repeat("?", chunk.Length));
                var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, isFirefox ? FirefoxQuery : ChromiumQuery, placeholders);

                connection.Query(sql, chunk, row =>
                {
                    var title = row.GetString(0);
                    if (title is null || found.ContainsKey(title))
                    {
                        return;
                    }

                    var data = row.GetBlob(1);
                    if (data is not null && IsSupportedRasterImage(data))
                    {
                        found[title] = data;
                    }
                });
            }
        }

        private string GetSourceStamp()
        {
            var builder = new StringBuilder();
            foreach (var path in new[] { HistoryPath, HistoryPath + "-wal", faviconsPath, faviconsPath + "-wal" })
            {
                var info = new FileInfo(path);
                builder.Append(info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length};" : "-;");
            }

            return builder.ToString();
        }

        private static void CopyDatabase(string source, string destination)
        {
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                File.Delete(destination + suffix);
            }

            CopyFileShared(source, destination);

            // Recent changes may still live in the write-ahead log.
            if (File.Exists(source + "-wal"))
            {
                CopyFileShared(source + "-wal", destination + "-wal");
            }
        }

        private static void CopyFileShared(string source, string destination)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }
}
