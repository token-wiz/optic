// SOVR (Sovren, chain sovr-1): a Cosmos SDK layer one with sovr1... addresses; SOVR is usovr with 6 decimals (Cosmos chain
// registry "sovr"). Balances for saved wallets (in wallet, staked, unbonding, unclaimed rewards, lockup locks), and the
// Chains > SOVR page: latest block, the chain's own supply ledger (x/supply: released, latent, vesting, locked), staking,
// validators, lockup settings, governance and the DefiLlama price. Transaction history: WebDashboard.SovrLots.cs.
using OPTIC.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    const string SovrPriceKey = "coingecko:sovren";
    static readonly string[] SovrLcds = { "https://api.sovrchain.net" };
    const decimal Usovr = 1_000_000m;

    internal static bool IsSovrAddress(string? a) => a is not null && Regex.IsMatch(a, "^sovr1[02-9ac-hj-np-z]{38,58}$");

    // The signed-in user's saved sovr1... addresses with their labels.
    static List<(string Address, string Label)> UserSovrWallets(HttpContext ctx)
    {
        var path = Path.Combine(DashboardAuth.UserDataDir(ctx), "wallets.json");
        if (!File.Exists(path)) return new();
        var arr = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray();
        return arr.Select(w => (Address: (w?["a"]?.ToString() ?? "").ToLowerInvariant(), Label: w?["l"]?.ToString() ?? ""))
            .Where(w => IsSovrAddress(w.Address)).ToList();
    }

    static async Task<JsonNode?> SovrGetAsync(string path, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var lcd in SovrLcds)
        {
            try { return JsonNode.Parse(await EvmHttp.GetStringAsync(lcd + path, ct)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { last = ex; }
        }
        throw new HttpRequestException("SOVR: no endpoint answered" + (last is null ? "." : ": " + last.Message));
    }

    static decimal SovrAmount(JsonNode? n) =>
        decimal.TryParse(n?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d / Usovr : 0m;

    // Sum of usovr in a coin list ([{ denom, amount }]).
    static decimal SovrCoins(JsonNode? coins) =>
        (coins as JsonArray ?? new()).OfType<JsonObject>().Where(c => c["denom"]?.ToString() == "usovr").Sum(c => SovrAmount(c["amount"]));

    sealed record SovrBalance(decimal Liquid, decimal Staked, decimal Unbonding, decimal Rewards, decimal Locked, List<(decimal Amount, DateTime Completes)> UnbondingEntries);

    // One account's SOVR: in the wallet, delegated, unbonding (with dates), unclaimed staking rewards and lockup locks.
    static async Task<SovrBalance> LoadSovrBalanceAsync(string address, CancellationToken ct)
    {
        var bank = SovrGetAsync($"/cosmos/bank/v1beta1/balances/{address}", ct);
        var dels = SovrGetAsync($"/cosmos/staking/v1beta1/delegations/{address}", ct);
        var unb = SovrGetAsync($"/cosmos/staking/v1beta1/delegators/{address}/unbonding_delegations", ct);
        var rew = SovrGetAsync($"/cosmos/distribution/v1beta1/delegators/{address}/rewards", ct);
        var locks = SovrGetAsync($"/parler-tech/sovr/lockup/v1/locks/{address}", ct);
        await Task.WhenAll(bank, dels, unb, rew);
        var entries = new List<(decimal, DateTime)>();
        foreach (var u in (unb.Result?["unbonding_responses"] as JsonArray ?? new()).OfType<JsonObject>())
            foreach (var e in (u["entries"] as JsonArray ?? new()).OfType<JsonObject>())
                entries.Add((SovrAmount(e["balance"]), DateTime.TryParse(e["completion_time"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTime.MinValue));
        // Lock records: the amount field is read whichever name it has (amount / usovr, a coin or a number).
        var locked = 0m;
        try
        {
            foreach (var l in ((await locks)?["locks"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                var a = l["amount"] ?? l["usovr"] ?? l["amount_usovr"];
                locked += a is JsonObject coin ? SovrAmount(coin["amount"]) : SovrAmount(a);
            }
        }
        catch (HttpRequestException) { }
        return new SovrBalance(
            SovrCoins(bank.Result?["balances"]),
            (dels.Result?["delegation_responses"] as JsonArray ?? new()).OfType<JsonObject>().Sum(d => SovrAmount(d["balance"]?["amount"])),
            entries.Sum(e => e.Item1),
            SovrCoins(rew.Result?["total"]),
            locked,
            entries);
    }

    // Rows for the balances list: SOVR in wallet, staked, unbonding and unclaimed rewards (each noted).
    static async Task<List<(string Symbol, decimal Amount, string Note)>> LoadSovrBalancesAsync(string address, CancellationToken ct)
    {
        var b = await LoadSovrBalanceAsync(address, ct);
        var rows = new List<(string, decimal, string)> { ("SOVR", b.Liquid, ""), ("SOVR", b.Staked, b.Locked > 0 ? $"staked ({b.Locked:0.##} locked)" : "staked"), ("SOVR", b.Unbonding, "unbonding"), ("SOVR", b.Rewards, "unclaimed rewards") };
        return rows.Where(r => r.Item2 > 0.000001m).ToList();
    }

    // A REST query answered from the chain's state at an earlier block (the node keeps recent history).
    static async Task<JsonNode?> SovrGetAtAsync(string path, long height, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SovrLcds[0] + path);
        req.Headers.Add("x-cosmos-block-height", height.ToString(CultureInfo.InvariantCulture));
        using var res = await EvmHttp.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    // The staking reward rate, measured: Sovren mints nothing on a schedule (no x/mint), so rewards are fees and the staking
    // share of network revenue. Every reward pays the community tax into the community pool, so the pool's growth over the
    // last 7 days (or 1 day, if the node no longer has a week of history), divided by the tax rate, is what all stakers earned.
    // That per year over today's staked amount is the gross rate; less the validators' typical commission, a delegator's.
    static async Task<object?> SovrRewardRateAsync(long height, DateTime now, decimal bonded, decimal? communityTax, decimal commission, CancellationToken ct)
    {
        if (communityTax is not > 0m || bonded <= 0 || height <= 0) return null;
        static decimal Pool(JsonNode? n) => SovrCoins(n?["pool"]);
        var poolNow = Pool(await SovrGetAtAsync("/cosmos/distribution/v1beta1/community_pool", height, ct));
        foreach (var days in new[] { 7, 1 })
        {
            try
            {
                var back = height - days * 14_400L; // about 6-second blocks
                var then = await SovrGetAtAsync("/cosmos/distribution/v1beta1/community_pool", back, ct);
                var block = await SovrGetAsync($"/cosmos/base/tendermint/v1beta1/blocks/{back}", ct);
                if (!DateTime.TryParse(block?["block"]?["header"]?["time"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t0)) continue;
                var span = (decimal)(now - t0).TotalDays;
                var earned = (poolNow - Pool(then)) / communityTax.Value;
                if (span <= 0.1m || earned <= 0) continue;
                var perYear = earned / span * 365m;
                var gross = perYear / bonded;
                return new { grossApr = Math.Round(gross, 4), netApr = Math.Round(gross * (1 - commission), 4), commission, perYear = Math.Round(perYear, 2), days = Math.Round(span, 1), from = t0 };
            }
            catch (HttpRequestException) { }
        }
        return null;
    }

    static (DateTime At, object Data)? _sovrOverview;
    static readonly SemaphoreSlim SovrOverviewGate = new(1, 1);

    // The chain-wide figures for Chains > SOVR, kept for two minutes.
    static async Task<object> BuildSovrOverviewAsync(CancellationToken ct)
    {
        await SovrOverviewGate.WaitAsync(ct);
        try
        {
            if (_sovrOverview is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(2)) return c.Data;
            async Task<JsonNode?> Try(string path) { try { return await SovrGetAsync(path, ct); } catch (HttpRequestException) { return null; } }
            var block = Try("/cosmos/base/tendermint/v1beta1/blocks/latest");
            var supply = Try("/sovr/supply/v1/state");
            var buckets = Try("/sovr/supply/v1/latent-sub-buckets");
            var audit = Try("/sovr/supply/v1/audit");
            var pool = Try("/cosmos/staking/v1beta1/pool");
            var sparams = Try("/cosmos/staking/v1beta1/params");
            var vals = Try("/cosmos/staking/v1beta1/validators?status=BOND_STATUS_BONDED&pagination.limit=300");
            var lparams = Try("/parler-tech/sovr/lockup/v1/params");
            var queue = Try("/parler-tech/sovr/lockup/v1/claim-queue");
            var gov = Try("/cosmos/gov/v1/proposals?pagination.limit=8&pagination.reverse=true");
            var dist = Try("/cosmos/distribution/v1beta1/params");
            await Task.WhenAll(block, supply, buckets, audit, pool, sparams, vals, lparams, queue, gov, dist);

            var header = block.Result?["block"]?["header"];
            var s = supply.Result;
            var bonded = SovrAmount(pool.Result?["pool"]?["bonded_tokens"]);
            var validators = (vals.Result?["validators"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(v => new
                {
                    moniker = v["description"]?["moniker"]?.ToString() ?? "",
                    operatorAddress = v["operator_address"]?.ToString() ?? "",
                    tokens = SovrAmount(v["tokens"]),
                    commission = decimal.TryParse(v["commission"]?["commission_rates"]?["rate"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var cr) ? cr : 0m,
                    jailed = v["jailed"]?.GetValue<bool>() ?? false,
                })
                .OrderByDescending(v => v.tokens).ToList();
            var communityTaxRate = decimal.TryParse(dist.Result?["params"]?["community_tax"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var ctr) ? ctr : (decimal?)null;
            var commissions = validators.Where(v => !v.jailed).Select(v => v.commission).OrderBy(c => c).ToList();
            var typicalCommission = commissions.Count > 0 ? commissions[commissions.Count / 2] : 0.1m;
            object? rewardRate = null;
            try
            {
                if (long.TryParse(header?["height"]?.ToString(), out var hNow) && DateTime.TryParse(header?["time"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var tNow))
                    rewardRate = await SovrRewardRateAsync(hNow, tNow, bonded, communityTaxRate, typicalCommission, ct);
            }
            catch (HttpRequestException) { }
            static long Secs(JsonNode? n) => long.TryParse(n?.ToString()?.TrimEnd('s'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
            var proposals = (gov.Result?["proposals"] as JsonArray ?? new()).OfType<JsonObject>().Select(p => new
            {
                id = p["id"]?.ToString(),
                title = p["title"]?.ToString() is { Length: > 0 } t ? t : (p["messages"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["@type"]?.ToString()?.Split('.').Last() ?? "",
                status = (p["status"]?.ToString() ?? "").Replace("PROPOSAL_STATUS_", "").Replace('_', ' ').ToLowerInvariant(),
                votingEnd = p["voting_end_time"]?.ToString(),
            }).ToList();

            SortedDictionary<string, decimal> prices = new();
            try { prices = await LoadDailyPricesAsync(SovrPriceKey, false, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            decimal? priceNow = null;
            try
            {
                using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync($"https://coins.llama.fi/prices/current/{SovrPriceKey}", ct));
                priceNow = doc.RootElement.GetProperty("coins").GetProperty(SovrPriceKey).GetProperty("price").GetDecimal();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { }

            var data = new
            {
                chainId = header?["chain_id"]?.ToString(),
                height = long.TryParse(header?["height"]?.ToString(), out var h) ? h : (long?)null,
                blockTime = header?["time"]?.ToString(),
                supply = s is null ? null : new
                {
                    total = SovrAmount(s["total_usovr"]),
                    released = SovrAmount(s["claimed_usovr"]),
                    latent = SovrAmount(s["latent_usovr"]),
                    vesting = SovrAmount(s["vesting_usovr"]),
                    locked = SovrAmount(s["locked_usovr"]),
                },
                latentBuckets = (buckets.Result?["buckets"] as JsonArray ?? new()).OfType<JsonObject>().Select(b => new { name = b["name"]?.ToString(), amount = SovrAmount(b["usovr"]) }).ToList(),
                auditOk = audit.Result?["invariant_ok"]?.GetValue<bool>(),
                auditHeight = audit.Result?["checked_height"]?.ToString(),
                staking = new
                {
                    bonded,
                    validatorCount = validators.Count,
                    unbondingDays = Secs(sparams.Result?["params"]?["unbonding_time"]) / 86400m,
                    maxValidators = sparams.Result?["params"]?["max_validators"]?.ToString(),
                    communityTax = communityTaxRate,
                    rewardRate,
                },
                validators,
                lockup = lparams.Result?["params"] is JsonObject lp ? new
                {
                    lockDays = Secs(lp["lock_duration_seconds"]) / 86400m,
                    unbondingDays = Secs(lp["unbonding_duration_seconds"]) / 86400m,
                    vestingDays = Secs(lp["vesting_duration_seconds"]) / 86400m,
                    earlyUnlockPenaltyBps = lp["early_unlock_penalty_bps"]?.ToString(),
                } : null,
                claimQueue = queue.Result is JsonObject q ? new
                {
                    vesting = SovrAmount(q["total_vesting_usovr"]),
                    next24h = SovrAmount(q["released_next_24h_usovr"]),
                    next30d = SovrAmount(q["released_next_30d_usovr"]),
                    positions = q["active_position_count"]?.ToString(),
                } : null,
                proposals,
                price = priceNow ?? (prices.Count > 0 ? prices.Last().Value : (decimal?)null),
                priceHistory = prices.Where(p => string.CompareOrdinal(p.Key, DateTime.UtcNow.AddDays(-90).ToString("yyyy-MM-dd")) >= 0).Select(p => new { date = p.Key, usd = p.Value }).ToList(),
                source = SovrLcds[0],
                readUtc = DateTime.UtcNow,
            };
            _sovrOverview = (DateTime.UtcNow, data);
            return data;
        }
        finally { SovrOverviewGate.Release(); }
    }

    static void MapSovr(WebApplication app)
    {
        app.MapGet("/api/sovr/overview", async (HttpContext ctx) =>
        {
            var overview = await BuildSovrOverviewAsync(ctx.RequestAborted);
            // The signed-in user's own SOVR, per saved sovr1... wallet.
            var mine = new List<object>();
            foreach (var w in UserSovrWallets(ctx))
            {
                try
                {
                    var b = await LoadSovrBalanceAsync(w.Address, ctx.RequestAborted);
                    mine.Add(new { address = w.Address, label = w.Label, liquid = b.Liquid, staked = b.Staked, unbonding = b.Unbonding, rewards = b.Rewards, locked = b.Locked,
                        unbondingEntries = b.UnbondingEntries.Select(e => new { amount = e.Amount, completes = e.Completes }) });
                }
                catch (HttpRequestException ex) { mine.Add(new { address = w.Address, label = w.Label, error = ex.Message }); }
            }
            return VJson(new { overview, mine });
        });
    }
}
