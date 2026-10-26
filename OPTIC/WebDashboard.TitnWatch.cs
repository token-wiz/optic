// Chains > TITN: where THORWallet's unreleased TITN sits and when it moves, read from the chain.
//   - The TITN token contract (Base) has no unlock schedule; the whole supply was minted at launch and most of it went to
//     THORWallet's treasury, a 2-of-3 Safe multisig the token contract exempts from every transfer restriction.
//   - Unlocks the chain does enforce are the TitnVesting contracts the treasury funded: each releases its balance evenly
//     from a start time over a fixed duration (claimable by the vested accounts).
//   - CoinMarketCap's published allocations (team, treasury, investors …) are shown next to it as the announced plan.
// Treasury and vesting transfers come from Blockscout and are cached in odata/evm/titn-watch.json (refreshed at most every
// 10 minutes). Users who turn on alerts get a push and a bell item when the treasury sends, or vested accounts claim,
// at least their chosen amount (default 1,000,000 TITN).
using OPTIC.Services;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    const string TitnTreasurySafe = "0x68fbde17200f69d0d17e2a858c79c264ee7bcea9";
    const decimal TitnAlertDefault = 1_000_000m;
    static readonly TimeSpan TitnWatchMaxAge = TimeSpan.FromMinutes(10);
    static readonly SemaphoreSlim TitnWatchGate = new(1, 1);
    static string TitnWatchPath => Path.Combine(Environment.CurrentDirectory, "odata", "evm", "titn-watch.json");

    // One TITN transfer touching the treasury or a vesting contract. Seen = when OPTIC first saw it (alerts use this, so a
    // transfer the explorer indexes late still alerts once).
    sealed record TitnMove(DateTime Time, string Tx, int Log, string From, string To, decimal Amount, string FromName, string ToName, DateTime Seen);

    sealed class TitnWatchCache
    {
        public DateTime UpdatedUtc { get; set; }
        public List<TitnMove> Safe { get; set; } = new();
        public Dictionary<string, List<TitnMove>> Vesting { get; set; } = new();
        public Dictionary<string, JsonObject> Live { get; set; } = new();
        // The explorer's holder count (wallets with any TITN in them, so staked-only wallets aren't counted), one per day.
        public Dictionary<string, Dictionary<string, int>> HoldersDaily { get; set; } = new();
    }

    static TitnWatchCache? LoadTitnWatch()
    {
        try { return File.Exists(TitnWatchPath) ? JsonSerializer.Deserialize<TitnWatchCache>(File.ReadAllText(TitnWatchPath)) : null; }
        catch (JsonException) { return null; }
    }

    // Name Blockscout gives a contract: its own name, or for a proxy the implementation's.
    static string ExplorerName(JsonElement a)
    {
        var name = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        if (a.TryGetProperty("implementations", out var im) && im.ValueKind == JsonValueKind.Array && im.GetArrayLength() > 0
            && im[0].TryGetProperty("name", out var iname) && iname.ValueKind == JsonValueKind.String && (name is "" or "BeaconProxy" or "SafeProxy" || name.Contains("Proxy")))
            return iname.GetString()!;
        return name;
    }

    // TITN transfers of one address, newest first, stopping at the first one already cached.
    static async Task<List<TitnMove>> FetchTitnMovesAsync(string address, HashSet<string> known, bool firstScan, CancellationToken ct)
    {
        var basePath = $"{EvmExplorersAll["base"]}/addresses/{address}/token-transfers?type=ERC-20&token={TitnToken}";
        var list = new List<TitnMove>();
        var url = basePath;
        for (var page = 0; page < 100; page++)
        {
            using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync(url, ct));
            var stop = false;
            foreach (var x in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var tx = x.GetProperty("transaction_hash").GetString()!.ToLowerInvariant();
                var log = x.TryGetProperty("log_index", out var li) && li.ValueKind == JsonValueKind.Number ? li.GetInt32() : 0;
                if (known.Contains(tx + ":" + log)) { stop = true; continue; }
                var time = ParseUtc(x.GetProperty("timestamp").GetString()!);
                var amount = (decimal)(double.Parse(x.GetProperty("total").GetProperty("value").GetString()!, CultureInfo.InvariantCulture) / 1e18);
                var from = x.GetProperty("from"); var to = x.GetProperty("to");
                list.Add(new TitnMove(time, tx, log, from.GetProperty("hash").GetString()!.ToLowerInvariant(), to.GetProperty("hash").GetString()!.ToLowerInvariant(),
                    Math.Round(amount, 2), ExplorerName(from), ExplorerName(to), firstScan ? time : DateTime.UtcNow));
            }
            if (stop || !doc.RootElement.TryGetProperty("next_page_params", out var next) || next.ValueKind != JsonValueKind.Object) break;
            url = basePath + "&" + string.Join("&", next.EnumerateObject().Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())));
        }
        return list;
    }

    static async Task<BigInteger> TitnCallAsync(string chain, string to, string data, CancellationToken ct)
    {
        var hex = await EvmRpcHexAsync(chain, "eth_call", new JsonArray(new JsonObject { ["to"] = to, ["data"] = data }, "latest"), ct);
        return hex is null || hex.Length < 66 ? BigInteger.Zero : BigInteger.Parse("0" + hex[2..66], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
    static string Arg(string address) => address[2..].ToLowerInvariant().PadLeft(64, '0');

    // Brings the cache up to date (at most every 10 minutes unless forced).
    static async Task<TitnWatchCache> RefreshTitnWatchAsync(bool force, CancellationToken ct)
    {
        var cache = LoadTitnWatch();
        if (cache is not null && !force && DateTime.UtcNow - cache.UpdatedUtc < TitnWatchMaxAge) return cache;
        await TitnWatchGate.WaitAsync(ct);
        try
        {
            cache = LoadTitnWatch();
            if (cache is not null && !force && DateTime.UtcNow - cache.UpdatedUtc < TitnWatchMaxAge) return cache;
            var first = cache is null;
            cache ??= new TitnWatchCache();

            var known = cache.Safe.Select(m => m.Tx + ":" + m.Log).ToHashSet();
            cache.Safe.InsertRange(0, await FetchTitnMovesAsync(TitnTreasurySafe, known, first, ct));
            cache.Safe = cache.Safe.OrderByDescending(m => m.Time).ThenByDescending(m => m.Log).ToList();

            // Vesting contracts are found from the treasury's own transfers (Blockscout names them TitnVesting).
            foreach (var v in cache.Safe.Where(m => m.ToName == "TitnVesting").Select(m => m.To).Distinct())
            {
                var had = cache.Vesting.TryGetValue(v, out var vl);
                vl ??= new();
                var vk = vl.Select(m => m.Tx + ":" + m.Log).ToHashSet();
                vl.InsertRange(0, await FetchTitnMovesAsync(v, vk, !had, ct));
                cache.Vesting[v] = vl.OrderByDescending(m => m.Time).ThenByDescending(m => m.Log).ToList();
            }

            // Live balances and vesting terms.
            var live = new Dictionary<string, JsonObject>();
            decimal T(BigInteger b) => Scale(b, 18);
            live["supply"] = new JsonObject
            {
                ["base"] = T(await TitnCallAsync("base", TitnToken, "0x18160ddd", ct)),
                ["arbitrum"] = T(await TitnCallAsync("arbitrum", TitnToken, "0x18160ddd", ct)),
                ["treasury"] = T(await TitnCallAsync("base", TitnToken, "0x70a08231" + Arg(TitnTreasurySafe), ct)),
                ["staking"] = T(await TitnCallAsync("base", TitnToken, "0x70a08231" + Arg(TitnStakingContract), ct)),
                ["threshold"] = (int)await TitnCallAsync("base", TitnTreasurySafe, "0xe75235b8", ct),
            };
            foreach (var v in cache.Vesting.Keys)
                live["vest:" + v] = new JsonObject
                {
                    ["start"] = (long)await TitnCallAsync("base", v, "0x43afdbbd", ct),
                    ["duration"] = (long)await TitnCallAsync("base", v, "0x0289943d", ct),
                    ["vested"] = T(await TitnCallAsync("base", v, "0x016a0a4d", ct)),
                    ["balance"] = T(await TitnCallAsync("base", TitnToken, "0x70a08231" + Arg(v), ct)),
                };
            // CoinMarketCap's published allocations (best effort; the page still works without them).
            try
            {
                using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync("https://api.coinmarketcap.com/data-api/v3/token-unlock/allocations?cryptoId=36271", ct));
                var arr = new JsonArray();
                foreach (var a in doc.RootElement.GetProperty("data").GetProperty("tokenAllocations").EnumerateArray())
                    arr.Add(new JsonObject { ["name"] = a.GetProperty("allocationName").GetString(), ["totalPct"] = a.GetProperty("totalPercent").GetDecimal(), ["unlockedPct"] = a.GetProperty("unlockedPercent").GetDecimal(), ["progress"] = a.GetProperty("unlockProgress").GetDecimal() });
                if (arr.Count > 0) live["cmc"] = new JsonObject { ["fetchedUtc"] = DateTime.UtcNow, ["rows"] = arr };
            }
            catch (Exception) { if (cache.Live.TryGetValue("cmc", out var old)) live["cmc"] = old.DeepClone().AsObject(); }

            // The fee splitter's current recipients and percentages, and the USDC the two THORWallet wallets hold.
            try
            {
                var split = new JsonArray();
                for (var i = 0; i < 3; i++)
                {
                    var arg = i.ToString("x").PadLeft(64, '0');
                    var who = await TitnCallAsync("base", TitnFeeSplitter, "0xd1bc76a1" + arg, ct);
                    var pct = await TitnCallAsync("base", TitnFeeSplitter, "0xf8858386" + arg, ct);
                    split.Add(new JsonObject { ["to"] = "0x" + who.ToString("x").PadLeft(40, '0')[^40..], ["pct"] = (int)pct });
                }
                live["split"] = new JsonObject { ["rows"] = split };
            }
            catch (Exception) { if (cache.Live.TryGetValue("split", out var old)) live["split"] = old.DeepClone().AsObject(); }
            live["cash"] = new JsonObject
            {
                ["ops"] = Scale(await TitnCallAsync("base", BaseUsdc, "0x70a08231" + Arg(TitnFeeTreasury), ct), 6),
                ["safe"] = Scale(await TitnCallAsync("base", BaseUsdc, "0x70a08231" + Arg(TitnTreasurySafe), ct), 6),
            };
            // Holder counts as the explorers report them (best effort), kept once a day.
            try
            {
                var hd = new Dictionary<string, int>();
                foreach (var chain in new[] { "base", "arbitrum" })
                {
                    using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync($"{EvmExplorersAll[chain]}/tokens/{TitnToken}/counters", ct));
                    if (int.TryParse(doc.RootElement.GetProperty("token_holders_count").GetString(), out var n)) hd[chain] = n;
                }
                if (hd.Count == 2) cache.HoldersDaily[DateTime.UtcNow.ToString("yyyy-MM-dd")] = hd;
            }
            catch (Exception) { }
            cache.Live = live;
            cache.UpdatedUtc = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(TitnWatchPath)!);
            File.WriteAllText(TitnWatchPath, JsonSerializer.Serialize(cache));
            return cache;
        }
        finally { TitnWatchGate.Release(); }
    }

    // What a treasury transfer was: to the treasury's own Aerodrome liquidity, into vesting, to reward contracts, or to a wallet.
    static string TitnMoveKind(TitnMove m)
    {
        var other = m.From == TitnTreasurySafe ? m.To : m.From;
        var name = m.From == TitnTreasurySafe ? m.ToName : m.FromName;
        if (other == TitnTreasurySafe) return "self";
        if (m.To == TitnTreasurySafe && m.Amount >= 100_000_000m) return "launch"; // the treasury's share of the supply at launch
        if (name.StartsWith("Aerodrome", StringComparison.Ordinal)) return "liquidity";
        if (name == "TitnVesting") return "vesting";
        if (other == TitnStakingContract || name is "LiquidRewards" or "TitnDistributor" or "TitnStaking") return "rewards";
        return "wallet";
    }

    static readonly Dictionary<string, string> TitnKindText = new()
    {
        ["liquidity"] = "the treasury's own Aerodrome liquidity",
        ["vesting"] = "a vesting contract",
        ["rewards"] = "reward contracts",
        ["wallet"] = "a wallet",
    };

    // Transfers worth an alert: treasury sends (not to itself; liquidity only when asked) and vesting claims, at least min TITN.
    static IEnumerable<(TitnMove M, string Kind, string Text)> TitnAlertMoves(TitnWatchCache c, decimal min, bool liquidity)
    {
        foreach (var m in c.Safe.Where(m => m.From == TitnTreasurySafe && m.Amount >= min))
        {
            var k = TitnMoveKind(m);
            if (k is "self" or "launch" || (k == "liquidity" && !liquidity)) continue;
            yield return (m, k, $"Treasury sent {m.Amount:N0} TITN to {TitnKindText[k]} ({m.To[..6]}…{m.To[^4..]})");
        }
        foreach (var (v, list) in c.Vesting)
            foreach (var m in list.Where(m => m.From == v && m.Amount >= min))
                yield return (m, "claim", $"{m.Amount:N0} TITN claimed from vesting by {m.To[..6]}…{m.To[^4..]}");
    }

    static object BuildTitnWatch(TitnWatchCache c, decimal price)
    {
        var now = DateTime.UtcNow;
        var s = c.Live.GetValueOrDefault("supply") ?? new JsonObject();
        var total = Dec(s["base"]) + Dec(s["arbitrum"]);
        var vesting = c.Vesting.Select(kv =>
        {
            var l = c.Live.GetValueOrDefault("vest:" + kv.Key) ?? new JsonObject();
            var start = DateTimeOffset.FromUnixTimeSeconds((long)Dec(l["start"])).UtcDateTime;
            var dur = TimeSpan.FromSeconds((double)Dec(l["duration"]));
            var claimed = kv.Value.Where(m => m.From == kv.Key).Sum(m => m.Amount);
            var alloc = Dec(l["vested"]) + claimed;
            var f = dur.TotalSeconds <= 0 ? 0m : Math.Clamp((decimal)((now - start).TotalSeconds / dur.TotalSeconds), 0m, 1m);
            var unlocked = alloc * f;
            return new
            {
                address = kv.Key, start, end = start + dur, days = Math.Round(dur.TotalDays, 1),
                allocation = Math.Round(alloc), claimed = Math.Round(claimed), unlocked = Math.Round(unlocked),
                claimable = Math.Round(Math.Max(0, unlocked - claimed)), locked = Math.Round(alloc - unlocked),
                perDay = f is > 0 and < 1 && dur.TotalDays > 0 ? Math.Round(alloc / (decimal)dur.TotalDays) : 0m,
                balance = Math.Round(Dec(l["balance"])),
            };
        }).OrderBy(v => v.end).ToList();

        var moves = c.Safe.Select(m => new { m, k = TitnMoveKind(m) }).Where(x => x.k is not ("self" or "launch")).Select(x => new
        {
            time = x.m.Time, tx = x.m.Tx, kind = x.k, dir = x.m.From == TitnTreasurySafe ? "out" : "in",
            amount = x.m.Amount, counterparty = x.m.From == TitnTreasurySafe ? x.m.To : x.m.From,
            name = x.m.From == TitnTreasurySafe ? x.m.ToName : x.m.FromName,
        }).ToList();
        var claims = c.Vesting.SelectMany(kv => kv.Value.Where(m => m.From == kv.Key).Select(m => new { time = m.Time, tx = m.Tx, amount = m.Amount, to = m.To, contract = kv.Key }))
            .OrderByDescending(x => x.time).ToList();

        // Per month: what left the treasury by kind (liquidity as net: out minus what came back), and vesting claims.
        var months = moves.GroupBy(x => x.time.ToString("yyyy-MM")).Select(g => new
        {
            month = g.Key,
            wallet = g.Where(x => x.kind == "wallet").Sum(x => x.dir == "out" ? x.amount : -x.amount),
            vesting = g.Where(x => x.kind == "vesting" && x.dir == "out").Sum(x => x.amount),
            rewards = g.Where(x => x.kind == "rewards").Sum(x => x.dir == "out" ? x.amount : -x.amount),
            liquidity = g.Where(x => x.kind == "liquidity").Sum(x => x.dir == "out" ? x.amount : -x.amount),
            claims = claims.Where(cl => cl.time.ToString("yyyy-MM") == g.Key).Sum(cl => cl.amount),
        }).OrderBy(x => x.month).ToList();

        var cmcRows = (c.Live.GetValueOrDefault("cmc")?["rows"] as JsonArray ?? new()).OfType<JsonObject>().Select(r => new
        {
            name = r["name"]?.ToString(), totalPct = Dec(r["totalPct"]), unlockedPct = Dec(r["unlockedPct"]), progress = Dec(r["progress"]),
            total = Math.Round(total * Dec(r["totalPct"]) / 100m), unlocked = Math.Round(total * Dec(r["unlockedPct"]) / 100m),
        }).ToList();

        return new
        {
            updatedUtc = c.UpdatedUtc, price,
            supply = new { total = Math.Round(total), @base = Math.Round(Dec(s["base"])), arbitrum = Math.Round(Dec(s["arbitrum"])) },
            treasury = new { address = TitnTreasurySafe, balance = Math.Round(Dec(s["treasury"])), threshold = s["threshold"]?.GetValue<int>() ?? 0 },
            staking = Math.Round(Dec(s["staking"])),
            vesting, moves, claims, months,
            cmc = new { fetchedUtc = c.Live.GetValueOrDefault("cmc")?["fetchedUtc"]?.ToString(), rows = cmcRows },
        };
    }

    static (bool On, decimal Min, bool Liquidity) TitnAlertSettings(JsonObject push) =>
        (push["titnAlert"]?.GetValue<bool>() == true, push["titnMin"] is JsonValue v && v.TryGetValue<decimal>(out var d) && d > 0 ? d : TitnAlertDefault, push["titnLiquidity"]?.GetValue<bool>() == true);

    static void MapTitnWatch(WebApplication app)
    {
        app.MapGet("/api/titn/watch", async (HttpContext ctx) =>
        {
            var fresh = ctx.Request.Query["fresh"] == "1";
            TitnWatchCache c;
            try { c = await RefreshTitnWatchAsync(fresh, ctx.RequestAborted); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
            {
                if (LoadTitnWatch() is { } old) c = old;
                else return VError("Couldn't read the TITN treasury from the explorer: " + ex.Message, 502);
            }
            decimal price = 0;
            SortedDictionary<string, decimal> prices = new();
            try { prices = await LoadDailyPricesAsync(TitnPriceKey, false, ctx.RequestAborted); price = prices.LastOrDefault().Value; } catch (Exception) { }
            var (on, min, liq) = TitnAlertSettings(LoadPush(DashboardAuth.UserDataDir(ctx)));
            var push = LoadPush(DashboardAuth.UserDataDir(ctx));
            return VJson(new { data = BuildTitnWatch(c, price), prices = prices.Select(kv => new { d = kv.Key, p = kv.Value }), alerts = new { on, min, liquidity = liq, devices = (push["subs"] as JsonArray ?? new()).Count } });
        });

        // Bell: alert-sized moves in the last 3 days, from the cache only (no explorer call on every page load).
        app.MapGet("/api/titn/watch/recent", (HttpContext ctx) =>
        {
            var (on, min, liq) = TitnAlertSettings(LoadPush(DashboardAuth.UserDataDir(ctx)));
            if (!on || LoadTitnWatch() is not { } c) return VJson(new { items = Array.Empty<object>() });
            var since = DateTime.UtcNow.AddDays(-3);
            var items = TitnAlertMoves(c, min, liq).Where(x => x.M.Time >= since).OrderByDescending(x => x.M.Time).Take(5).Select(x => new { time = x.M.Time, text = x.Text, kind = x.Kind, tx = x.M.Tx }).ToList();
            // Fees stopped reaching the splitter for a week: volume collapsed, or fees were routed elsewhere.
            if (LoadTitnGrowth() is { Fees.Count: > 0 } g && g.Fees.Where(kv => kv.Value.GetValueOrDefault("in") > 0).Select(kv => kv.Key).DefaultIfEmpty("").Max() is { Length: > 0 } lastFee
                && DateTime.UtcNow - DateTime.ParseExact(lastFee, "yyyy-MM-dd", CultureInfo.InvariantCulture) > TimeSpan.FromDays(7))
                items.Insert(0, new { time = DateTime.UtcNow, text = $"No THORWallet fees have reached the splitter since {lastFee}", kind = "fees", tx = "" });
            return VJson(new { items });
        });

        // Body: { on, min, liquidity }.
        app.MapPost("/api/titn/watch/settings", async (HttpContext ctx) =>
        {
            JsonObject? b;
            try { b = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
            catch (JsonException) { return VError("Not valid JSON.", 400); }
            if (b is null) return VError("Nothing to save.", 400);
            var dir = DashboardAuth.UserDataDir(ctx);
            await PushGate.WaitAsync(ctx.RequestAborted);
            try
            {
                var push = LoadPush(dir);
                if (b["on"] is JsonValue ov && ov.TryGetValue<bool>(out var on))
                {
                    if (on && push["titnAlert"]?.GetValue<bool>() != true) push["titnSeen"] = DateTime.UtcNow; // only moves from now on
                    push["titnAlert"] = on;
                }
                if (b["min"] is JsonValue mv && mv.TryGetValue<decimal>(out var min) && min is >= 10_000m and <= 100_000_000m) push["titnMin"] = Math.Round(min);
                if (b["liquidity"] is JsonValue lv && lv.TryGetValue<bool>(out var liq)) push["titnLiquidity"] = liq;
                SavePush(dir, push);
            }
            finally { PushGate.Release(); }
            return VJson(new { ok = true });
        });
    }

    // Called from the 5-minute push timer: refresh the treasury watch when someone wants alerts, and push new big moves.
    static async Task TitnWatchTickAsync()
    {
        var usersDir = Path.Combine(Environment.CurrentDirectory, "odata", "users");
        if (!Directory.Exists(usersDir)) return;
        var wanting = Directory.GetDirectories(usersDir).Where(d => File.Exists(PushPath(d)) && TitnAlertSettings(LoadPush(d)).On).ToList();
        if (wanting.Count == 0) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        TitnWatchCache c;
        try { c = await RefreshTitnWatchAsync(false, cts.Token); }
        catch (Exception ex) { Console.WriteLine($"TITN watch refresh failed: {ex.Message}"); return; }
        foreach (var dir in wanting)
        {
            try
            {
                var push = LoadPush(dir);
                var (_, min, liq) = TitnAlertSettings(push);
                var seen = DateTime.TryParse(push["titnSeen"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var sv) ? sv : DateTime.UtcNow;
                var fresh = TitnAlertMoves(c, min, liq).Where(x => x.M.Seen > seen).OrderBy(x => x.M.Time).ToList();
                await PushGate.WaitAsync(cts.Token);
                try { push = LoadPush(dir); push["titnSeen"] = c.UpdatedUtc; SavePush(dir, push); }
                finally { PushGate.Release(); }
                // The fee split changed since this user last saw it?
                var splitNow = c.Live.GetValueOrDefault("split")?["rows"]?.ToJsonString() ?? "";
                var splitSeen = push["titnSplit"]?.ToString();
                if (splitNow != "" && splitSeen != splitNow)
                {
                    await PushGate.WaitAsync(cts.Token);
                    try { push = LoadPush(dir); push["titnSplit"] = splitNow; SavePush(dir, push); }
                    finally { PushGate.Release(); }
                    if (splitSeen is { Length: > 0 } && (push["subs"] as JsonArray ?? new()).Count > 0)
                    {
                        var stakers = (c.Live["split"]!["rows"] as JsonArray)!.OfType<JsonObject>().FirstOrDefault(r => r["to"]?.ToString() == TitnStakingContract)?["pct"]?.GetValue<int>() ?? 0;
                        await SendPushAsync(dir, new { title = $"TITN fee split changed: stakers now get {stakers}%", body = "THORWallet changed the fee splitter's recipients or percentages. See Chains → TITN.", url = "/page/titn", tag = "optic-titn-split" }, cts.Token);
                    }
                }
                if (fresh.Count == 0 || (push["subs"] as JsonArray ?? new()).Count == 0) continue;
                var sum = fresh.Sum(x => x.M.Amount);
                var title = fresh.Count == 1 ? $"TITN unlock: {fresh[0].M.Amount:N0} TITN moved" : $"TITN unlocks: {fresh.Count} moves, {sum:N0} TITN";
                var body = string.Join("; ", fresh.Take(4).Select(x => x.Text)) + (fresh.Count > 4 ? $"; and {fresh.Count - 4} more" : "") + ". Tokens leaving the treasury can reach the market.";
                var r = await SendPushAsync(dir, new { title, body, url = "/page/titn", tag = "optic-titn-" + fresh[^1].M.Tx[..10] }, cts.Token);
                Console.WriteLine($"TITN watch push for {Path.GetFileName(dir)}: {title} ({string.Join(", ", r.Select(x => JsonSerializer.Serialize(x)))})");
            }
            catch (Exception ex) { Console.WriteLine($"TITN watch push for {Path.GetFileName(dir)} failed: {ex.Message}"); }
        }
    }
}
