using System.Globalization;
using System.Numerics;
using System.Text.Json;

// Likely MEXC deposits. MEXC gives each customer an own deposit address on Optio and sweeps it to a collector wallet a
// few minutes later. Every bank send on the chain is kept in odata/sends.jsonl (refreshed hourly, new blocks only);
// deposit addresses are the ones that forward nearly everything they receive to the collector within minutes, and the
// wallets that paid into them are counted as sending OPT to MEXC. The collector was found by this pattern (233 deposit
// addresses swept a median 15 blocks after receiving); nothing on chain says it is MEXC, hence "likely".
public static partial class WebDashboard
{
    static readonly string[] MexcCollectors = { "optio1yj9d0e23djwt03vfl4sgescntrtpdj0tdyrsjc" };
    const int MexcMaxSweepLagBlocks = 120;
    const string FeeCollector = "optio17xpfvakm2amg962yls6f84z3kell8c5l9yn0qn";

    sealed record SendRow(long H, string S, string R, decimal Opt);

    sealed class SweepDetection
    {
        public HashSet<string> DepositAddresses { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (decimal Sent, int Deposits, long FirstH, long LastH)> Customers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, decimal> Back { get; } = new(StringComparer.Ordinal);
        public long ScannedToHeight { get; set; }
        public DateTime AtUtc { get; set; }
    }

    static List<SendRow>? _sends;
    static long _sendsScannedTo;
    static SweepDetection? _sweeps;
    static readonly SemaphoreSlim SweepsGate = new(1, 1);
    static int _sweepLoopStarted;

    static string SendsPath => Path.Combine(Environment.CurrentDirectory, "odata", "sends.jsonl");
    static string SendsMetaPath => Path.Combine(Environment.CurrentDirectory, "odata", "sends-meta.json");

