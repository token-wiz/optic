using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OPTIC.Services;
using OPTIC.Services.Valuation;

// OPT Historical Liquidity and Distribution Analysis: server side. Keeps reference values, market-capacity evidence
// and reviewed tax values apart; nothing here changes a tax value without an explicit approval.
public static partial class WebDashboard
{
    const string ValuationDisclaimer = "Historical market-capacity analysis supports valuation review. It is not an IRS-approved discount formula, a guarantee of sale proceeds, or a substitute for a tax determination. Network scenarios are hypothetical. Missing information is not evidence of zero value.";
    const string CmcHistoricalEndpoint = "https://api.coinmarketcap.com/data-api/v3.1/cryptocurrency/historical?id=35828&convertId=2781";

    static readonly Lazy<ValuationStore> _valuationStore = new(() => new ValuationStore(Path.Combine(Environment.CurrentDirectory, "odata", "valuation.db")));
    static ValuationStore VStore => _valuationStore.Value;
    // Short timeout: the public archive sometimes stalls; a stalled request is retried rather than waited on.
    static readonly HttpClient OsmosisHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly ConcurrentDictionary<string, (DateTime At, List<RewardReceipt> Receipts)> _receiptCache = new();
    static readonly Regex OptioAddress = new("^optio1[02-9ac-hj-np-z]{38,58}$");

    static readonly HashSet<string> OutDisposal = new() { "exchange-sale", "sale", "node", "node-expense", "payment", "worthless" };
    static readonly HashSet<string> OutCarry = new() { "exchange-held", "own", "gift" };
    static readonly HashSet<string> SkipKinds = new() { "loan", "ignore", "loan-back" };

    static IResult VJson(object value, int status = 200) => Results.Json(value, ValuationStore.Json, statusCode: status);
    static IResult VError(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);

    static TimeZoneInfo ReportingZone() => DistributionSchedule.Zone(LoadDashboardConfig().GetValueOrDefault("tz") ?? "America/New_York");

