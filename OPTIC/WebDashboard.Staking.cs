using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// My Staking: the validators an address delegates to, its locks, and which validator each lock's OPT was delegated
// to when the lock was created. On Optio (OptioNetwork/optio v3.0.0, x/lockup) a lock belongs to the account, not to
// a validator: the chain only requires total delegations to stay at or above the total locked
// (x/lockup/ante/ante_handler.go 164–191, 409–428). The validator link shown here comes from the transaction that
// created each lock (a MsgDelegate or MsgSendDelegateAndLock delegating the same OPT), followed through extensions.
public static partial class WebDashboard
{
    static readonly HttpClient StakingHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    sealed record LockSegment(string? Validator, BigInteger Units);

    static async Task<JsonDocument?> LcdGetAsync(string lcdBase, string path, CancellationToken ct)
    {
        try
        {
            using var resp = await StakingHttp.GetAsync(lcdBase.TrimEnd('/') + path, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    static BigInteger ParseUnits(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return BigInteger.Zero;
        var whole = s.Split('.')[0];
        return BigInteger.TryParse(whole, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : BigInteger.Zero;
    }

    static decimal Opt(BigInteger units) => (decimal)units / 1_000_000m;

    /// <summary>
    /// Replays the address's lock history (lock and lock_extended events, oldest first) to find, for each unlock date,
    /// how much was delegated to which validator in the transaction that created it. Null validator = locked without
    /// a delegation in the same transaction.
    /// </summary>
    static async Task<(Dictionary<string, List<LockSegment>> Buckets, List<string> Notes)> LockOriginsAsync(string address, CancellationToken ct)
    {
        using var rpc = CreateRpcContext();
        var txs = new SortedDictionary<long, List<JsonElement>>();
        var seen = new HashSet<string>();
        foreach (var ev in new[] { "lock", "lock_extended" })
            await SearchTxsAsync(rpc.Http, $"{ev}.address='{address}'", (tx, hash, height) =>
            {
                if (!seen.Add(hash)) return;
                if (!txs.TryGetValue(height, out var list)) txs[height] = list = new List<JsonElement>();
                list.Add(tx.Clone());
            }, ct);

        var buckets = new Dictionary<string, List<LockSegment>>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var tx in txs.Values.SelectMany(x => x))
        {
            if (!tx.TryGetProperty("tx_result", out var result) || !result.TryGetProperty("events", out var events)) continue;
            var parsed = events.EnumerateArray()
                .Select(e => (Type: e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "", Attrs: ReadCometEventAttributes(e).ToDictionary(a => a.Key, a => a.Value)))
                .ToList();
            string? Attr(Dictionary<string, string> a, string k) => a.TryGetValue(k, out var v) ? v : null;
            var delegations = parsed.Where(e => e.Type == "delegate" && string.Equals(Attr(e.Attrs, "delegator"), address, StringComparison.OrdinalIgnoreCase)).ToList();
            var usedDelegations = new HashSet<int>();

            foreach (var e in parsed.Where(e => (e.Type == "lock" || e.Type == "lock_extended") && string.Equals(Attr(e.Attrs, "address"), address, StringComparison.OrdinalIgnoreCase)))
            {
                var amount = ParseUnits(Regex.Match(Attr(e.Attrs, "amount") ?? "", "^[0-9]+").Value);
                var date = Attr(e.Attrs, "unlock_date") ?? "";
                if (e.Type == "lock")
                {
                    // The delegation made with this lock: same message, else same amount, else the only one in the tx.
                    var msg = Attr(e.Attrs, "msg_index");
                    int pick = -1;
                    for (var i = 0; i < delegations.Count && pick < 0; i++)
                        if (!usedDelegations.Contains(i) && msg != null && Attr(delegations[i].Attrs, "msg_index") == msg) pick = i;
                    for (var i = 0; i < delegations.Count && pick < 0; i++)
                        if (!usedDelegations.Contains(i) && ParseUnits(Regex.Match(Attr(delegations[i].Attrs, "amount") ?? "", "^[0-9]+").Value) == amount) pick = i;
                    if (pick < 0 && delegations.Count == 1 && !usedDelegations.Contains(0)) pick = 0;
                    string? validator = null;
                    if (pick >= 0) { usedDelegations.Add(pick); validator = Attr(delegations[pick].Attrs, "validator"); }
                    if (!buckets.TryGetValue(date, out var list)) buckets[date] = list = new List<LockSegment>();
                    list.Add(new LockSegment(validator, amount));
                }
                else
                {
                    // Extension: move this amount from the old date to the new one, keeping each part's validator.
                    var oldDate = Attr(e.Attrs, "old_unlock_date") ?? "";
                    if (!buckets.TryGetValue(oldDate, out var from) || from.Count == 0)
                    {
                        notes.Add($"An extension from {oldDate} to {date} had no earlier lock record; its validator link is unknown.");
                        if (!buckets.TryGetValue(date, out var l2)) buckets[date] = l2 = new List<LockSegment>();
                        l2.Add(new LockSegment(null, amount));
                        continue;
                    }
                    var total = from.Aggregate(BigInteger.Zero, (s, x) => s + x.Units);
                    var moving = BigInteger.Min(amount, total);
                    if (!buckets.TryGetValue(date, out var to)) buckets[date] = to = new List<LockSegment>();
                    var moved = BigInteger.Zero;
                    var kept = new List<LockSegment>();
                    for (var i = 0; i < from.Count; i++)
                    {
                        var part = i == from.Count - 1 ? moving - moved : from[i].Units * moving / total;
                        moved += part;
                        if (part > 0) to.Add(new LockSegment(from[i].Validator, part));
                        if (from[i].Units - part > 0) kept.Add(new LockSegment(from[i].Validator, from[i].Units - part));
                    }
                    buckets[oldDate] = kept;
                }
            }
        }
        return (buckets, notes);
    }

    static void MapStaking(WebApplication app)
    {
        MapValidators(app);
        MapDistroReceived(app);
        MapExchangeFlows(app);
        MapOsmosisWallet(app);
        MapTitn(app);
        MapTitnLots(app);
        MapBsc(app);
        app.MapGet("/api/staking/positions", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null) return VError("Enter a valid optio1... wallet address.");
            var ct = ctx.RequestAborted;
            var lcd = LoadDashboardConfig().GetValueOrDefault("lcd", "http://127.0.0.1:1317");

            using var delegationsDoc = await LcdGetAsync(lcd, $"/cosmos/staking/v1beta1/delegations/{address}?pagination.limit=1000", ct);
            if (delegationsDoc is null) return VError($"The node's REST endpoint ({lcd}) did not answer.", 502);
            using var rewardsDoc = await LcdGetAsync(lcd, $"/cosmos/distribution/v1beta1/delegators/{address}/rewards", ct);
            using var unbondingDoc = await LcdGetAsync(lcd, $"/cosmos/staking/v1beta1/delegators/{address}/unbonding_delegations?pagination.limit=1000", ct);
            using var locksDoc = await LcdGetAsync(lcd, $"/optio/lockup/account_locks/{address}", ct);
            using var balanceDoc = await LcdGetAsync(lcd, $"/cosmos/bank/v1beta1/balances/{address}/by_denom?denom=uOPT", ct);
            using var stakingParamsDoc = await LcdGetAsync(lcd, "/cosmos/staking/v1beta1/params", ct);

            var staked = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
            foreach (var d in delegationsDoc.RootElement.GetProperty("delegation_responses").EnumerateArray())
                staked[d.GetProperty("delegation").GetProperty("validator_address").GetString()!] = ParseUnits(d.GetProperty("balance").GetProperty("amount").GetString());

            var rewards = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
            if (rewardsDoc is not null && rewardsDoc.RootElement.TryGetProperty("rewards", out var rw))
                foreach (var r in rw.EnumerateArray())
                    rewards[r.GetProperty("validator_address").GetString()!] = r.GetProperty("reward").EnumerateArray()
                        .Where(c => string.Equals(c.GetProperty("denom").GetString(), "uOPT", StringComparison.OrdinalIgnoreCase))
                        .Aggregate(BigInteger.Zero, (s, c) => s + ParseUnits(c.GetProperty("amount").GetString()));

            var unbonding = new Dictionary<string, List<(BigInteger Units, string? Completes)>>(StringComparer.Ordinal);
            if (unbondingDoc is not null && unbondingDoc.RootElement.TryGetProperty("unbonding_responses", out var ub))
                foreach (var u in ub.EnumerateArray())
                    unbonding[u.GetProperty("validator_address").GetString()!] = u.GetProperty("entries").EnumerateArray()
                        .Select(en => (ParseUnits(en.GetProperty("balance").GetString()), en.TryGetProperty("completion_time", out var ctm) ? ctm.GetString() : null)).ToList();

            var locks = new List<(string Date, BigInteger Units)>();
            if (locksDoc is not null && locksDoc.RootElement.TryGetProperty("locks", out var lk))
                foreach (var l in lk.EnumerateArray())
                    locks.Add((l.GetProperty("unlock_date").GetString()!, ParseUnits(l.GetProperty("amount").GetProperty("amount").GetString())));

            // Validator names and status.
            var validators = staked.Keys.Concat(unbonding.Keys).Concat(rewards.Keys).Distinct().ToList();
            var info = new Dictionary<string, (string Moniker, string Status, bool Jailed, string? Commission)>(StringComparer.Ordinal);
            await Task.WhenAll(validators.Select(async v =>
            {
                using var doc = await LcdGetAsync(lcd, $"/cosmos/staking/v1beta1/validators/{v}", ct);
                if (doc is null || !doc.RootElement.TryGetProperty("validator", out var val)) return;
                var moniker = val.TryGetProperty("description", out var desc) && desc.TryGetProperty("moniker", out var m) ? m.GetString() ?? v : v;
                var status = val.TryGetProperty("status", out var st) ? (st.GetString() ?? "").Replace("BOND_STATUS_", "").ToLowerInvariant() : "";
                var jailed = val.TryGetProperty("jailed", out var j) && j.ValueKind == JsonValueKind.True;
                string? commission = val.TryGetProperty("commission", out var cm) && cm.TryGetProperty("commission_rates", out var cr) && cr.TryGetProperty("rate", out var rate) ? rate.GetString() : null;
                lock (info) info[v] = (moniker, status, jailed, commission);
            }));
            string Moniker(string? v) => v is null ? "" : info.TryGetValue(v, out var i) ? i.Moniker : v;

            // Validator link of each current lock, from the lock history; scaled to the current amount if they differ.
            var notes = new List<string>();
            var buckets = new Dictionary<string, List<LockSegment>>();
            try { (buckets, var hn) = await LockOriginsAsync(address, ct); notes.AddRange(hn); }
            catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add("Lock history could not be read: " + ex.Message); }
            var lockRows = locks.OrderBy(l => l.Date, StringComparer.Ordinal).Select(l =>
            {
                var segs = buckets.TryGetValue(l.Date, out var s) ? s : new List<LockSegment>();
                var histTotal = segs.Aggregate(BigInteger.Zero, (a, x) => a + x.Units);
                if (histTotal > 0 && histTotal != l.Units)
                    notes.Add($"The lock until {l.Date} is {Opt(l.Units):N2} OPT on chain but its history adds to {Opt(histTotal):N2} OPT; the validator split is scaled to the current amount.");
                var parts = segs.GroupBy(x => x.Validator ?? "").Select(g =>
                {
                    var units = g.Aggregate(BigInteger.Zero, (a, x) => a + x.Units);
                    var scaled = histTotal > 0 ? units * l.Units / histTotal : BigInteger.Zero;
                    return (Validator: g.Key.Length == 0 ? null : g.Key, Units: scaled);
                }).Where(p => p.Units > 0).ToList();
                var linkedTotal = parts.Aggregate(BigInteger.Zero, (a, p) => a + p.Units);
                if (histTotal == 0) parts.Add((null, l.Units));
                else if (linkedTotal < l.Units) parts.Add((null, l.Units - linkedTotal)); // rounding remainder
                return new
                {
                    unlockDate = l.Date,
                    opt = Opt(l.Units),
                    parts = parts.GroupBy(p => p.Validator).Select(g => new { validator = g.Key, moniker = Moniker(g.Key), opt = Opt(g.Aggregate(BigInteger.Zero, (a, p) => a + p.Units)) })
                        .OrderBy(p => p.validator is null).ThenByDescending(p => p.opt).ToList(),
                };
            }).ToList();

            var totalStaked = staked.Values.Aggregate(BigInteger.Zero, (a, x) => a + x);
            var totalLocked = locks.Aggregate(BigInteger.Zero, (a, x) => a + x.Units);
            var free = BigInteger.Max(BigInteger.Zero, totalStaked - totalLocked);
            // Bank sends are allowed while the balance left covers the locked OPT that is not staked (x/lockup ante handler).
            BigInteger? balance = balanceDoc is not null && balanceDoc.RootElement.TryGetProperty("balance", out var bal) && bal.ValueKind == JsonValueKind.Object
                ? ParseUnits(bal.GetProperty("amount").GetString()) : null;
            var lockedNotStaked = BigInteger.Max(BigInteger.Zero, totalLocked - totalStaked);
            BigInteger? sendable = balance is null ? null : BigInteger.Max(BigInteger.Zero, balance.Value - lockedNotStaked);
            double? unbondingDays = stakingParamsDoc is not null && stakingParamsDoc.RootElement.TryGetProperty("params", out var sp) && sp.TryGetProperty("unbonding_time", out var ut)
                && double.TryParse((ut.GetString() ?? "").TrimEnd('s'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs) ? secs / 86400 : null;
            var validatorRows = validators.Select(v =>
            {
                var s = staked.GetValueOrDefault(v);
                var linked = lockRows.SelectMany(l => l.parts.Where(p => p.validator == v).Select(p => new { l.unlockDate, p.opt })).ToList();
                info.TryGetValue(v, out var i);
                return new
                {
                    operatorAddress = v, moniker = Moniker(v), status = i.Status, jailed = i.Jailed, commission = i.Commission,
                    stakedOpt = Opt(s), shareOfStake = totalStaked > 0 ? (decimal)s / (decimal)totalStaked : 0m,
                    rewardsOpt = Opt(rewards.GetValueOrDefault(v)),
                    unbondingOpt = Opt(unbonding.TryGetValue(v, out var ul) ? ul.Aggregate(BigInteger.Zero, (a, x) => a + x.Units) : BigInteger.Zero),
                    unbondingCompletes = unbonding.TryGetValue(v, out var ul2) ? ul2.Select(x => x.Completes).Where(c => c != null).OrderBy(c => c).FirstOrDefault() : null,
                    lockedWithThisValidatorOpt = linked.Sum(x => x.opt),
                    lockedWithThisValidator = linked,
                    canUndelegateNowOpt = Opt(BigInteger.Min(s, free)),
                };
            }).OrderByDescending(v => v.stakedOpt).ToList();

            return VJson(new
            {
                address,
                asOfUtc = DateTime.UtcNow,
                totals = new
                {
                    stakedOpt = Opt(totalStaked), lockedOpt = Opt(totalLocked), freeToUndelegateOpt = Opt(free),
                    balanceOpt = balance is null ? (decimal?)null : Opt(balance.Value), sendableNowOpt = sendable is null ? (decimal?)null : Opt(sendable.Value), unbondingDays,
                    rewardsOpt = Opt(rewards.Values.Aggregate(BigInteger.Zero, (a, x) => a + x)),
                    unbondingOpt = validatorRows.Sum(v => v.unbondingOpt),
                    unbondingEntries = unbonding.SelectMany(kv => kv.Value.Select(x => new { validator = kv.Key, moniker = Moniker(kv.Key), opt = Opt(x.Units), completes = x.Completes }))
                        .OrderBy(x => x.completes, StringComparer.Ordinal).ToList(),
                    lockedWithoutValidatorOpt = lockRows.SelectMany(l => l.parts.Where(p => p.validator is null)).Sum(p => p.opt),
                },
                validators = validatorRows,
                locks = lockRows,
                notes,
                source = new { lcd, chainCode = "OptioNetwork/optio v3.0.0 x/lockup (locks are per account; total staked must stay at or above total locked)" },
            });
        });
    }
}
