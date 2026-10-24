using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// How much OPT each Top 100 address has received from the Distro Wallet (node and staking payouts), counting the
// early MultiSend payouts from before the v2.0.0 upgrade too. Totals are kept in odata/distro-received.json; each
// refresh reads only blocks newer than the last scan. Runs in the background so the Top 100 page never waits.
public static partial class WebDashboard
{
    sealed class DistroReceivedEntry
    {
        public decimal Opt { get; set; }
        public int Payouts { get; set; }
        public long ScannedToHeight { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    static readonly object DistroReceivedLock = new();
    static Dictionary<string, DistroReceivedEntry>? _distroReceived;
    static Task? _distroReceivedJob;
    static (int Done, int Total, string? Error) _distroReceivedProgress;
    static readonly TimeSpan DistroReceivedMaxAge = TimeSpan.FromHours(6);

    static string DistroReceivedPath => Path.Combine(Environment.CurrentDirectory, "odata", "distro-received.json");

    static Dictionary<string, DistroReceivedEntry> LoadDistroReceived()
    {
        lock (DistroReceivedLock)
        {
            if (_distroReceived is not null) return _distroReceived;
            try
            {
                _distroReceived = File.Exists(DistroReceivedPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, DistroReceivedEntry>>(File.ReadAllText(DistroReceivedPath)) ?? new()
                    : new();
            }
            catch (JsonException) { _distroReceived = new(); }
            return _distroReceived;
        }
    }

    static void SaveDistroReceived()
    {
        lock (DistroReceivedLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DistroReceivedPath)!);
            var tmp = DistroReceivedPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_distroReceived, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, DistroReceivedPath, overwrite: true);
        }
    }

    // Starts a background refresh of the addresses whose totals are missing or older than DistroReceivedMaxAge.
    static void EnsureDistroReceived(IReadOnlyList<string> addresses)
    {
        var book = LoadDistroReceived();
        List<string> stale;
        lock (DistroReceivedLock)
        {
            if (_distroReceivedJob is { IsCompleted: false }) return;
            stale = addresses.Where(a => !book.TryGetValue(a, out var e) || DateTime.UtcNow - e.UpdatedUtc > DistroReceivedMaxAge).ToList();
            if (stale.Count == 0) return;
            _distroReceivedProgress = (0, stale.Count, null);
            _distroReceivedJob = Task.Run(() => RefreshDistroReceivedAsync(stale));
        }
    }

    static async Task RefreshDistroReceivedAsync(List<string> addresses)
    {
        try
        {
            using var rpc = CreateRpcContext();
            var book = LoadDistroReceived();
            var done = 0;
            foreach (var address in addresses)
            {
                // The chain height before the scan: everything at or below it is counted once the scan finishes.
                var head = await GetLatestHeightAsync(rpc.Http, CancellationToken.None);
                DistroReceivedEntry entry;
                lock (DistroReceivedLock) entry = book.TryGetValue(address, out var e) ? e : new DistroReceivedEntry();
                var from = entry.ScannedToHeight;
                var query = from > 0 ? $"transfer.recipient='{address}' AND tx.height>{from}" : $"transfer.recipient='{address}'";
                var units = BigInteger.Zero;
                var payouts = 0;
                // Sender is filtered locally (adding it to the query is far slower); GetTxTransfers also reads the
                // MultiSend inputs, whose transfer events don't name a sender.
                await SearchTxsAsync(rpc.Http, query, (tx, hash, height) =>
                {
                    if (head > 0 && height > head) return;
                    var got = BigInteger.Zero;
                    foreach (var t in GetTxTransfers(tx, rpc.DenomWantedUpper))
                        if (t.Sender.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase) && t.Recipient.Equals(address, StringComparison.OrdinalIgnoreCase))
                            got += t.AmountUnits;
                    if (got > 0) { units += got; payouts++; }
                }, CancellationToken.None);
                lock (DistroReceivedLock)
                {
                    book[address] = new DistroReceivedEntry
                    {
                        Opt = entry.Opt + ToDecimalOpt(units, rpc.ScaleInt),
                        Payouts = entry.Payouts + payouts,
                        ScannedToHeight = head > 0 ? head : entry.ScannedToHeight,
                        UpdatedUtc = DateTime.UtcNow,
                    };
                }
                SaveDistroReceived();
                _distroReceivedProgress = (++done, addresses.Count, null);
            }
        }
        catch (Exception ex)
        {
            _distroReceivedProgress = (_distroReceivedProgress.Done, _distroReceivedProgress.Total, ex.Message);
        }
    }

    static async Task<long> GetLatestHeightAsync(HttpClient rpcHttp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await rpcHttp.GetStringAsync("status", ct));
            return long.Parse(doc.RootElement.GetProperty("result").GetProperty("sync_info").GetProperty("latest_block_height").GetString()!, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or KeyNotFoundException) { return 0; }
    }

    static void MapDistroReceived(WebApplication app)
    {
        app.MapGet("/api/top100/distro", async (HttpContext ctx) =>
        {
            var wallets = await GetTop100WalletsAsync();
            var addresses = wallets.Select(w => w.Address).ToList();
            EnsureDistroReceived(addresses);
            var book = LoadDistroReceived();
            Dictionary<string, object> entries;
            lock (DistroReceivedLock)
                entries = addresses.Where(book.ContainsKey).ToDictionary(a => a, a => (object)new { opt = book[a].Opt, payouts = book[a].Payouts, scannedToHeight = book[a].ScannedToHeight, updatedUtc = book[a].UpdatedUtc });
            var p = _distroReceivedProgress;
            return Results.Json(new
            {
                source = DistributionSourceAddress,
                entries,
                running = _distroReceivedJob is { IsCompleted: false },
                done = p.Done, total = p.Total, error = p.Error,
            });
        });
    }
}
