using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OPTIC.Services.Valuation;

/// <summary>
/// Stable receipt ids. On-chain receipts are identified by tx hash plus the transfer event and its position, so two
/// distinct payments in one tx stay distinct. Without a tx hash the source file's hash and row number identify the
/// receipt, so receipts without hashes are never dropped. Re-importing the same row gives the same id (deduplicated).
/// </summary>
public static class ReceiptIdentity
{
    public static string ForChainEvent(string chain, string txHash, int eventIndex, int pairIndex, string recipient, string denom) =>
        $"{chain}:{txHash.ToUpperInvariant()}:e{eventIndex}.{pairIndex}:{recipient.ToLowerInvariant()}:{denom}";

    public static string ForSourceRow(string sourceSha256, int row) =>
        $"file:{sourceSha256[..Math.Min(16, sourceSha256.Length)].ToLowerInvariant()}:r{row.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Tx hash present but no event position (e.g. an exported CSV): the n-th payment of the same amount to the same
    /// recipient in that tx, so identical events are kept apart while a re-import maps to the same ids.
    /// </summary>
    public static string ForTxOrdinal(string chain, string txHash, string recipient, decimal baseUnits, int ordinal) =>
        $"{chain}:{txHash.ToUpperInvariant()}:n{ordinal.ToString(CultureInfo.InvariantCulture)}:{recipient.ToLowerInvariant()}:{baseUnits.ToString(CultureInfo.InvariantCulture)}";

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>The tax report's ledger key for a receipt (one ledger entry may hold several receipts).</summary>
    public static string LedgerKey(string txHash, string sender) => $"{txHash}|in|{sender.ToLowerInvariant()}";

    public const string CsvHeader = "wallet,timestamp_utc,tx_hash,event_index,category,classification,quantity_base_units,denom,decimals,sender,reference_unit_price_usd,price_provider,price_endpoint,price_field,price_interval,price_observed_utc,price_fallback";

    /// <summary>
    /// Imports receipts from CSV. Rows with a tx hash and event index use the chain identity; rows with a tx hash only
    /// use the per-tx ordinal; rows without a hash use the source file row. Exact duplicates within the file are reported.
    /// </summary>
    public static (List<RewardReceipt> Receipts, List<string> Errors, int Duplicates) ImportCsv(string text, string sourceName, string chain = "optio")
    {
        var sha = Sha256Hex(text);
        var receipts = new List<RewardReceipt>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicates = 0;
        foreach (var (row, f) in Csv.ParseWithHeader(text))
        {
            var before = errors.Count;
            var wallet = f.GetValueOrDefault("wallet", "").ToLowerInvariant();
            if (wallet.Length == 0) errors.Add($"Row {row}: wallet is required.");
            if (!DateTime.TryParse(f.GetValueOrDefault("timestamp_utc", ""), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts))
                errors.Add($"Row {row}: timestamp_utc is not a date-time.");
            var baseUnits = Csv.Dec(f, "quantity_base_units", errors, row);
            if (baseUnits is null) errors.Add($"Row {row}: quantity_base_units is required.");
            else if (baseUnits <= 0 || baseUnits != decimal.Truncate(baseUnits.Value)) errors.Add($"Row {row}: quantity_base_units must be a positive whole number of base units.");
            var decimals = 6;
            if (f.TryGetValue("decimals", out var dec) && dec.Length > 0 && !int.TryParse(dec, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimals))
                errors.Add($"Row {row}: decimals is not a whole number.");
            var denom = f.GetValueOrDefault("denom", "");
            if (denom.Length == 0) denom = "uOPT";
            var catText = f.GetValueOrDefault("category", "");
            var category = RewardCategory.Unclassified;
            if (catText.Length > 0 && !Enum.TryParse(catText, true, out category)) errors.Add($"Row {row}: category must be node, staking, other or unclassified.");
            var clsText = f.GetValueOrDefault("classification", "");
            var classification = catText.Length == 0 ? ClassificationStatus.Uncertain : ClassificationStatus.Inferred;
            if (clsText.Length > 0 && !Enum.TryParse(clsText, true, out classification)) errors.Add($"Row {row}: classification must be confirmed, inferred or uncertain.");
            var price = Csv.Dec(f, "reference_unit_price_usd", errors, row);
            DateTime? observed = null;
            if (f.TryGetValue("price_observed_utc", out var po) && po.Length > 0)
            {
                if (DateTime.TryParse(po, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p)) observed = p;
                else errors.Add($"Row {row}: price_observed_utc is not a date-time.");
            }
            if (errors.Count > before) continue;

            var hash = f.GetValueOrDefault("tx_hash", "");
            var evText = f.GetValueOrDefault("event_index", "");
            string id, basis;
            int? eventIndex = null;
            if (hash.Length > 0 && int.TryParse(evText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ev))
            {
                eventIndex = ev;
                id = ForChainEvent(chain, hash, ev, 0, wallet, denom);
                basis = "chain-event";
            }
            else if (hash.Length > 0)
            {
                var k = $"{hash.ToUpperInvariant()}|{wallet}|{baseUnits}";
                var n = ordinals.GetValueOrDefault(k);
                ordinals[k] = n + 1;
                id = ForTxOrdinal(chain, hash, wallet, baseUnits!.Value, n);
                basis = "chain-tx-ordinal";
            }
            else
            {
                id = ForSourceRow(sha, row);
                basis = "source-row";
            }
            if (!seen.Add(id)) { duplicates++; continue; }

            receipts.Add(new RewardReceipt
            {
                Id = id, IdentityBasis = basis, Wallet = wallet, Chain = chain,
                TxHash = hash.Length > 0 ? hash.ToUpperInvariant() : null, EventIndex = eventIndex,
                SourceFile = sourceName, SourceRow = row,
                TimestampUtc = DateTime.SpecifyKind(ts, DateTimeKind.Utc),
                Category = category, Classification = classification,
                Denom = denom, Decimals = decimals, BaseUnits = baseUnits!.Value,
                Sender = NullIfEmpty(f.GetValueOrDefault("sender", "")),
                Reference = new ReferenceValuation
                {
                    UnitPriceUsd = price,
                    Status = price is null ? "unpriced" : f.GetValueOrDefault("price_fallback", "").Length > 0 ? "fallback" : "priced",
                    Source = price is null ? null : new PriceSourceInfo
                    {
                        Provider = f.GetValueOrDefault("price_provider", ""), Endpoint = NullIfEmpty(f.GetValueOrDefault("price_endpoint", "")),
                        Field = NullIfEmpty(f.GetValueOrDefault("price_field", "")), Interval = NullIfEmpty(f.GetValueOrDefault("price_interval", "")),
                        ObservedUtc = observed, Fallback = NullIfEmpty(f.GetValueOrDefault("price_fallback", "")),
                    },
                },
            });
            var r = receipts[^1];
            r.Reference.ValueUsd = ValuationMath.ReferenceValueUsd(r.QuantityOpt, price);
        }
        return (receipts, errors, duplicates);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