    /// <summary>
    /// Every reward receipt for the address, one per transfer event (tx hash + event index + position), so distinct
    /// payments in one tx are never merged or deduplicated. Node vs staking/participation is inferred from payout size
    /// (see ClassifyDistributionKinds) and marked Inferred until the user confirms it; payments from the distribution
    /// module are chain staking rewards.
    /// </summary>
    static async Task<List<RewardReceipt>> GetRewardReceiptsAsync(string address, CancellationToken ct)
    {
        if (_receiptCache.TryGetValue(address, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(15))
            return hit.Receipts;

        using var rpc = CreateRpcContext();
        var distributionModule = ModuleAccounts.FirstOrDefault(kv => kv.Value == "distribution").Key;
        var raw = new List<(string Hash, long Height, int Ev, int Pair, string Sender, BigInteger Units)>();
        await SearchTxsAsync(rpc.Http, $"transfer.recipient='{address}'", (tx, hash, height) =>
        {
            foreach (var t in GetTxTransfersIndexed(tx, rpc.DenomWantedUpper))
            {
                if (!t.Recipient.Equals(address, StringComparison.OrdinalIgnoreCase)) continue;
                var fromSource = t.Sender.Equals(DistributionSourceAddress, StringComparison.OrdinalIgnoreCase);
                var fromModule = distributionModule != null && t.Sender.Equals(distributionModule, StringComparison.OrdinalIgnoreCase);
                if (fromSource || fromModule) raw.Add((hash, height, t.EventIndex, t.PairIndex, t.Sender.ToLowerInvariant(), t.AmountUnits));
            }
        }, ct);

        await ResolveBlockTimesAsync(rpc.Http, raw.Select(r => r.Height), ct);
        raw.RemoveAll(r => !_blockTimeCache.ContainsKey(r.Height));

        // Size-based node/staking inference on the distribution-source payments, one row per event.
        var distRows = raw.Where(r => r.Sender == DistributionSourceAddress)
            .Select(r => (r, row: new AddressDistributionRow { Height = r.Height, TxHash = r.Hash, TimeUtc = _blockTimeCache[r.Height], AmountOpt = ToDecimalOpt(r.Units, rpc.ScaleInt) }))
            .ToList();
        ClassifyDistributionKinds(distRows.Select(x => x.row).OrderBy(x => x.Height).ToList());
        var kindByEvent = distRows.ToDictionary(x => (x.r.Hash, x.r.Ev, x.r.Pair), x => x.row);
        var dayKinds = PayoutClassifier.Classify(distRows.Select(x => (x.row.TimeUtc, x.row.AmountOpt)).ToList());
        var dayKindByEvent = distRows.Select((x, i) => (x, i)).ToDictionary(t => (t.x.r.Hash, t.x.r.Ev, t.x.r.Pair), t => dayKinds[t.i].Day);

        var times = raw.Select(r => _blockTimeCache[r.Height]).Distinct().ToList();
        var prices = await TokenPriceService.GetPricesAtAsync(times, ct);
        var receipts = new List<RewardReceipt>();
        foreach (var r in raw.OrderBy(r => r.Height).ThenBy(r => r.Ev).ThenBy(r => r.Pair))
        {
            var time = _blockTimeCache[r.Height];
            var isModule = r.Sender != DistributionSourceAddress;
            var row = isModule ? null : kindByEvent[(r.Hash, r.Ev, r.Pair)];
            var receipt = new RewardReceipt
            {
                Id = ReceiptIdentity.ForChainEvent("optio", r.Hash, r.Ev, r.Pair, address, "uOPT"),
                IdentityBasis = "chain-event",
                Wallet = address, Chain = "optio", TxHash = r.Hash.ToUpperInvariant(), EventIndex = r.Ev, MsgIndex = null, Ordinal = r.Pair,
                TimestampUtc = time,
                Category = isModule ? RewardCategory.Staking : row!.Kind == "staking" ? RewardCategory.Staking : RewardCategory.Node,
                // Rule-based labels count as confirmed only once the user confirms the rule (ReceiptsForUserAsync).
                Classification = isModule ? ClassificationStatus.Confirmed : row!.KindInferred ? ClassificationStatus.Uncertain : ClassificationStatus.Inferred,
                ClassificationBasis = isModule ? "Paid by the distribution module (chain staking reward)"
                    : dayKindByEvent[(r.Hash, r.Ev, r.Pair)] switch
                    {
                        PayoutDayKind.Split => "Smaller same-day payout is staking, larger is node",
                        PayoutDayKind.Single => "Only payout that day: guessed from the sizes on nearby days",
                        _ => "Same-size payouts that day: guessed from the sizes on nearby days",
                    },
                Denom = "uOPT", Decimals = 6, BaseUnits = (decimal)r.Units, Sender = r.Sender,
            };
            if (prices.TryGetValue(time, out var p) && p is not null)
            {
                receipt.Reference = new ReferenceValuation
                {
                    UnitPriceUsd = p.PriceUsd,
                    ValueUsd = receipt.QuantityOpt * p.PriceUsd,
                    Status = "priced",
                    Source = new PriceSourceInfo
                    {
                        Provider = "CoinMarketCap", Endpoint = $"{CmcHistoricalEndpoint}&interval={p.Interval}", Field = "quote.close",
                        Interval = p.Interval == "1h" ? "close of the 1-hour candle containing the receipt" : "close of the UTC day (no hourly candle)",
                        QuoteCurrency = "USD", ObservedUtc = p.CandleOpenUtc,
                        Fallback = p.Interval == "1h" ? null : "daily close used because the hourly candle was unavailable",
                    },
                };
            }
            receipts.Add(receipt);
        }
        _receiptCache[address] = (DateTime.UtcNow, receipts);
        return receipts;
    }

    /// <summary>Chain receipts plus the user's imported receipts, with the user's confirmed classifications applied.</summary>
    static Task<List<RewardReceipt>> ReceiptsForUserAsync(HttpContext ctx, string address, CancellationToken ct) =>
        ReceiptsForUserAsync(DashboardAuth.UserName(ctx), address, ct);

    static async Task<List<RewardReceipt>> ReceiptsForUserAsync(string user, string address, CancellationToken ct)
    {
        var chain = await GetRewardReceiptsAsync(address, ct);
        var imported = VStore.List<RewardReceipt>("imported-receipt", user).Where(r => r.Owner != ValuationStore.SystemOwner && r.Value.Wallet == address).Select(r => r.Value);
        var byId = new Dictionary<string, RewardReceipt>(StringComparer.Ordinal);
        foreach (var r in chain.Concat(imported))
            byId.TryAdd(r.Id, Clone(r));
        var rule = ClassificationRuleFor(user);
        var cls = VStore.Classifications(user);
        foreach (var r in byId.Values)
        {
            if (rule != null && r.Classification == ClassificationStatus.Inferred && r.ClassificationBasis?.StartsWith("Smaller same-day", StringComparison.Ordinal) == true)
            {
                r.Classification = ClassificationStatus.Confirmed;
                r.ClassificationBasis += $" (rule confirmed by {rule.ConfirmedBy} on {rule.ConfirmedUtc:yyyy-MM-dd})";
            }
            if (cls.TryGetValue(r.Id, out var c))
            {
                r.Category = c.Category;
                r.Classification = c.Status;
                r.ClassificationBasis = $"Set by you{(string.IsNullOrWhiteSpace(c.Note) ? "" : ": " + c.Note)}";
            }
        }
        return byId.Values.OrderBy(r => r.TimestampUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    static ClassificationRuleConfirmation? ClassificationRuleFor(string user) =>
        VStore.Get<ClassificationRuleConfirmation>("classification-rule", "rule:" + user.ToLowerInvariant())?.Value;

    static RewardReceipt Clone(RewardReceipt r) => JsonSerializer.Deserialize<RewardReceipt>(JsonSerializer.Serialize(r, ValuationStore.Json), ValuationStore.Json)!;

    static string? AddressParam(HttpContext ctx)
    {
        var a = (ctx.Request.Query["address"].ToString() ?? "").Trim().ToLowerInvariant();
        return OptioAddress.IsMatch(a) ? a : null;
    }

    static (DateOnly From, DateOnly To)? RangeParams(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        if (int.TryParse(q["year"], out var y) && y is > 2000 and < 2100) return (new DateOnly(y, 1, 1), new DateOnly(y, 12, 31));
        if (DateOnly.TryParseExact(q["from"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) &&
            DateOnly.TryParseExact(q["to"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t >= f)
            return (f, t);
        return null;
    }

    static async Task<string> BodyTextAsync(HttpContext ctx, int maxBytes = 5_000_000)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > maxBytes) throw new InvalidDataException("Upload too large.");
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static string OwnerFor(HttpContext ctx, string? scope) =>
        string.Equals(scope, "system", StringComparison.OrdinalIgnoreCase) ? ValuationStore.SystemOwner : DashboardAuth.UserName(ctx);

    static bool CanWriteOwner(HttpContext ctx, string owner) =>
        ValuationStore.CanWrite(DashboardAuth.UserName(ctx), DashboardAuth.IsAdmin(ctx), owner);

    static List<T> Visible<T>(HttpContext ctx, string kind) => Visible<T>(DashboardAuth.UserName(ctx), kind);
    static List<T> Visible<T>(string user, string kind) => VStore.List<T>(kind, user).Select(r => r.Value).ToList();

    static TaxpayerProfile ProfileFor(HttpContext ctx) => ProfileFor(DashboardAuth.UserName(ctx));
    static TaxpayerProfile ProfileFor(string user) =>
        VStore.Get<TaxpayerProfile>("profile", "profile:" + user.ToLowerInvariant())?.Value ?? new TaxpayerProfile();

    static Dictionary<string, JsonElement> TaxLabelsFor(HttpContext ctx, string address)
    {
        var path = TaxLabelsPath(ctx, address);
        lock (TaxLabelsLock)
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? new() : new();
    }

    static HashSet<string> OwnWallets(HttpContext ctx)
    {
        var path = Path.Combine(DashboardAuth.UserDataDir(ctx), "wallets.json");
        try
        {
            if (!File.Exists(path)) return new();
            return (JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray())
                .Select(n => n?["a"]?.GetValue<string>()?.ToLowerInvariant()).Where(a => a != null).Select(a => a!).ToHashSet();
        }
        catch (JsonException) { return new(); }
    }

    // ------------------------------------------------------------------ analysis

    sealed class AnalyzeRequest
    {
        public string Address { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public List<string>? ReceiptIds { get; set; }
        public double MaxAgeHours { get; set; } = 24;
        public List<decimal>? Thresholds { get; set; }
        public decimal? TransactionCostsUsd { get; set; }
        public string? TransactionCostsBasis { get; set; }
        public bool Stress { get; set; } = true;
        public List<decimal>? ScenarioFractions { get; set; }
        public string? ScenarioBasis { get; set; }
    }

    /// <summary>Markets that may stand for the receipt time: contemporaneous snapshots only, with their access decisions.</summary>
    static (List<CandidateMarket> Accessible, List<CandidateMarket> Evidence, List<string> Later) CandidatesAt(
        DateTime atUtc, SnapshotPolicy policy, List<OrderBookSnapshot> books, List<OsmosisPoolSnapshot> pools, List<VenueStatus> venues, TaxpayerProfile profile)
    {
        var accessible = new List<CandidateMarket>();
        var evidence = new List<CandidateMarket>();
        var later = new List<string>();
        foreach (var g in books.GroupBy(b => (b.Venue, b.Market)))
        {
            var (snap, after, reason) = policy.Select(g, b => b.ObservedUtc, atUtc);
            later.AddRange(after.Take(3).Select(a => a.Id));
            if (snap is null) continue;
            var access = VenueAccess.Decide(venues, g.Key.Venue, atUtc, profile, onChainVenue: false);
            var m = new CandidateMarket(new OrderBookMarket(snap), access, reason);
            accessible.Add(m);
            evidence.Add(m with { Access = access with { TaxpayerAccessible = true, Blocking = null } });
        }
        // Osmosis: the most recent verified height at or before the receipt within the age limit.
        var heights = pools.Where(p => p.ServedHeight != null && p.BlockTimeUtc != null).GroupBy(p => p.ServedHeight!.Value).ToList();
        var (hgroup, laterH, _) = policy.Select(heights, g => g.First().BlockTimeUtc!.Value, atUtc);
        later.AddRange(laterH.Take(3).Select(g => $"osmosis@{g.Key}"));
        if (hgroup is not null)
        {
            var (routes, excluded) = PoolRouteMarket.DiscoverRoutes(hgroup.ToList(), hgroup.Key);
            var access = VenueAccess.Decide(venues, "Osmosis", atUtc, profile, onChainVenue: true);
            foreach (var r in routes)
            {
                var m = new CandidateMarket(new PoolRouteMarket(r), access, PoolRouteMarket.Unsupported(r));
                accessible.Add(m);
                evidence.Add(m with { Access = access with { TaxpayerAccessible = true, Blocking = null } });
            }
            foreach (var x in excluded) later.Add("excluded: " + x);
        }
        return (accessible, evidence, later);
    }

    // ------------------------------------------------------------------ reviewed report

    /// <param name="wallets">The selected wallet and the user's other saved wallets, each with its receipts and ledger.
    /// They are processed together so OPT moved between them keeps its lots (basis and acquisition dates).</param>
    static object BuildReviewedReport(HttpContext ctx, string address, int year, Dictionary<string, (List<RewardReceipt> Receipts, WalletLedger Ledger)> wallets)
    {
        var user = DashboardAuth.UserName(ctx);
        var receipts = wallets[address].Receipts;
        var allReceipts = wallets.Values.SelectMany(w => w.Receipts).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var reviews = VStore.Reviews(user);
        var selected = ValuationReviews.Selected(reviews, allReceipts);
        var own = OwnWallets(ctx);
        own.Add(address);
        var yearEnd = new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        bool InYear(DateTime t) => t.Year == year;

        var events = new List<LotEvent>();
        var unresolvedTransfers = new List<object>();
        foreach (var (w, data) in wallets)
            foreach (var r in data.Receipts.Where(r => r.TimestampUtc <= yearEnd))
                events.Add(new LotEvent { Id = r.Id, Wallet = w, TimeUtc = r.TimestampUtc, Kind = LotEventKind.RewardReceipt, QuantityOpt = r.QuantityOpt, ValueUsd = selected.TryGetValue(r.Id, out var s) ? s.ValueUsd : null, Category = r.Category });

        // All wallets' transfers in time order, so loan balances follow what actually happened.
        var giftSenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var gp = TaxGiftSendersPath(ctx);
            if (File.Exists(gp)) foreach (var k in (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(gp)) ?? new()).Keys) giftSenders.Add(k);
        }
        catch (JsonException) { }
        var labelsBy = wallets.Keys.ToDictionary(w => w, w => TaxLabelsFor(ctx, w));
        var loans = new LoanNetting();
        var transfers = wallets.SelectMany(kv => kv.Value.Ledger.Entries.Where(e => e.TimeUtc <= yearEnd).Select(e => (W: kv.Key, E: e)))
            .OrderBy(x => x.E.Height).ThenBy(x => x.E.Direction == "out" ? 1 : 0).ToList();
        foreach (var (w, e) in transfers)
        {
            var isSelected = w == address;
            var key = $"{e.TxHash}|{e.Direction}|{e.Counterparty}";
            labelsBy[w].TryGetValue(key, out var label);
            string? kind = label.ValueKind == JsonValueKind.Object && label.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (kind == "unclassified") kind = null;
            decimal? usd = label.ValueKind == JsonValueKind.Object && label.TryGetProperty("usd", out var u) && decimal.TryParse(u.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var uv) ? uv : null;
            DateTime? date = label.ValueKind == JsonValueKind.Object && label.TryGetProperty("date", out var d) && DateTime.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dv) ? dv.Date.AddHours(23).AddMinutes(59) : null;
            // A transfer between two of the user's own processed wallets moves its lots unchanged.
            var ownTransfer = own.Contains(e.Counterparty) && wallets.ContainsKey(e.Counterparty) && (kind is null or "own");
            if (e.Direction == "in")
            {
                if (e.Category is "distribution" or "reward" or "module") continue; // receipts above, or own staked tokens returning
                if (ownTransfer) { events.Add(new LotEvent { Id = key, Wallet = w, TimeUtc = e.TimeUtc, Kind = LotEventKind.TransferIn, QuantityOpt = e.AmountOpt, TransferKey = $"{e.TxHash}|{w}", Note = "own" }); continue; }
                if (kind == "loan-back") { loans.Repaid(e.Counterparty, e.AmountOpt); continue; }
                if (kind != null && SkipKinds.Contains(kind)) continue;
                var qty = e.AmountOpt;
                if (kind == null)
                {
                    // Unlabelled: repayment first, up to what this person still owes; the rest follows the default.
                    qty -= loans.AutoRepay(e.Counterparty, qty);
                    if (qty <= 0) continue;
                    if (giftSenders.Contains(e.Counterparty)) { kind = "gift-in"; usd = 0; }
                }
                if (kind == null && !own.Contains(e.Counterparty) && isSelected && InYear(e.TimeUtc)) unresolvedTransfers.Add(new { e.TxHash, e.Direction, e.Counterparty, AmountOpt = qty, e.TimeUtc });
                events.Add(new LotEvent { Id = key, Wallet = w, TimeUtc = e.TimeUtc, Kind = LotEventKind.Acquisition, QuantityOpt = qty, ValueUsd = kind == "gift-in" ? (usd ?? 0) * qty / e.AmountOpt : usd, Note = kind ?? "unclassified" });
                continue;
            }
            if (e.Category == "module") continue;
            if (e.Category == "fee") { events.Add(new LotEvent { Id = key, Wallet = w, TimeUtc = e.TimeUtc, Kind = LotEventKind.Fee, QuantityOpt = e.AmountOpt }); continue; }
            if (ownTransfer) { events.Add(new LotEvent { Id = key, Wallet = w, TimeUtc = e.TimeUtc, Kind = LotEventKind.TransferOut, QuantityOpt = e.AmountOpt, TransferKey = $"{e.TxHash}|{e.Counterparty}", Note = "own" }); continue; }
            kind ??= own.Contains(e.Counterparty) ? "own" : null;
            if (kind == "loan") { loans.Lent(e.Counterparty, e.AmountOpt); continue; }
            if (kind != null && SkipKinds.Contains(kind)) continue;
            if (kind == null)
            {
                // Not classified: the OPT left the wallet but is not treated as a sale.
                if (isSelected && InYear(e.TimeUtc)) unresolvedTransfers.Add(new { e.TxHash, e.Direction, e.Counterparty, e.AmountOpt, e.TimeUtc });
                events.Add(new LotEvent { Id = key, Wallet = w, TimeUtc = e.TimeUtc, Kind = InYear(e.TimeUtc) ? LotEventKind.Unresolved : LotEventKind.CarryOut, QuantityOpt = e.AmountOpt, Note = "unclassified" });
                continue;
            }
            if (OutCarry.Contains(kind)) { events.Add(new LotEvent { Id = key, Wallet = w, TimeUtc = e.TimeUtc, Kind = LotEventKind.CarryOut, QuantityOpt = e.AmountOpt, Note = kind }); continue; }
            // Disposal proceeds are what the user entered; this report never fills or changes them.
            events.Add(new LotEvent { Id = key, Wallet = w, TimeUtc = e.TimeUtc, Kind = LotEventKind.Disposal, QuantityOpt = e.AmountOpt, ValueUsd = kind == "worthless" ? (usd ?? 0) : usd, DisposedUtc = date, Note = kind });
        }
        var byWallet = LotRecalculator.RunWallets(events);
        var lots = byWallet.TryGetValue(address, out var lr) ? lr : new LotResult();
        events = events.Where(e => e.Wallet == address).ToList();
        var yearReceipts = receipts.Where(r => InYear(r.TimestampUtc)).ToList();
        var coverage = ValuationReviews.Coverage(yearReceipts, selected);
        var dispositions = lots.Dispositions.Where(d => InYear(d.DisposedUtc)).ToList();
        var categories = new[] { RewardCategory.Node, RewardCategory.Staking, RewardCategory.Other, RewardCategory.Unclassified };
        var byCategory = categories.Select(c =>
        {
            var xs = yearReceipts.Where(r => r.Category == c).ToList();
            var both = xs.Where(r => selected.ContainsKey(r.Id) && r.Reference.ValueUsd != null).ToList();
            return new
            {
                category = c.ToString(),
                receipts = xs.Count,
                quantityOpt = xs.Sum(r => r.QuantityOpt),
                referenceValueUsd = xs.Where(r => r.Reference.ValueUsd != null).Sum(r => r.Reference.ValueUsd!.Value),
                unpricedReferenceOpt = xs.Where(r => r.Reference.ValueUsd == null).Sum(r => r.QuantityOpt),
                selectedValueUsd = xs.Where(r => selected.ContainsKey(r.Id)).Sum(r => selected[r.Id].ValueUsd),
                adjustmentUsd = both.Sum(r => selected[r.Id].ValueUsd - r.Reference.ValueUsd!.Value),
                unvaluedOpt = xs.Where(r => !selected.ContainsKey(r.Id)).Sum(r => r.QuantityOpt),
                unvaluedReferenceEstimateUsd = xs.Where(r => !selected.ContainsKey(r.Id) && r.Reference.ValueUsd != null).Sum(r => r.Reference.ValueUsd!.Value),
                uncertainClassification = xs.Count(r => r.Classification != ClassificationStatus.Confirmed),
            };
        }).Where(x => x.receipts > 0).ToList();

        // Evidence behind the values, from the candidates built for these receipts.
        var cands = VStore.List<FmvCandidate>("fmv-candidate", user).Where(c => c.Owner != ValuationStore.SystemOwner).Select(c => c.Value)
            .Where(c => yearReceipts.Any(r => r.Id == c.ReceiptId)).ToList();
        var periods = Visible<NetworkDistributionPeriod>(user, "distribution");
        var yearDays = Enumerable.Range(0, new DateOnly(year, 12, 31).DayNumber - new DateOnly(year, 1, 1).DayNumber + 1).Select(i => new DateOnly(year, 1, 1).AddDays(i)).ToList();
        var emitted = yearDays.Select(d => DistributionSchedule.ResolveEmission(periods, d)).ToList();
        var combined = yearDays.Select(d => DistributionSchedule.Resolve(periods, d, RewardCategory.Combined, DistributionBasis.Observed)).ToList();
        var methodsUsed = yearReceipts.Where(r => selected.ContainsKey(r.Id)).GroupBy(r => selected[r.Id].Method)
            .Select(g => new { method = g.Key.ToString(), receipts = g.Count(), selectedUsd = g.Sum(r => selected[r.Id].ValueUsd), referenceUsd = g.Sum(r => r.Reference.ValueUsd ?? 0) }).ToList();

        // Missing information and blockers for a final report.
        var missing = new List<string>();
        if (coverage.Unresolved > 0) missing.Add($"{coverage.Unresolved} receipt(s), {coverage.UnvaluedOpt:N2} OPT, have no selected value (reference estimate {byCategory.Sum(c => c.unvaluedReferenceEstimateUsd):N2} USD, not included in income).");
        var unconfirmed = yearReceipts.Count(r => r.Classification != ClassificationStatus.Confirmed);
        if (unconfirmed > 0) missing.Add($"{unconfirmed} receipt(s) have an unconfirmed node/staking classification.");
        if (unresolvedTransfers.Count > 0) missing.Add($"{unresolvedTransfers.Count} transfer(s) in {year} are not classified on the Tax Report page (not counted as sales).");
        var nodeBuys = dispositions.Where(d => events.First(e => e.Id == d.EventId).Note == "node" && d.ProceedsUsd is null).ToList();
        if (nodeBuys.Count > 0) missing.Add($"{nodeBuys.Count} node purchase(s) paid in OPT have no amount realized. Enter the value of the licenses received on the Tax Report page; the one-time promotional exchange is not valued from the disputed OPT price and is not used as market evidence.");
        var unknownProceeds = dispositions.Count(d => d.ProceedsUsd is null) - nodeBuys.Count;
        if (unknownProceeds > 0) missing.Add($"{unknownProceeds} disposition(s) have no proceeds entered.");
        var unknownBasis = dispositions.Count(d => d.BasisUsd is null);
        if (unknownBasis > 0) missing.Add($"{unknownBasis} disposition(s) draw on lots with unknown basis (unresolved receipt values or unpriced acquisitions).");
        if (cands.Count < yearReceipts.Count) missing.Add($"{yearReceipts.Count - cands.Count} receipt(s) have no market evidence built yet (Approvals & reports → Receipt-date FMV).");
        if (lots.UnmatchedTransfersIn > 0) missing.Add($"{lots.UnmatchedTransfersIn} transfer(s) from an own wallet could not be matched to the sending side; their basis is unknown.");
        if (lots.UnmatchedOpt > 0) missing.Add($"{lots.UnmatchedOpt:N2} OPT left the wallet with no matching lot (history before the wallet's records, or a missing receipt).");

        var remainingKnown = lots.RemainingLots.Where(l => l.UnitBasisUsd != null).Sum(l => l.QuantityOpt * l.UnitBasisUsd!.Value);
        var disposedBasisAll = lots.Dispositions.Sum(d => d.Parts.Sum(p => p.BasisUsd ?? 0));
        var basisOut = disposedBasisAll + lots.BasisCarriedOutUsd + lots.BasisFeesUsd + lots.BasisUnresolvedOutUsd + lots.BasisTransferredOutUsd + remainingKnown;
        var incomeYear = coverage.KnownValueUsd;
        var rewardLotsYear = events.Where(e => e.Kind == LotEventKind.RewardReceipt && InYear(e.TimeUtc) && e.ValueUsd != null).Sum(e => e.ValueUsd!.Value);

        var complete = coverage.Complete && unresolvedTransfers.Count == 0 && dispositions.All(d => d.GainUsd != null) && unconfirmed == 0;
        var reviewIds = reviews.Where(r => r.ReceiptIds.Any(id => yearReceipts.Any(x => x.Id == id))).Select(r => r.ReviewId).ToHashSet();
        return new
        {
            title = $"OPT receipt-date FMV report, {year}",
            status = complete ? "final" : "draft",
            statusText = complete ? "Every taxable receipt in the year has a selected value and confirmed category, and all transfers are classified."
                : "Draft: not every in-scope item is resolved (see Missing information). Selected-value subtotals, unresolved quantities and reference estimates are shown separately; unresolved values are not zero.",
            wallet = address,
            year,
            preparedUtc = DateTime.UtcNow,
            preparedBy = user,
            reportingTimeZone = ReportingZone().Id,
            policy = FmvPolicy.Current.Version,
            policyDescription = FmvPolicy.Current.Describe(),
            policyApproval = PolicyApprovalFor(user),
            disclaimer = ValuationDisclaimer,
            coverage,
            categories = byCategory,
            income = new
            {
                knownSelectedUsd = coverage.KnownValueUsd,
                unvaluedOpt = coverage.UnvaluedOpt,
                unvaluedReferenceEstimateUsd = byCategory.Sum(c => c.unvaluedReferenceEstimateUsd),
                referenceUsd = byCategory.Sum(c => c.referenceValueUsd),
                adjustmentUsd = byCategory.Sum(c => c.adjustmentUsd),
                note = "Each receipt's selected value is both its income and its lot basis. Receipts without a selected value are listed as unresolved, not as zero, and the reference estimate for them is shown separately and not included.",
            },
            methods = methodsUsed,
            evidence = new
            {
                candidates = cands.Count,
                qualifyingUnderPolicy = cands.Count(c => c.Qualifies),
                bySource = cands.GroupBy(c => c.Source ?? "none").Select(g => new { source = g.Key, receipts = g.Count() }),
                flags = cands.SelectMany(c => c.Flags).GroupBy(f => f.Trigger).Select(g => new { trigger = g.Key.ToString(), receipts = g.Count() }),
                withOsmosisPoolState = cands.Count(c => c.Evidence.OsmosisObservedUtc != null),
                largestPriceImpact = cands.Where(c => c.Evidence.ModeledPriceImpact != null).Select(c => c.Evidence.ModeledPriceImpact).DefaultIfEmpty(null).Max(),
                network = new
                {
                    emittedOpt = emitted.All(e => e.OptPerDay != null) ? emitted.Sum(e => e.OptPerDay!.Value) : (decimal?)null,
                    emittedDaysRecorded = emitted.Count(e => e.OptPerDay != null),
                    distributedObservedOpt = combined.Where(c => c.OptPerDay != null).Sum(c => c.OptPerDay!.Value),
                    distributedDaysObserved = combined.Count(c => c.OptPerDay != null),
                    days = yearDays.Count,
                    note = "Network distributions and hypothetical selling scenarios are supporting context only; they are not applied as discounts.",
                },
            },
            missingInformation = missing,
            reconciliation = new
            {
                incomeSelectedUsd = incomeYear,
                rewardLotBasisCreatedUsd = rewardLotsYear,
                incomeEqualsBasis = Math.Abs(incomeYear - rewardLotsYear) < 0.01m,
                basisInKnownUsd = lots.BasisInKnownUsd,
                basisInUnknownOpt = lots.BasisInUnknownOpt,
                basisDisposedUsd = disposedBasisAll,
                basisCarriedOutUsd = lots.BasisCarriedOutUsd,
                basisFeesUsd = lots.BasisFeesUsd,
                basisUnresolvedOutUsd = lots.BasisUnresolvedOutUsd,
                basisToOwnWalletsUsd = lots.BasisTransferredOutUsd,
                basisFromOwnWalletsUsd = lots.BasisTransferredInUsd,
                ownWallets = wallets.Keys.Where(k => k != address),
                basisRemainingUsd = remainingKnown,
                differenceUsd = lots.BasisInKnownUsd - basisOut,
                dispositionsInYear = dispositions.Count,
                proceedsUsd = dispositions.Sum(d => d.ProceedsUsd ?? 0),
                basisUsd = dispositions.Sum(d => d.BasisUsd ?? 0),
                gainUsd = dispositions.Sum(d => d.GainUsd ?? 0),
                note = "Through the end of the year: known basis in (selected receipt values, entered costs and lots received from your own wallets) equals basis disposed + carried out + fees + not-classified transfers + sent to your own wallets + still held. Any difference comes from lots with unknown basis.",
            },
            dispositions = dispositions.Select(d => new { d.EventId, d.DisposedUtc, d.QuantityOpt, d.ProceedsUsd, d.BasisUsd, d.GainUsd, term = d.LongTerm ? "long" : "short", kind = events.First(e => e.Id == d.EventId).Note, parts = d.Parts }),
            remainingLots = new { quantityOpt = lots.RemainingLots.Sum(l => l.QuantityOpt), knownBasisUsd = remainingKnown, unknownBasisOpt = lots.RemainingLots.Where(l => l.UnitBasisUsd == null).Sum(l => l.QuantityOpt) },
            unmatchedOpt = lots.UnmatchedOpt,
            unresolvedTransfers,
            loans = loans.Balances().Select(b => new { b.Counterparty, b.LentOpt, b.RepaidOpt, b.AutoRepaidOpt, b.OwedOpt }),
            approvals = reviews.Where(r => reviewIds.Contains(r.ReviewId)).OrderBy(r => r.ReviewedUtc).Select(r => new { r.ReviewId, r.Version, status = r.Status.ToString(), method = r.Method.ToString(), r.SelectedValueUsd, receipts = r.ReceiptIds.Count, r.AllocationMethod, r.GroupJustification, r.Explanation, r.Reviewer, r.ReviewedUtc, evidence = r.Evidence.Count, r.Source }),
            selectedValues = yearReceipts.Select(r =>
            {
                selected.TryGetValue(r.Id, out var sv);
                var cand = cands.FirstOrDefault(c => c.ReceiptId == r.Id);
                return new
                {
                    receiptId = r.Id, r.TimestampUtc, category = r.Category.ToString(), classification = r.Classification.ToString(), r.QuantityOpt,
                    referenceValueUsd = r.Reference.ValueUsd, selectedValueUsd = sv?.ValueUsd, method = sv?.Method.ToString(),
                    adjustmentUsd = sv is not null && r.Reference.ValueUsd is { } rv ? sv.ValueUsd - rv : (decimal?)null,
                    reviewId = sv is null ? null : sv.ReviewId + " v" + sv.Version,
                    flags = cand?.Flags.Select(f => f.Trigger.ToString()),
                };
            }),
            assumptions = new[]
            {
                "Lots are matched first-in, first-out within this wallet, the same method as the Tax Report page; acquisition dates and transfer history are kept.",
                "Proceeds and sale dates of transfers come from the Tax Report page's entries; they are not changed here.",
                "Node rewards and staking/participation rewards are reported separately; labels come from the user-confirmed payout rule or individual choices, not from payment size alone.",
                "Section 179, hosting expenses, business classification and self-employment tax are outside this report and unchanged.",
                "Selling scenarios, issuance growth and modeled immediate-sale proceeds are evidence for review only; none is applied as an automatic discount.",
                "The Tax Report page's older automatic rules (marketability discount, blockage discount, next/last sellable price) are not used here.",
            },
            fingerprint = ReceiptIdentity.Sha256Hex(string.Join("|", selected.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value.ValueUsd.ToString(CultureInfo.InvariantCulture)}@{k.Value.ReviewId}v{k.Value.Version}"))),
        };
    }
    // ------------------------------------------------------------------ endpoints

    static void MapValuation(WebApplication app, LocalDataSyncService syncService)
    {
        MapFmv(app);
        MapStaking(app);
        // Receipts with reference values, selected values and per-day network context.
        app.MapGet("/api/valuation/receipts", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null) return VError("Enter a valid optio1... wallet address.");
            var range = RangeParams(ctx);
            try
            {
                var all = await ReceiptsForUserAsync(ctx, address, ctx.RequestAborted);
                var zone = ReportingZone();
                var receipts = range is null ? all : all.Where(r => { var d = DistributionSchedule.DayOf(r.TimestampUtc, TimeZoneInfo.Utc); return d >= range.Value.From && d <= range.Value.To; }).ToList();
                var byId = all.ToDictionary(r => r.Id);
                var selected = ValuationReviews.Selected(VStore.Reviews(DashboardAuth.UserName(ctx)), byId);
                var periods = Visible<NetworkDistributionPeriod>(ctx, "distribution");
                var analyses = VStore.List<ReceiptAnalysis>("analysis", DashboardAuth.UserName(ctx)).Where(a => a.Owner != ValuationStore.SystemOwner).ToDictionary(a => a.Value.ReceiptId, a => a.Value);
                var reviewState = ValuationReviews.Current(VStore.Reviews(DashboardAuth.UserName(ctx)));
                return VJson(new
                {
                    address,
                    reportingTimeZone = zone.Id,
                    disclaimer = ValuationDisclaimer,
                    classificationRule = ClassificationRuleFor(DashboardAuth.UserName(ctx)),
                    coverage = ValuationReviews.Coverage(receipts, selected),
                    receipts = receipts.Select(r =>
                    {
                        var dayUtc = DistributionSchedule.DayOf(r.TimestampUtc, TimeZoneInfo.Utc);
                        var node = DistributionSchedule.Resolve(periods, dayUtc, RewardCategory.Node);
                        var combined = DistributionSchedule.Resolve(periods, dayUtc, RewardCategory.Combined);
                        analyses.TryGetValue(r.Id, out var an);
                        var pending = reviewState.Where(v => v.Status == ReviewStatus.Proposed && v.ReceiptIds.Contains(r.Id)).Select(v => v.ReviewId).ToList();
                        return new
                        {
                            receipt = r,
                            ledgerKey = r.TxHash is null || r.Sender is null ? null : ReceiptIdentity.LedgerKey(r.TxHash, r.Sender),
                            reportingDay = DistributionSchedule.DayOf(r.TimestampUtc, zone),
                            utcDay = dayUtc,
                            network = new { node, combined },
                            analysis = an,
                            selected = selected.TryGetValue(r.Id, out var s) ? s : null,
                            proposedReviews = pending,
                        };
                    }),
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return VError(ex.Message, 500);
            }
        });

        app.MapPost("/api/valuation/receipts/classify", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var ids = body.TryGetProperty("receiptIds", out var idsEl) ? idsEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new();
            if (ids.Count == 0 || ids.Count > 5000) return VError("Give 1 to 5000 receipt ids.");
            if (!Enum.TryParse<RewardCategory>(body.TryGetProperty("category", out var c) ? c.GetString() : "", true, out var category) || category == RewardCategory.Combined)
                return VError("category must be node, staking, other or unclassified.");
            var status = body.TryGetProperty("status", out var st) && Enum.TryParse<ClassificationStatus>(st.GetString(), true, out var sv) ? sv : ClassificationStatus.Confirmed;
            var note = body.TryGetProperty("note", out var n) ? n.GetString() : null;
            var user = DashboardAuth.UserName(ctx);
            foreach (var id in ids) VStore.AddClassification(user, id, category, status, note, user);
            return VJson(new { updated = ids.Count });
        });

        // The user's attestation of the payout-size rule. Recorded with who and when; revocable.
        app.MapPost("/api/valuation/receipts/rule", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (!(body.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True)) return VError("Confirmation needed.");
            var user = DashboardAuth.UserName(ctx);
            var c = new ClassificationRuleConfirmation
            {
                Statement = body.TryGetProperty("statement", out var st) && st.GetString() is { Length: > 0 } s ? s : "On a day with more than one payout from the distribution account, the smaller payout(s) are staking/participation rewards and the larger are node rewards.",
                ConfirmedBy = user, ConfirmedUtc = DateTime.UtcNow,
            };
            VStore.Put("classification-rule", "rule:" + user.ToLowerInvariant(), user, c, user);
            return VJson(c);
        });
        app.MapDelete("/api/valuation/receipts/rule", (HttpContext ctx) =>
        {
            var user = DashboardAuth.UserName(ctx);
            VStore.Delete("classification-rule", "rule:" + user.ToLowerInvariant(), user);
            return VJson(new { revoked = true });
        });

        app.MapPost("/api/valuation/receipts/import", async (HttpContext ctx) =>
        {
            string text;
            try { text = await BodyTextAsync(ctx); } catch (InvalidDataException ex) { return VError(ex.Message, 413); }
            var name = ctx.Request.Query["name"].ToString();
            var (receipts, errors, dups) = ReceiptIdentity.ImportCsv(text, string.IsNullOrWhiteSpace(name) ? "receipts.csv" : name);
            if (errors.Count > 0) return VJson(new { imported = 0, errors }, 400);
            var user = DashboardAuth.UserName(ctx);
            var sha = VStore.ArchiveEvidence(string.IsNullOrWhiteSpace(name) ? "receipts.csv" : name, Encoding.UTF8.GetBytes(text), null, user);
            var existing = VStore.List<RewardReceipt>("imported-receipt", user).Select(r => r.Id).ToHashSet();
            var added = 0;
            foreach (var r in receipts)
            {
                if (existing.Contains(r.Id)) { dups++; continue; }
                VStore.Put("imported-receipt", r.Id, user, r, user, r.TimestampUtc, sha);
                added++;
            }
            return VJson(new { imported = added, duplicates = dups, sourceSha256 = sha });
        });

        // ---- network distributions
        app.MapGet("/api/valuation/distributions", (HttpContext ctx) =>
            VJson(VStore.List<NetworkDistributionPeriod>("distribution", DashboardAuth.UserName(ctx)).Select(r => new { r.Owner, r.CreatedUtc, r.CreatedBy, period = r.Value })));

        app.MapPost("/api/valuation/distributions", async (HttpContext ctx) =>
        {
            var owner = OwnerFor(ctx, ctx.Request.Query["scope"]);
            if (!CanWriteOwner(ctx, owner)) return VError("Only an admin can change shared evidence.", 403);
            NetworkDistributionPeriod? p;
            try { p = await JsonSerializer.DeserializeAsync<NetworkDistributionPeriod>(ctx.Request.Body, ValuationStore.Json, ctx.RequestAborted); }
            catch (JsonException ex) { return VError("Invalid period: " + ex.Message); }
            if (p is null) return VError("Missing period.");
            if (p.EffectiveTo < p.EffectiveFrom) return VError("The end date is before the start date.");
            if (p.ScheduledOptPerDay is null && p.ObservedOptPerDay is null && p.UserAssumedOptPerDay is null && p.EmittedOptPerDay is null) return VError("Give a scheduled, observed, user-assumed or emitted amount.");
            if (p.EffectiveTo.DayNumber - p.EffectiveFrom.DayNumber > 3660) return VError("A period can cover at most ten years; split longer schedules at each change.");
            p.Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id;
            p.Origin = "manual";
            if (p.UserAssumedOptPerDay is not null && p.Verification == VerificationStatus.Verified && p.ScheduledOptPerDay is null && p.ObservedOptPerDay is null)
                p.Verification = VerificationStatus.UserReported; // an assumption alone cannot be verified
            p.Evidence.Add(new EvidenceRef { Kind = "user-statement", Description = "Entered on the Liquidity & Distribution Review page", CollectedUtc = DateTime.UtcNow, UserReported = true });
            VStore.Put("distribution", p.Id, owner, p, DashboardAuth.UserName(ctx), p.EffectiveFrom.ToDateTime(TimeOnly.MinValue));
            return VJson(p);
        });

        app.MapDelete("/api/valuation/distributions/{id}", (string id, HttpContext ctx) => DeleteRecord(ctx, "distribution", id));

        app.MapPost("/api/valuation/distributions/import", async (HttpContext ctx) =>
        {
            var owner = OwnerFor(ctx, ctx.Request.Query["scope"]);
            if (!CanWriteOwner(ctx, owner)) return VError("Only an admin can change shared evidence.", 403);
            string text;
            try { text = await BodyTextAsync(ctx); } catch (InvalidDataException ex) { return VError(ex.Message, 413); }
            var name = ctx.Request.Query["name"].ToString();
            name = string.IsNullOrWhiteSpace(name) ? "distribution-schedule.csv" : name;
            var sha = ReceiptIdentity.Sha256Hex(text);
            var (periods, errors) = DistributionSchedule.ImportCsv(text, name, sha, DateTime.UtcNow);
            if (errors.Count > 0) return VJson(new { imported = 0, errors }, 400);
            VStore.ArchiveEvidence(name, Encoding.UTF8.GetBytes(text), null, DashboardAuth.UserName(ctx));
            foreach (var p in periods) VStore.Put("distribution", p.Id, owner, p, DashboardAuth.UserName(ctx), p.EffectiveFrom.ToDateTime(TimeOnly.MinValue), sha);
            return VJson(new { imported = periods.Count, sourceSha256 = sha });
        });

        // Observed daily totals from the synced chain data: every transfer from the distribution account per UTC day,
        // which includes node and participation payouts together, so it is recorded as Combined.
        app.MapPost("/api/valuation/distributions/from-chain", async (HttpContext ctx) =>
        {
            if (!DashboardAuth.IsAdmin(ctx)) return VError("Only an admin can change shared evidence.", 403);
            var stats = await syncService.GetAllDailyStatsAsync();
            var count = 0;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var s in stats.Where(s => s.DistributedOpt is > 0))
            {
                if (!DateOnly.TryParseExact(s.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d >= today) continue; // today is still partial
                var p = new NetworkDistributionPeriod
                {
                    Id = $"chain-combined-{d:yyyyMMdd}", EffectiveFrom = d, EffectiveTo = d, Category = RewardCategory.Combined,
                    ObservedOptPerDay = s.DistributedOpt, Verification = VerificationStatus.Observed, Origin = "chain",
                    Notes = $"Sum of all OPT sent by the distribution account {DistributionSourceAddress} on this UTC day (node and participation payouts together; not split by kind). Blocks {s.StartBlockNumber}-{s.EndBlockNumber}.",
                    Evidence = { new EvidenceRef { Kind = "chain-query", Description = "OPTIC synced daily stats (DailyStats.DistributedOpt) from the Optio chain", EffectiveUtc = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), CollectedUtc = DateTime.UtcNow, ParserVersion = "optic-dailystats/1", BlockHeight = s.EndBlockNumber, RepositoryRef = "OptioNetwork/optio (chain); distribution account per OPTIC Program.cs" } },
                };
                VStore.Put("distribution", p.Id, ValuationStore.SystemOwner, p, DashboardAuth.UserName(ctx), d.ToDateTime(TimeOnly.MinValue));
                count++;
            }
            return VJson(new { imported = count, note = "Recorded as Combined (node + participation), Observed, UTC days. A node-only share needs node-only network data." });
        });

        // ---- supply observations
        app.MapGet("/api/valuation/supply", (HttpContext ctx) => VJson(Visible<SupplyObservation>(ctx, "supply")));
        app.MapPost("/api/valuation/supply", async (HttpContext ctx) =>
        {
            var owner = OwnerFor(ctx, ctx.Request.Query["scope"]);
            if (!CanWriteOwner(ctx, owner)) return VError("Only an admin can change shared evidence.", 403);
            var o = await JsonSerializer.DeserializeAsync<SupplyObservation>(ctx.Request.Body, ValuationStore.Json, ctx.RequestAborted);
            if (o is null || o.AtUtc == default) return VError("Give at least the time of the observation.");
            if (string.IsNullOrWhiteSpace(o.Source)) return VError("Give the source.");
            if (o.CirculatingSupplyOpt is not null && string.IsNullOrWhiteSpace(o.CirculatingDefinition)) return VError("Say how circulating supply is defined.");
            o.Id = string.IsNullOrWhiteSpace(o.Id) ? Guid.NewGuid().ToString("N") : o.Id;
            VStore.Put("supply", o.Id, owner, o, DashboardAuth.UserName(ctx), o.AtUtc);
            return VJson(o);
        });
        app.MapPost("/api/valuation/supply/from-chain", async (HttpContext ctx) =>
        {
            if (!DashboardAuth.IsAdmin(ctx)) return VError("Only an admin can change shared evidence.", 403);
            var count = 0;
            foreach (var s in (await syncService.GetAllDailyStatsAsync()).Where(s => s.TotalSupply is > 0))
            {
                if (!DateOnly.TryParseExact(s.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                var o = new SupplyObservation
                {
                    Id = $"chain-supply-{d:yyyyMMdd}", AtUtc = d.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc), BlockHeight = s.EndBlockNumber,
                    TotalSupplyOpt = s.TotalSupply, Source = "OPTIC synced daily stats (bank total supply of uOPT)",
                    CoverageLimitations = "End-of-day total bank supply as synced by OPTIC; circulating supply is not defined here (staked and locked OPT are not assumed excluded).",
                };
                VStore.Put("supply", o.Id, ValuationStore.SystemOwner, o, DashboardAuth.UserName(ctx), o.AtUtc);
                count++;
            }
            return VJson(new { imported = count });
        });
        app.MapDelete("/api/valuation/supply/{id}", (string id, HttpContext ctx) => DeleteRecord(ctx, "supply", id));

        // ---- venue access statuses
        app.MapGet("/api/valuation/venues", (HttpContext ctx) =>
            VJson(VStore.List<VenueStatus>("venue-status", DashboardAuth.UserName(ctx)).Select(r => new { r.Owner, r.CreatedBy, status = r.Value })));
        app.MapPost("/api/valuation/venues", async (HttpContext ctx) =>
        {
            var owner = OwnerFor(ctx, ctx.Request.Query["scope"]);
            if (!CanWriteOwner(ctx, owner)) return VError("Only an admin can change shared evidence.", 403);
            var v = await JsonSerializer.DeserializeAsync<VenueStatus>(ctx.Request.Body, ValuationStore.Json, ctx.RequestAborted);
            if (v is null || string.IsNullOrWhiteSpace(v.Venue) || v.EffectiveFromUtc == default) return VError("Give the venue, what the status covers and when it took effect.");
            v.Id = string.IsNullOrWhiteSpace(v.Id) ? Guid.NewGuid().ToString("N") : v.Id;
            if (owner != ValuationStore.SystemOwner && v.Verification == VerificationStatus.Verified && v.Evidence.All(e => e.UserReported))
                v.Verification = VerificationStatus.UserReported;
            VStore.Put("venue-status", v.Id, owner, v, DashboardAuth.UserName(ctx), v.EffectiveFromUtc);
            return VJson(v);
        });
        app.MapDelete("/api/valuation/venues/{id}", (string id, HttpContext ctx) => DeleteRecord(ctx, "venue-status", id));

        // ---- taxpayer access profile
        app.MapGet("/api/valuation/profile", (HttpContext ctx) => VJson(ProfileFor(ctx)));
        app.MapPost("/api/valuation/profile", async (HttpContext ctx) =>
        {
            var p = await JsonSerializer.DeserializeAsync<TaxpayerProfile>(ctx.Request.Body, ValuationStore.Json, ctx.RequestAborted) ?? new TaxpayerProfile();
            var user = DashboardAuth.UserName(ctx);
            VStore.Put("profile", "profile:" + user.ToLowerInvariant(), user, p, user);
            return VJson(p);
        });

        // ---- market snapshots
        app.MapGet("/api/valuation/snapshots", (HttpContext ctx) => VJson(new
        {
            orderBooks = VStore.List<OrderBookSnapshot>("orderbook", DashboardAuth.UserName(ctx)).Select(r => new { r.Owner, r.Value.Id, r.Value.Venue, r.Value.Market, r.Value.QuoteAsset, r.Value.ObservedUtc, levels = r.Value.Bids.Count, bidDepthOpt = r.Value.Bids.Sum(b => b.Quantity), r.Value.Truncated }),
            osmosisPools = VStore.List<OsmosisPoolSnapshot>("osmosis-pool", DashboardAuth.UserName(ctx)).Select(r => new { r.Owner, r.Value.Id, r.Value.PoolId, r.Value.PoolType, r.Value.ServedHeight, r.Value.BlockTimeUtc, r.Value.Exists, r.Value.CreatedHeight, ticks = r.Value.Ticks.Count, r.Value.TakerFee, r.Value.Problems }),
            conversions = Visible<QuoteConversion>(ctx, "conversion").GroupBy(c => (c.QuoteAsset, c.Source)).Select(g => new { asset = g.Key.QuoteAsset, source = g.Key.Source, from = g.Min(c => c.Date), to = g.Max(c => c.Date), days = g.Count() }),
        }));

        app.MapPost("/api/valuation/orderbooks/import", async (HttpContext ctx) =>
        {
            var owner = OwnerFor(ctx, ctx.Request.Query["scope"]);
            if (!CanWriteOwner(ctx, owner)) return VError("Only an admin can change shared evidence.", 403);
            string text;
            try { text = await BodyTextAsync(ctx); } catch (InvalidDataException ex) { return VError(ex.Message, 413); }
            var name = ctx.Request.Query["name"].ToString();
            var sha = VStore.ArchiveEvidence(string.IsNullOrWhiteSpace(name) ? "orderbook.csv" : name, Encoding.UTF8.GetBytes(text), null, DashboardAuth.UserName(ctx));
            var errors = new List<string>();
            var books = new Dictionary<string, OrderBookSnapshot>();
            foreach (var (row, f) in Csv.ParseWithHeader(text))
            {
                if (!DateTime.TryParse(f.GetValueOrDefault("observed_utc", ""), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var obs)) { errors.Add($"Row {row}: observed_utc is required."); continue; }
                var side = f.GetValueOrDefault("side", "bid").ToLowerInvariant();
                if (side is not ("bid" or "ask")) { errors.Add($"Row {row}: side must be bid or ask."); continue; }
                var price = Csv.Dec(f, "price", errors, row);
                var qty = Csv.Dec(f, "quantity", errors, row);
                if (price is null || qty is null || price <= 0 || qty < 0) { errors.Add($"Row {row}: price and quantity must be positive."); continue; }
                var quote = f.GetValueOrDefault("quote_asset", "");
                if (quote.Length == 0) { errors.Add($"Row {row}: quote_asset is required (USD, USDT and USDC are different assets)."); continue; }
                var venue = f.GetValueOrDefault("venue", "");
                var market = f.GetValueOrDefault("market", "");
                var key = $"{venue}|{market}|{obs:o}";
                if (!books.TryGetValue(key, out var b))
                    books[key] = b = new OrderBookSnapshot
                    {
                        Id = $"book:{venue}:{market}:{obs:yyyyMMddTHHmmssZ}", Venue = venue, Market = market,
                        BaseAsset = f.GetValueOrDefault("base_asset", "optio:uOPT"), QuoteAsset = quote, ObservedUtc = obs, CollectedUtc = DateTime.UtcNow,
                        Truncated = f.GetValueOrDefault("truncated", "").Equals("true", StringComparison.OrdinalIgnoreCase),
                        TakerFeeRate = Csv.Dec(f, "taker_fee_rate", errors, row) ?? 0,
                        Evidence = { new EvidenceRef { Kind = "file", FileName = name, Sha256 = sha, Description = "Imported order book", CollectedUtc = DateTime.UtcNow, EffectiveUtc = obs } },
                    };
                if (side == "bid") b.Bids.Add(new BookLevel(price.Value, qty.Value));
            }
            if (errors.Count > 0) return VJson(new { imported = 0, errors }, 400);
            foreach (var b in books.Values) VStore.Put("orderbook", b.Id, owner, b, DashboardAuth.UserName(ctx), b.ObservedUtc, sha);
            return VJson(new { imported = books.Count, sourceSha256 = sha });
        });

        app.MapPost("/api/valuation/conversions/import", async (HttpContext ctx) =>
        {
            var owner = OwnerFor(ctx, ctx.Request.Query["scope"]);
            if (!CanWriteOwner(ctx, owner)) return VError("Only an admin can change shared evidence.", 403);
            string text;
            try { text = await BodyTextAsync(ctx); } catch (InvalidDataException ex) { return VError(ex.Message, 413); }
            var errors = new List<string>();
            var list = new List<QuoteConversion>();
            foreach (var (row, f) in Csv.ParseWithHeader(text))
            {
                var asset = f.GetValueOrDefault("quote_asset", "");
                var source = f.GetValueOrDefault("source", "");
                var rate = Csv.Dec(f, "usd_per_unit", errors, row);
                if (asset.Length == 0 || asset == "USD") { errors.Add($"Row {row}: quote_asset is required and cannot be USD."); continue; }
                if (source.Length == 0) { errors.Add($"Row {row}: a documented source is required."); continue; }
                if (!DateOnly.TryParseExact(f.GetValueOrDefault("from_date", ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                    !DateOnly.TryParseExact(f.GetValueOrDefault("to_date", f.GetValueOrDefault("from_date", "")), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to < from)
                { errors.Add($"Row {row}: from_date/to_date must be yyyy-MM-dd."); continue; }
                if (rate is null || rate <= 0) continue;
                if (to.DayNumber - from.DayNumber > 3660) { errors.Add($"Row {row}: range too long."); continue; }
                for (var d = from; d <= to; d = d.AddDays(1)) list.Add(new QuoteConversion { QuoteAsset = asset, Date = d, UsdPerUnit = rate.Value, Source = source });
            }
            if (errors.Count > 0) return VJson(new { imported = 0, errors }, 400);
            foreach (var c in list) VStore.Put("conversion", $"{c.QuoteAsset}|{c.Date:yyyy-MM-dd}|{c.Source}", owner, c, DashboardAuth.UserName(ctx), c.Date.ToDateTime(TimeOnly.MinValue));
            return VJson(new { imported = list.Count });
        });

        app.MapDelete("/api/valuation/snapshots/{kind}/{id}", (string kind, string id, HttpContext ctx) =>
            kind is "orderbook" or "osmosis-pool" ? DeleteRecord(ctx, kind, id) : VError("Unknown snapshot kind."));

        // Historical Osmosis pool state at a verified height (read-only public queries).
        app.MapPost("/api/valuation/osmosis/fetch", async (HttpContext ctx) =>
        {
            var owner = OwnerFor(ctx, ctx.Request.Query["scope"]);
            if (!CanWriteOwner(ctx, owner)) return VError("Only an admin can change shared evidence.", 403);
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var poolIds = body.TryGetProperty("poolIds", out var pe) ? pe.EnumerateArray().Select(x => x.ToString()).Where(x => Regex.IsMatch(x, "^[0-9]{1,7}$")).Distinct().ToList() : new();
            if (poolIds.Count is 0 or > 12) return VError("Give 1 to 12 pool ids.");
            var user = DashboardAuth.UserName(ctx);
            var client = new OsmosisHistory(OsmosisHttp, LoadDashboardConfig().GetValueOrDefault("osmosisArchiveLcd"), (n, bytes, url) => VStore.ArchiveEvidence(n, bytes, url, user));
            long height;
            DateTime? blockTime = null;
            try
            {
                if (body.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) height = h.GetInt64();
                else if (body.TryGetProperty("atUtc", out var at) && DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var atUtc))
                    (height, blockTime) = await client.HeightAtOrBeforeAsync(atUtc, ctx.RequestAborted);
                else return VError("Give a block height or a UTC time.");
            }
            catch (HistoricalFetchException ex) { return VError(ex.Message, 502); }
            var results = new List<object>();
            foreach (var id in poolIds)
            {
                try
                {
                    var created = VStore.Get<long>("pool-created", "pool-created:" + id)?.Value;
                    var snap = await client.FetchPoolAsync(id, height, ctx.RequestAborted);
                    snap.CreatedHeight = created == 0 ? null : created;
                    VStore.Put("osmosis-pool", snap.Id, owner, snap, user, snap.BlockTimeUtc ?? blockTime);
                    results.Add(new { poolId = id, ok = true, snap.PoolType, snap.ServedHeight, snap.BlockTimeUtc, snap.Exists, ticks = snap.Ticks.Count, snap.Problems });
                }
                catch (HistoricalFetchException ex)
                {
                    results.Add(new { poolId = id, ok = false, error = ex.Message });
                }
            }
            return VJson(new { height, blockTime, results });
        });

        app.MapPost("/api/valuation/osmosis/creation", async (HttpContext ctx) =>
        {
            if (!DashboardAuth.IsAdmin(ctx)) return VError("Only an admin can change shared evidence.", 403);
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var id = body.TryGetProperty("poolId", out var p) ? p.ToString() : "";
            if (!Regex.IsMatch(id, "^[0-9]{1,7}$")) return VError("Give a pool id.");
            var client = new OsmosisHistory(OsmosisHttp, LoadDashboardConfig().GetValueOrDefault("osmosisArchiveLcd"));
            var h = await client.PoolCreationHeightAsync(id, ctx.RequestAborted);
            if (h is null) return VJson(new { poolId = id, createdHeight = (long?)null, note = "The node could not answer the pool_created search; creation height unknown (pool snapshots are still checked for existence at their height)." });
            VStore.Put("pool-created", "pool-created:" + id, ValuationStore.SystemOwner, h.Value, DashboardAuth.UserName(ctx));
            return VJson(new { poolId = id, createdHeight = h });
        });

        app.MapPost("/api/valuation/osmosis/validate", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var hash = body.TryGetProperty("txHash", out var t) ? t.GetString() ?? "" : "";
            if (!Regex.IsMatch(hash, "^[0-9A-Fa-f]{64}$")) return VError("Give a 64-character transaction hash.");
            var user = DashboardAuth.UserName(ctx);
            var client = new OsmosisHistory(OsmosisHttp, LoadDashboardConfig().GetValueOrDefault("osmosisArchiveLcd"), (n, bytes, url) => VStore.ArchiveEvidence(n, bytes, url, user));
            try
            {
                var v = await client.ValidateSwapAsync(hash.ToUpperInvariant(), ctx.RequestAborted);
                VStore.Put("swap-validation", "swap:" + hash.ToUpperInvariant(), user, v, user);
                return VJson(v);
            }
            catch (HistoricalFetchException ex) { return VError(ex.Message, 502); }
        });

        app.MapGet("/api/valuation/osmosis/validations", (HttpContext ctx) =>
            VJson(VStore.List<List<OsmosisHistory.SwapValidation>>("swap-validation", DashboardAuth.UserName(ctx)).SelectMany(r => r.Value)));

        // ---- analysis
        app.MapPost("/api/valuation/analyze", async (HttpContext ctx) =>
        {
            AnalyzeRequest? req;
            try { req = await JsonSerializer.DeserializeAsync<AnalyzeRequest>(ctx.Request.Body, ValuationStore.Json, ctx.RequestAborted); }
            catch (JsonException ex) { return VError(ex.Message); }
            if (req is null || !OptioAddress.IsMatch(req.Address.Trim().ToLowerInvariant())) return VError("Enter a valid optio1... wallet address.");
            if (!DateOnly.TryParse(req.From, CultureInfo.InvariantCulture, out var from) || !DateOnly.TryParse(req.To, CultureInfo.InvariantCulture, out var to) || to < from)
                return VError("Give a valid from/to date range.");
            if (to.DayNumber - from.DayNumber > 366) return VError("Analyze at most one year at a time.");
            var address = req.Address.Trim().ToLowerInvariant();
            var user = DashboardAuth.UserName(ctx);
            var receipts = (await ReceiptsForUserAsync(ctx, address, ctx.RequestAborted))
                .Where(r => { var d = DateOnly.FromDateTime(r.TimestampUtc); return d >= from && d <= to; })
                .Where(r => req.ReceiptIds is null || req.ReceiptIds.Contains(r.Id)).ToList();
            if (receipts.Count > 1500) return VError($"{receipts.Count} receipts in range; analyze a shorter range (at most 1500 receipts).");

            var policy = new SnapshotPolicy { MaxAge = TimeSpan.FromHours(Math.Clamp(req.MaxAgeHours, 0.1, 24 * 14)) };
            var books = Visible<OrderBookSnapshot>(ctx, "orderbook");
            var pools = Visible<OsmosisPoolSnapshot>(ctx, "osmosis-pool");
            var venues = Visible<VenueStatus>(ctx, "venue-status");
            var conversions = Visible<QuoteConversion>(ctx, "conversion");
            var profile = ProfileFor(ctx);
            var settings = new AnalysisSettings
            {
                Thresholds = (req.Thresholds is { Count: > 0 } ? req.Thresholds : new List<decimal> { 0.01m, 0.05m, 0.10m, 0.25m }).Where(t => t is > 0 and < 1).Distinct().Take(8).ToArray(),
                TransactionCostsUsd = req.TransactionCostsUsd,
                TransactionCostsBasis = req.TransactionCostsBasis,
                Increments = 100,
            };

            var results = new List<object>();
            var dayMarkets = new Dictionary<DateOnly, List<ISellMarket>>();
            foreach (var r in receipts)
            {
                ctx.RequestAborted.ThrowIfCancellationRequested();
                var (accessible, evidence, later) = CandidatesAt(r.TimestampUtc, policy, books, pools, venues, profile);
                var mine = ReceiptAnalyzer.Analyze(r, accessible, conversions, settings);
                var universe = ReceiptAnalyzer.Analyze(r, evidence, conversions, settings);
                if (later.Count > 0) mine.Notes.Add("Later data exists (corroboration only, not used): " + string.Join("; ", later.Take(4)));
                VStore.Put("analysis", "analysis:" + user.ToLowerInvariant() + ":" + r.Id, user, mine, user, r.TimestampUtc);
                results.Add(new { receiptId = r.Id, taxpayerAccessible = mine, marketEvidenceUniverse = universe });
                var day = DateOnly.FromDateTime(r.TimestampUtc);
                var fresh = accessible.Where(c => c.SnapshotProblem == null && c.Access.TaxpayerAccessible && DateOnly.FromDateTime(c.Market.ObservedUtc) == day).Select(c => c.Market).ToList();
                if (fresh.Count > 0 && !dayMarkets.ContainsKey(day)) dayMarkets[day] = fresh;
            }

            object? stress = null;
            object? scenarios = null;
            if (req.Stress && receipts.Count > 0)
            {
                var sales = receipts.GroupBy(r => DateOnly.FromDateTime(r.TimestampUtc)).Select(g => (g.Key, g.Sum(r => r.QuantityOpt))).ToList();
                stress = StressSimulator.Run(sales, d => dayMarkets.GetValueOrDefault(d), null, carryUnsold: true);
                // Network-selling scenarios: each in its own simulation state, never the state used for the receipts.
                var periods = Visible<NetworkDistributionPeriod>(ctx, "distribution");
                var basis = Enum.TryParse<DistributionBasis>(req.ScenarioBasis, true, out var b) ? b : DistributionBasis.BestAvailable;
                var fractions = (req.ScenarioFractions is { Count: > 0 } ? req.ScenarioFractions : ValuationMath.DefaultScenarioFractions.ToList()).Where(f => f is > 0 and <= 1).Distinct().Take(6).ToList();
                if (to.DayNumber - from.DayNumber <= 62)
                    scenarios = fractions.Select(f =>
                    {
                        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => from.AddDays(i))
                            .Select(d => (d, DistributionSchedule.Resolve(periods, d, RewardCategory.Node, basis))).ToList();
                        var missing = days.Count(x => x.Item2.OptPerDay is null);
                        var run = StressSimulator.Run(days.Where(x => x.Item2.OptPerDay != null).Select(x => (x.d, x.Item2.OptPerDay!.Value * f)).ToList(),
                            d => dayMarkets.GetValueOrDefault(d)?.Select(m => m.Clone()).ToList(), null, carryUnsold: false);
                        return new { fraction = f, label = DistributionSchedule.ScenarioLabel, daysWithoutNetworkAmount = missing, result = run };
                    }).ToList();
            }

            return VJson(new
            {
                analyzed = results.Count,
                settings = new { maxAgeHours = policy.MaxAge.TotalHours, settings.Thresholds, profile },
                note = "Per-receipt results are independent counterfactuals and must not be added up as proceeds. The cumulative stress view and network scenarios are separate simulations.",
                disclaimer = ValuationDisclaimer,
                results,
                stress,
                scenarios,
            });
        });

        // Distribution metrics and hypothetical network-selling scenarios for a period.
        app.MapGet("/api/valuation/scenarios", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            var range = RangeParams(ctx);
            if (address is null || range is null) return VError("Give an address and a year or from/to range.");
            var receipts = await ReceiptsForUserAsync(ctx, address, ctx.RequestAborted);
            var periods = Visible<NetworkDistributionPeriod>(ctx, "distribution");
            var basis = Enum.TryParse<DistributionBasis>(ctx.Request.Query["basis"], true, out var b) ? b : DistributionBasis.BestAvailable;
            var fractions = ctx.Request.Query["fractions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / 100m : -1).Where(v => v is > 0 and <= 1).ToList();
            if (fractions.Count == 0) fractions = ValuationMath.DefaultScenarioFractions.ToList();
            var zone = ctx.Request.Query["dayBasis"] == "reporting" ? ReportingZone() : TimeZoneInfo.Utc;
            var inRange = receipts.Where(r => { var d = DistributionSchedule.DayOf(r.TimestampUtc, zone); return d >= range.Value.From && d <= range.Value.To; }).ToList();
            var priced = inRange.Where(r => r.Reference.UnitPriceUsd != null).ToList();
            decimal? avgRef = priced.Count == 0 ? null : priced.Sum(r => r.Reference.UnitPriceUsd!.Value * r.QuantityOpt) / priced.Sum(r => r.QuantityOpt);
            var m = DistributionSchedule.Metrics(receipts, periods, range.Value.From, range.Value.To, zone, basis, fractions, avgRef);
            var supply = Visible<SupplyObservation>(ctx, "supply").Where(s => s.TotalSupplyOpt != null).OrderBy(s => s.AtUtc).ToList();
            var startSupply = supply.LastOrDefault(s => DateOnly.FromDateTime(s.AtUtc) < range.Value.From && (range.Value.From.ToDateTime(TimeOnly.MinValue) - s.AtUtc).TotalDays <= 3);
            var mintPeriods = periods.Where(p => p.EffectiveFrom >= range.Value.From && p.EffectiveTo <= range.Value.To).ToList();
            var minted = mintPeriods.Count > 0 && mintPeriods.All(p => p.MintedOpt != null) ? mintPeriods.Sum(p => p.MintedOpt!.Value) : (decimal?)null;
            var burned = mintPeriods.Count > 0 && mintPeriods.All(p => p.BurnedOpt != null) ? mintPeriods.Sum(p => p.BurnedOpt!.Value) : (decimal?)null;
            var netMinted = ValuationMath.NetMinted(minted, burned);
            var growth = ValuationMath.NetMintSupplyGrowth(netMinted.Value, startSupply?.TotalSupplyOpt);
            var daily = Enumerable.Range(0, range.Value.To.DayNumber - range.Value.From.DayNumber + 1).Select(i => range.Value.From.AddDays(i)).Select(d => new
            {
                date = d,
                personalNodeOpt = inRange.Where(r => DistributionSchedule.DayOf(r.TimestampUtc, zone) == d && r.Category == RewardCategory.Node).Sum(r => r.QuantityOpt),
                personalStakingOpt = inRange.Where(r => DistributionSchedule.DayOf(r.TimestampUtc, zone) == d && r.Category == RewardCategory.Staking).Sum(r => r.QuantityOpt),
                node = DistributionSchedule.Resolve(periods, d, RewardCategory.Node, basis),
                combined = DistributionSchedule.Resolve(periods, d, RewardCategory.Combined, DistributionBasis.Observed),
                emission = DistributionSchedule.ResolveEmission(periods, d),
            }).ToList();
            return VJson(new
            {
                metrics = m,
                averageReferencePriceUsd = avgRef,
                referenceNotionalNote = "Reference notional = OPT × average reference price of your receipts in the period. It is not the cash needed to absorb the sale, and a liquidity snapshot is capacity at an instant, not a daily flow.",
                supplyGrowth = new { netMinted, growth, startSupply, note = "Supply growth is not assumed to cause a matching price decline. Expected distributions may already be reflected in the reference price; applying another discount for them could double count." },
                daily,
                disclaimer = ValuationDisclaimer,
            });
        });

        // ---- reviews
        app.MapGet("/api/valuation/reviews", (HttpContext ctx) =>
        {
            var all = VStore.Reviews(DashboardAuth.UserName(ctx));
            return VJson(new { current = ValuationReviews.Current(all), history = all });
        });

        app.MapPost("/api/valuation/reviews", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null) return VError("Give the wallet address.");
            ValuationReview? draft;
            try { draft = await JsonSerializer.DeserializeAsync<ValuationReview>(ctx.Request.Body, ValuationStore.Json, ctx.RequestAborted); }
            catch (JsonException ex) { return VError(ex.Message); }
            if (draft is null) return VError("Missing review.");
            var user = DashboardAuth.UserName(ctx);
            var receipts = (await ReceiptsForUserAsync(ctx, address, ctx.RequestAborted)).ToDictionary(r => r.Id);
            draft.Reviewer = user;
            draft.ReferenceValueUsdAtReview = draft.ReceiptIds.All(id => receipts.TryGetValue(id, out var r) && r.Reference.ValueUsd != null) ? draft.ReceiptIds.Sum(id => receipts[id].Reference.ValueUsd!.Value) : null;
            var errors = ValuationReviews.Validate(draft, receipts);
            if (errors.Count > 0) return VJson(new { errors }, 400);
            var existing = VStore.Reviews(user);
            if (!string.IsNullOrEmpty(draft.ReviewId) && !existing.Any(v => v.ReviewId == draft.ReviewId)) return VError("Unknown review id.");
            var next = ValuationReviews.NextVersion(existing, draft, ReviewStatus.Proposed, user, DateTime.UtcNow);
            VStore.AppendReview(user, next);
            return VJson(next);
        });

        // Approval is its own explicit action, with a confirmation flag, never a side effect of proposing or analyzing.
        app.MapPost("/api/valuation/reviews/{id}/{action}", async (string id, string action, HttpContext ctx) =>
        {
            if (action is not ("approve" or "withdraw")) return VError("Unknown action.", 404);
            var address = AddressParam(ctx);
            if (address is null) return VError("Give the wallet address.");
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (action == "approve" && !(body.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True))
                return VError("Approval needs explicit confirmation.");
            var user = DashboardAuth.UserName(ctx);
            var existing = VStore.Reviews(user);
            var current = ValuationReviews.Current(existing).FirstOrDefault(v => v.ReviewId == id);
            if (current is null) return VError("Unknown review.", 404);
            if (action == "approve")
            {
                var receipts = (await ReceiptsForUserAsync(ctx, address, ctx.RequestAborted)).ToDictionary(r => r.Id);
                var errors = ValuationReviews.Validate(current, receipts);
                if (errors.Count > 0) return VJson(new { errors }, 400);
            }
            var next = ValuationReviews.NextVersion(existing, current, action == "approve" ? ReviewStatus.Approved : ReviewStatus.Withdrawn, user, DateTime.UtcNow);
            if (body.TryGetProperty("note", out var note) && note.GetString() is { Length: > 0 } n) next.Explanation += $"\n[{action} {DateTime.UtcNow:yyyy-MM-dd}] {n}";
            VStore.AppendReview(user, next);
            return VJson(next);
        });

        // Approved values for the Tax Report page, by its ledger key (one ledger entry can hold several receipts).
        app.MapGet("/api/valuation/selected", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null) return VError("Enter a valid optio1... wallet address.");
            var receipts = await ReceiptsForUserAsync(ctx, address, ctx.RequestAborted);
            var selected = ValuationReviews.Selected(VStore.Reviews(DashboardAuth.UserName(ctx)), receipts.ToDictionary(r => r.Id));
            var entries = receipts.Where(r => r.TxHash != null && r.Sender != null)
                .GroupBy(r => (Hash: r.TxHash!, Sender: r.Sender!))
                .ToDictionary(g => $"{g.Key.Hash}|in|{g.Key.Sender}", g => new
                {
                    receipts = g.Count(),
                    resolved = g.Count(r => selected.ContainsKey(r.Id)),
                    valueUsd = g.All(r => selected.ContainsKey(r.Id)) ? g.Sum(r => selected[r.Id].ValueUsd) : (decimal?)null,
                    reviews = g.Where(r => selected.ContainsKey(r.Id)).Select(r => $"{selected[r.Id].ReviewId[..8]} v{selected[r.Id].Version} {selected[r.Id].Method}").Distinct(),
                });
            return VJson(new { address, entries, caseInsensitiveHash = true, coverage = ValuationReviews.Coverage(receipts, selected) });
        });

        // ---- reviewed report versions (append-only)
        app.MapPost("/api/valuation/report", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null || !int.TryParse(ctx.Request.Query["year"], out var year)) return VError("Give the address and year.");
            // The selected wallet plus the user's other saved wallets, so moves between them keep their lots.
            var wallets = new Dictionary<string, (List<RewardReceipt>, WalletLedger)>(StringComparer.Ordinal);
            foreach (var w in new[] { address }.Concat(OwnWallets(ctx).Where(w => w != address)))
                wallets[w] = (await ReceiptsForUserAsync(ctx, w, ctx.RequestAborted), await GetWalletLedgerAsync(w, ctx.RequestAborted));
            var report = BuildReviewedReport(ctx, address, year, wallets);
            var json = JsonSerializer.SerializeToElement(report, ValuationStore.Json);
            var id = VStore.AddReportVersion(DashboardAuth.UserName(ctx), address, year, json.GetProperty("fingerprint").GetString()!, json.GetProperty("status").GetString()!, report);
            return VJson(new { id, report });
        });
        app.MapGet("/api/valuation/report/versions", (HttpContext ctx) => VJson(VStore.ReportVersions(DashboardAuth.UserName(ctx), AddressParam(ctx))));
        app.MapGet("/api/valuation/report/{id}", (string id, HttpContext ctx) =>
        {
            var json = VStore.ReportVersionJson(DashboardAuth.UserName(ctx), id);
            return json is null ? VError("Not found.", 404) : Results.Text(json, "application/json");
        });

        // ---- exports (internal: reference prices derived from CoinMarketCap stay within the user's reports)
        app.MapGet("/api/valuation/export/receipts.csv", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null) return VError("Enter a valid optio1... wallet address.");
            var range = RangeParams(ctx);
            var receipts = await ReceiptsForUserAsync(ctx, address, ctx.RequestAborted);
            var selected = ValuationReviews.Selected(VStore.Reviews(DashboardAuth.UserName(ctx)), receipts.ToDictionary(r => r.Id));
            var analyses = VStore.List<ReceiptAnalysis>("analysis", DashboardAuth.UserName(ctx)).Where(a => a.Owner != ValuationStore.SystemOwner).ToDictionary(a => a.Value.ReceiptId, a => a.Value);
            var zone = ReportingZone();
            var sb = new StringBuilder();
            sb.AppendLine(Csv.Line("receipt_id", "identity_basis", "wallet", "tx_hash", "event_index", "timestamp_utc", "reporting_day", "category", "classification", "classification_basis", "quantity_base_units", "quantity_opt",
                "reference_unit_price_usd", "reference_value_usd", "reference_status", "price_provider", "price_interval", "price_observed_utc",
                "analysis_outcome", "modeled_executable_opt", "residual_opt", "modeled_output_usd", "execution_shortfall", "usd_incomplete",
                "selected_value_usd", "review", "review_method"));
            foreach (var r in receipts.Where(r => range is null || (DateOnly.FromDateTime(r.TimestampUtc) >= range.Value.From && DateOnly.FromDateTime(r.TimestampUtc) <= range.Value.To)))
            {
                analyses.TryGetValue(r.Id, out var a);
                selected.TryGetValue(r.Id, out var s);
                sb.AppendLine(Csv.Line(r.Id, r.IdentityBasis, r.Wallet, r.TxHash, r.EventIndex, r.TimestampUtc, DistributionSchedule.DayOf(r.TimestampUtc, zone), r.Category, r.Classification, r.ClassificationBasis, r.BaseUnits, r.QuantityOpt,
                    r.Reference.UnitPriceUsd, r.Reference.ValueUsd, r.Reference.Status, r.Reference.Source?.Provider, r.Reference.Source?.Interval, r.Reference.Source?.ObservedUtc,
                    a?.Outcome, a?.ExecutableOpt, a?.ResidualOpt, a?.ModeledOutputUsd, a?.ExecutionShortfall, a?.UsdIncomplete,
                    s?.ValueUsd, s is null ? null : $"{s.ReviewId} v{s.Version}", s?.Method));
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"opt-receipts-{address[..12]}.csv");
        });

        app.MapGet("/api/valuation/export/distributions.csv", (HttpContext ctx) =>
        {
            var sb = new StringBuilder();
            sb.AppendLine(DistributionSchedule.CsvHeader + ",owner,origin,id");
            foreach (var r in VStore.List<NetworkDistributionPeriod>("distribution", DashboardAuth.UserName(ctx)))
            {
                var p = r.Value;
                var ev = p.Evidence.FirstOrDefault(e => e.Url != null);
                sb.AppendLine(Csv.Line(p.EffectiveFrom, p.EffectiveTo, p.Category, p.ScheduledOptPerDay, p.ObservedOptPerDay, p.UserAssumedOptPerDay, p.MintedOpt, p.BurnedOpt, p.ReserveReleaseOpt, p.UnlockOpt,
                    p.RewardEpoch, p.PaymentUtc, p.Verification, ev?.Url, ev?.Description, p.Notes, p.EmittedOptPerDay, p.EmissionVerification, r.Owner, p.Origin, p.Id));
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "opt-network-distributions.csv");
        });

        app.MapGet("/api/valuation/export/manifest.json", (HttpContext ctx) =>
        {
            var user = DashboardAuth.UserName(ctx);
            return VJson(new
            {
                generatedUtc = DateTime.UtcNow,
                note = "SHA-256 hashes show a file is unchanged since capture; they do not show its contents are true.",
                chainSource = new { repository = "OptioNetwork/optio", tags = new[] { "v2.0.0 (9e7becb, v2-distro)", "v3.0.0 (8795b3c, v3-lockup)" }, distributionAccount = DistributionSourceAddress },
                osmosisMath = new { repository = "osmosis-labs/osmosis", commit = "e4ae1fcd", modules = new[] { "x/concentrated-liquidity", "x/gamm/pool-models/balancer", "x/poolmanager (taker fee)" } },
                parsers = new[] { DistributionSchedule.ParserVersion, OsmosisHistory.ParserVersion, "receipts-csv/1" },
                evidenceFiles = VStore.EvidenceFiles().Select(e => new { e.Sha256, e.Name, e.Bytes, e.SourceUrl, e.CollectedUtc }),
                distributions = VStore.List<NetworkDistributionPeriod>("distribution", user).Select(r => new { r.Value.Id, r.Owner, r.Value.EffectiveFrom, r.Value.EffectiveTo, r.Value.Category, r.Value.Verification, r.Value.Evidence }),
                osmosisSnapshots = VStore.List<OsmosisPoolSnapshot>("osmosis-pool", user).Select(r => new { r.Value.Id, r.Value.PoolId, r.Value.ServedHeight, r.Value.BlockTimeUtc, r.Value.CollectedUtc, r.Value.Evidence, r.Value.MathVersion }),
                orderBooks = VStore.List<OrderBookSnapshot>("orderbook", user).Select(r => new { r.Value.Id, r.Value.Venue, r.Value.ObservedUtc, r.Value.CollectedUtc, r.Value.Evidence }),
                venueStatuses = Visible<VenueStatus>(ctx, "venue-status"),
                reviews = VStore.Reviews(user),
                reportVersions = VStore.ReportVersions(user),
                liquidityCaptures = LiquidityEvidenceService.Latest(),
            });
        });

        app.MapGet("/api/valuation/templates/{name}", (string name) =>
        {
            var (header, sample) = name switch
            {
                "distributions.csv" => (DistributionSchedule.CsvHeader,
                    "2025-01-01,2025-01-31,combined,,,,,,,,,,unverified,,User-reported emission; distribution not yet observed,SYNTHETIC EXAMPLE - replace with your own evidence,60000000,userreported"),
                "receipts.csv" => (ReceiptIdentity.CsvHeader,
                    "optio1exampleexampleexampleexampleexampleexam,2025-03-01T14:00:00Z,,,node,inferred,35000000000,uOPT,6,,0.01,CoinMarketCap,,quote.close,1h,2025-03-01T14:00:00Z,"),
                "orderbook.csv" => ("venue,market,base_asset,quote_asset,observed_utc,side,price,quantity,truncated,taker_fee_rate",
                    "ExampleExchange,OPT/USDT,optio:uOPT,USDT,2025-03-01T14:00:00Z,bid,0.0100,20000,true,0.002"),
                "valuations.csv" => (ValuationImportHeader,
                    ",SYNTHETIC0001,12,95.40,illiquid-market,proposed,Example Valuation LLC,2026-02-01,SYNTHETIC EXAMPLE: replace with the valuation report's conclusion and reasoning,https://example.invalid/report.pdf,Valuation report section 4"),
                "conversions.csv" => ("quote_asset,from_date,to_date,usd_per_unit,source",
                    "USDT,2025-01-01,2025-01-31,1.0000,SYNTHETIC EXAMPLE - cite a historical rate source"),
                _ => (null, null),
            };
            return header is null ? Results.NotFound() : Results.File(Encoding.UTF8.GetBytes(header + "\n" + sample + "\n"), "text/csv", name);
        });
    }

    static IResult DeleteRecord(HttpContext ctx, string kind, string id)
    {
        var rec = VStore.Get<JsonElement>(kind, id);
        if (rec is null || !ValuationStore.CanRead(DashboardAuth.UserName(ctx), rec.Owner)) return VError("Not found.", 404);
        if (!CanWriteOwner(ctx, rec.Owner)) return VError("Only an admin can change shared evidence.", 403);
        VStore.Delete(kind, id, DashboardAuth.UserName(ctx));
        return VJson(new { deleted = id, note = "Marked deleted; the record is kept in history." });
    }
}
