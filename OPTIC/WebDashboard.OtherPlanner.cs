// Sell Planner for assets other than OPT (TITN, TGT, ETH): lots from the TGT/TITN/ETH engine (/api/titn/lots), used
// highest cost first per wallet like the tax report, valued at today's price; how much to sell for a target loss and the
// largest loss available. Choosing OPT shows the existing OPT planner below instead.
public static partial class WebDashboard
{
    static void AppendOtherAssetPlannerCard(System.Text.StringBuilder sb) => sb.Append(OtherAssetPlannerHtml);

    const string OtherAssetPlannerHtml = """
        <div class="card" style="grid-column: 1 / -1;" id="op-card">
            <h2>Asset to plan</h2>
            <div class="card-content">
                <div style="display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end;">
                    <div class="form-group" style="flex: 0 0 160px; margin-bottom: 0;">
                        <label for="op-asset">Asset</label>
                        <select id="op-asset">
                            <option value="OPT">OPT</option>
                            <option value="TITN">TITN</option>
                            <option value="TGT">TGT</option>
                            <option value="ETH">ETH</option>
                            <option value="NILA">NILA</option>
                            <option value="PATRIOT">PATRIOT</option>
                            <option value="BNB">BNB</option>
                        </select>
                    </div>
                    <div class="form-group op-other" style="flex: 0 0 260px; margin-bottom: 0;" hidden>
                        <label for="op-wallet">Wallet</label>
                        <select id="op-wallet"><option value="">All my Ethereum, Arbitrum, Base and BNB Chain wallets</option></select>
                    </div>
                    <div class="form-group op-other" style="flex: 0 0 200px; margin-bottom: 0;" hidden>
                        <label for="op-target">Target loss (USD)</label>
                        <input type="number" id="op-target" min="0" step="any" placeholder="e.g. 1000">
                    </div>
                    <div class="form-group op-other" style="flex: 0 0 200px; margin-bottom: 0;" hidden title="Leave blank to use today's market price">
                        <label for="op-price">Sale price (USD)</label>
                        <input type="number" id="op-price" min="0" step="any" placeholder="today's price">
                    </div>
                </div>
                <div id="op-status" class="op-other" hidden style="font-size: 13px; color: var(--text-secondary); margin: 10px 0;"></div>
                <div id="op-tiles" class="op-other" hidden style="display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 8px; margin-bottom: 10px;"></div>
                <div id="op-where" class="op-other" hidden style="font-size: 13px; margin-bottom: 10px;"></div>
                <div class="data-table-wrap op-other" hidden>
                    <table class="data-table">
                        <thead><tr><th>Sell order</th><th>Wallet</th><th>Acquired</th><th>Term</th><th style="text-align: right;">Quantity</th><th style="text-align: right;">Unit cost</th><th style="text-align: right;">Cost</th><th style="text-align: right;">Value at sale price</th><th style="text-align: right;">Gain or (loss)</th><th style="text-align: right;">Running loss</th><th>How it was acquired</th></tr></thead>
                        <tbody id="op-body"></tbody>
                    </table>
                </div>
            </div>
        </div>
        <script>
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                const usd = (v) => (v < 0 ? '($' : '$') + Math.abs(Number(v || 0)).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + (v < 0 ? ')' : '');
                const qtyFmt = (v, a) => Number(v).toLocaleString('en-US', { maximumFractionDigits: a === 'ETH' || a === 'BNB' ? 6 : a === 'PATRIOT' ? 0 : 2 });
                // Unit prices: cents for ETH and BNB, enough digits for tokens worth a fraction of a cent.
                const pxFmt = (v, a) => a === 'ETH' || a === 'BNB' ? Number(v).toFixed(2) : Number(v) < 0.001 ? Number(v).toPrecision(3) : Number(v).toFixed(5);
                let W = window.opticWallets; // set by the wallet script further down the page
                let lotsData = null, balances = null, loading = false;
                // The OPT planner on this page (the tax-report card in planner mode).
                const optCard = () => { const f = $('tr-address'); return f ? f.closest('.card') : null; };
                const tile = (k, v, s) => `<div style="border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;"><div style="font-size: 12px; color: var(--text-secondary);">${k}</div><div style="font-size: 18px; font-weight: 700;">${v}</div><div style="font-size: 12px; color: var(--text-muted);">${s || ''}</div></div>`;

                const load = () => {
                    if (lotsData || loading) return;
                    loading = true;
                    $('op-status').textContent = 'Reading your Ethereum, Arbitrum, Base and BNB Chain wallets… The first time can take several minutes.';
                    Promise.all([
                        fetch('/api/titn/lots').then(r => r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status))),
                        fetch('/api/evm/balances').then(r => r.ok ? r.json() : null).catch(() => null),
                        fetch('/api/titn/summary').then(r => r.ok ? r.json() : null).catch(() => null),
                    ]).then(([l, b, s]) => { lotsData = l; balances = { b, s }; loading = false; render(); })
                        .catch(e => { loading = false; $('op-status').textContent = 'Could not read your wallets: ' + e.message; });
                };

                const render = () => {
                    W = window.opticWallets;
                    if (!W) { setTimeout(render, 200); return; }
                    const asset = $('op-asset').value;
                    const other = asset !== 'OPT';
                    document.querySelectorAll('#op-card .op-other').forEach(el => el.hidden = !other);
                    const oc = optCard(); if (oc) oc.hidden = other;
                    if (!other) return;
                    if (!W.listSaved().some(w => W.validEvm(w.a))) { $('op-status').innerHTML = 'Add your Ethereum, Arbitrum or Base wallets on <a href="/page/my-wallets">My Wallets</a> to plan TITN, TGT, NILA, PATRIOT, ETH or BNB sales.'; return; }
                    if (!lotsData) { load(); return; }
                    // Wallet choices.
                    const wsel = $('op-wallet');
                    if (wsel.options.length === 1) for (const w of W.listSaved().filter(w => W.validEvm(w.a))) { const o = document.createElement('option'); o.value = w.a; o.textContent = w.l || (w.a.slice(0, 6) + '…' + w.a.slice(-4)); wsel.appendChild(o); }
                    const wallet = wsel.value;
                    const marketPx = lotsData.price ? lotsData.price[asset] : null;
                    const px = Number($('op-price').value) > 0 ? Number($('op-price').value) : marketPx;
                    const lots = (lotsData.holdings || []).filter(h => h.asset === asset && (!wallet || h.wallet === wallet) && h.qty > (asset === 'ETH' || asset === 'BNB' ? 0.0000001 : 0.000001))
                        .sort((a, b) => (b.unitCost != null) - (a.unitCost != null) || (b.unitCost ?? 0) - (a.unitCost ?? 0) || String(a.acquired).localeCompare(String(b.acquired)));
                    const now = Date.now();
                    const year = new Date().getUTCFullYear();
                    const realized = (lotsData.disposals || []).filter(d => d.asset === asset && String(d.sold).startsWith(String(year)) && (!wallet || d.wallet === wallet));
                    const realizedGain = realized.reduce((s, d) => s + (d.gain || 0), 0);
                    const heldQty = lots.reduce((s, l) => s + l.qty, 0), heldCost = lots.reduce((s, l) => s + (l.cost || 0), 0);
                    const lossLots = lots.filter(l => l.unitCost != null && px != null && l.unitCost > px);
                    const maxLossQty = lossLots.reduce((s, l) => s + l.qty, 0);
                    const maxLoss = lossLots.reduce((s, l) => s + (l.qty * px - l.unitCost * l.qty), 0);
                    // Target loss: sell highest-cost lots first until the loss is reached (the last lot partly).
                    const target = Number($('op-target').value) > 0 ? -Number($('op-target').value) : null;
                    let needQty = null;
                    if (target != null && px != null) {
                        let run = 0, q = 0;
                        for (const l of lots) {
                            if (l.unitCost == null || l.unitCost <= px) break;
                            const per = px - l.unitCost; // negative
                            if (run + l.qty * per <= target) { q += (target - run) / per; run = target; break; }
                            run += l.qty * per; q += l.qty;
                        }
                        needQty = run <= target + 0.005 ? q : null;
                    }
                    $('op-tiles').innerHTML =
                        tile(`${asset} held`, qtyFmt(heldQty, asset), `Cost basis ${usd(heldCost)}`) +
                        tile('Sale price', px != null ? '$' + pxFmt(px, asset) : '–', Number($('op-price').value) > 0 ? 'Your price' : 'Today\x27s price (DefiLlama)') +
                        tile('Value at that price', px != null ? usd(heldQty * px) : '–', `Unrealized ${px != null ? usd(heldQty * px - heldCost) : '–'}`) +
                        tile(`Realized in ${year} so far`, usd(realizedGain), `${realized.length} sale lines (Tax Report section 4a)`) +
                        tile('Largest loss available', usd(maxLoss), `Sell ${qtyFmt(maxLossQty, asset)} ${asset} (every lot above the sale price)`) +
                        (target != null ? tile(`To lose ${usd(-target)}`, needQty != null ? qtyFmt(needQty, asset) + ' ' + asset : 'Not reachable', needQty != null ? `Highest-cost lots first; proceeds ${usd(needQty * px)}` : `The most available is ${usd(maxLoss)}`) : '');
                    // Where the asset is now (TITN staked must be unstaked before it can be sold).
                    const b = balances && balances.b, s = balances && balances.s;
                    const where = [];
                    for (const w of ((b && b.wallets) || []).filter(w => !wallet || w.address === wallet))
                        for (const ch of w.chains) for (const x of ch.balances.filter(x => x.symbol === asset)) where.push(`${esc(W.label(w.address) || w.address.slice(0, 6) + '…')} on ${ch.chain === 'bsc' ? 'BNB Chain' : ch.chain[0].toUpperCase() + ch.chain.slice(1)}: ${qtyFmt(x.amount, asset)} ${asset}`);
                    if (asset === 'TITN') for (const w of ((s && s.wallets) || []).filter(w => (!wallet || w.address === wallet) && w.titnStaked > 0.01)) where.push(`${esc(W.label(w.address) || w.address.slice(0, 6) + '…')}: ${qtyFmt(w.titnStaked, 'TITN')} TITN staked on Base (unstake before selling)`);
                    $('op-where').innerHTML = where.length ? '<strong>Where it is now:</strong> ' + where.join(' · ') : '';
                    $('op-status').textContent = `Lots are kept per wallet and sold highest cost first (your standing order), the same as the Tax Report. Gains and losses here are estimates at the sale price and leave out network fees.${lots.some(l => l.unitCost == null) ? ' Some lots have no known cost and are listed last.' : ''}`;
                    let running = 0;
                    $('op-body').innerHTML = lots.map((l, i) => {
                        const value = px != null ? l.qty * px : null, gain = value != null && l.unitCost != null ? value - l.unitCost * l.qty : null;
                        if (gain != null) running += gain;
                        const long = l.acquired && (now - Date.parse(l.acquired)) / 86400000 > 365;
                        return `<tr><td>${i + 1}</td><td>${esc(W.label(l.wallet) || l.wallet.slice(0, 6) + '…')}</td><td>${esc(String(l.acquired || '').slice(0, 10))}</td><td>${long ? 'Long' : 'Short'}</td><td style="text-align: right;">${qtyFmt(l.qty, asset)}</td><td style="text-align: right;">${l.unitCost == null ? '<span class="tr-flag">unknown</span>' : '$' + pxFmt(l.unitCost, asset)}</td><td style="text-align: right;">${l.cost == null ? '–' : usd(l.cost)}</td><td style="text-align: right;">${value == null ? '–' : usd(value)}</td><td style="text-align: right;">${gain == null ? '–' : usd(gain)}</td><td style="text-align: right;">${usd(running)}</td><td style="font-size: 12px;">${esc(l.how)}</td></tr>`;
                    }).join('') || `<tr><td colspan="11" style="text-align: center; color: var(--text-secondary);">No ${asset} held${wallet ? ' in this wallet' : ''}.</td></tr>`;
                };
                for (const id of ['op-asset', 'op-wallet']) $(id).addEventListener('change', render);
                for (const id of ['op-target', 'op-price']) $(id).addEventListener('input', render);
                // Remembered choice; ?asset=TITN in the address also works.
                const q = new URLSearchParams(location.search).get('asset');
                try { const saved = q || localStorage.getItem('optic_planner_asset'); if (saved && [...$('op-asset').options].some(o => o.value === saved)) $('op-asset').value = saved; } catch (e) { }
                $('op-asset').addEventListener('change', () => { try { localStorage.setItem('optic_planner_asset', $('op-asset').value); } catch (e) { } });
                window.addEventListener('load', render);
                setTimeout(render, 0);
            })();
        </script>
        """;
}