    static void LoadSends()
    {
        if (_sends is not null) return;
        var list = new List<SendRow>();
        long max = 0;
        if (File.Exists(SendsPath))
            foreach (var line in File.ReadLines(SendsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var d = JsonDocument.Parse(line);
                var r = d.RootElement;
                var row = new SendRow(r.GetProperty("h").GetInt64(), r.GetProperty("s").GetString()!, r.GetProperty("r").GetString()!, r.GetProperty("opt").GetDecimal());
                list.Add(row);
                if (row.H > max) max = row.H;
            }
        long meta = 0;
        try { if (File.Exists(SendsMetaPath)) meta = JsonDocument.Parse(File.ReadAllText(SendsMetaPath)).RootElement.GetProperty("scannedToHeight").GetInt64(); } catch (JsonException) { }
        _sends = list;
        _sendsScannedTo = Math.Max(meta, max);
    }

    // Reads bank sends newer than the last scan and appends them to the file.
    static async Task RefreshSendsAsync(CancellationToken ct)
    {
        LoadSends();
        using var rpc = CreateRpcContext();
        var head = await GetLatestHeightAsync(rpc.Http, ct);
        if (head <= _sendsScannedTo) return;
        var added = new List<SendRow>();
        var lines = new List<string>();
        await SearchTxsAsync(rpc.Http, $"message.action='/cosmos.bank.v1beta1.MsgSend' AND tx.height>{_sendsScannedTo}", (tx, hash, height) =>
        {
            if (height > head) return;
            foreach (var t in GetTxTransfers(tx, rpc.DenomWantedUpper))
            {
                if (t.Recipient == FeeCollector || t.AmountUnits <= 0) continue;
                var opt = ToDecimalOpt(t.AmountUnits, rpc.ScaleInt);
                added.Add(new SendRow(height, t.Sender, t.Recipient, opt));
                lines.Add(JsonSerializer.Serialize(new { h = height, tx = hash, s = t.Sender, r = t.Recipient, opt }));
            }
        }, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(SendsPath)!);
        if (lines.Count > 0) await File.AppendAllLinesAsync(SendsPath, lines, ct);
        _sends!.AddRange(added);
        _sendsScannedTo = head;
        await File.WriteAllTextAsync(SendsMetaPath, JsonSerializer.Serialize(new { scannedToHeight = head, updatedUtc = DateTime.UtcNow }), ct);
    }

    static SweepDetection DetectSweeps()
    {
        var collectors = new HashSet<string>(MexcCollectors, StringComparer.Ordinal);
        var ins = new Dictionary<string, List<SendRow>>(StringComparer.Ordinal);
        var outs = new Dictionary<string, List<SendRow>>(StringComparer.Ordinal);
        foreach (var x in _sends!)
        {
            (outs.TryGetValue(x.S, out var o) ? o : outs[x.S] = new()).Add(x);
            (ins.TryGetValue(x.R, out var i) ? i : ins[x.R] = new()).Add(x);
        }
        var det = new SweepDetection { ScannedToHeight = _sendsScannedTo, AtUtc = DateTime.UtcNow };
        foreach (var (addr, sent) in outs)
        {
            if (collectors.Contains(addr) || !ins.TryGetValue(addr, out var got)) continue;
            var outOpt = sent.Sum(x => x.Opt);
            var toCollector = sent.Where(x => collectors.Contains(x.R)).ToList();
            if (toCollector.Count == 0 || outOpt <= 0) continue;
            var inOpt = got.Sum(x => x.Opt);
            if (toCollector.Sum(x => x.Opt) / outOpt < 0.9m || outOpt / inOpt < 0.8m || sent.Select(x => x.R).Distinct().Count() > 2) continue;
            // Swept within minutes of the first deposit, like an exchange's deposit system (a slow treasury top-up is not).
            if (toCollector.Min(x => x.H) - got.Min(x => x.H) > MexcMaxSweepLagBlocks) continue;
            det.DepositAddresses.Add(addr);
        }
        var network = new HashSet<string>(collectors.Concat(det.DepositAddresses), StringComparer.Ordinal);
        foreach (var d in det.DepositAddresses)
            foreach (var x in ins[d])
            {
                if (network.Contains(x.S) || ModuleAccounts.ContainsKey(x.S)) continue;
                det.Customers.TryGetValue(x.S, out var c);
                det.Customers[x.S] = (c.Sent + x.Opt, c.Deposits + 1, c.FirstH == 0 ? x.H : Math.Min(c.FirstH, x.H), Math.Max(c.LastH, x.H));
            }
        // What came back to those wallets from the collector or a deposit address (withdrawals may use other wallets).
        foreach (var src in network)
            if (outs.TryGetValue(src, out var o))
                foreach (var x in o)
                    if (det.Customers.ContainsKey(x.R)) det.Back[x.R] = det.Back.GetValueOrDefault(x.R) + x.Opt;
        return det;
    }

    static async Task<SweepDetection> GetSweepsAsync(bool refresh, CancellationToken ct)
    {
        await SweepsGate.WaitAsync(ct);
        try
        {
            if (_sweeps is null || refresh)
            {
                try { await RefreshSendsAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException && _sends is not null) { Console.WriteLine("Sends refresh failed: " + ex.Message); }
                _sweeps = DetectSweeps();
            }
            return _sweeps;
        }
        finally { SweepsGate.Release(); }
    }

    // Keeps the sends index and the deposit-address list current: new blocks are read every hour.
    static void StartSweepLoop()
    {
        if (Interlocked.Exchange(ref _sweepLoopStarted, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var before = _sweeps?.DepositAddresses.Count ?? -1;
                    await GetSweepsAsync(refresh: true, CancellationToken.None);
                    if (_sweeps!.DepositAddresses.Count != before) _exchangeFlows = null;
                    else if (_exchangeFlows is not null && DateTime.UtcNow - _exchangeFlowsAt > TimeSpan.FromHours(1)) _exchangeFlows = null;
                }
                catch (Exception ex) { Console.WriteLine("Sweep detection failed: " + ex.Message); }
                await Task.Delay(TimeSpan.FromHours(1));
            }
        });
    }

    // Shared names for the detected deposit addresses (explicit entries in the shared list take precedence).
    static Dictionary<string, object> SweepDepositNames()
    {
        var d = _sweeps;
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        if (d is null) return map;
        foreach (var a in d.DepositAddresses)
            map[a] = new { name = "Likely MEXC · deposit address", source = $"OPTIC sweep detection: forwards its OPT to {MexcCollectors[0]} within minutes (auto, to block {d.ScannedToHeight})" };
        return map;
    }
}
