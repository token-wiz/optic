// My Wallets: every asset across all saved wallets in one place (OPT liquid / staked / unbonding / rewards on Optio,
// OPT, USDC and OSMO on Osmosis, ETH, USDC, USDT, TITN (held and staked) and TGT on Ethereum, Arbitrum and Base).
public static partial class WebDashboard
{
    static void AppendPortfolioCard(System.Text.StringBuilder sb) => sb.Append(PortfolioHtml);

    const string PortfolioHtml = """
        <div class="card" style="grid-column: 1 / -1;" id="pf-card">
            <h2>Portfolio</h2>
            <div class="card-content">
                <div id="pf-status" style="font-size: 13px; color: var(--text-secondary); margin-bottom: 8px;">Reading your wallets…</div>
                <div id="pf-tiles" style="display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 8px; margin-bottom: 10px;"></div>
                <div class="data-table-wrap">
                    <table class="data-table">
                        <thead><tr><th>Asset</th><th style="text-align: right;">Total</th><th style="text-align: right;">Price</th><th style="text-align: right;">Value</th><th>Where it is</th></tr></thead>
                        <tbody id="pf-body"></tbody>
                        <tfoot id="pf-foot"></tfoot>
                    </table>
                </div>
            </div>
        </div>
        <script>
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                const usd = (v) => '$' + Number(v || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                const qty = (v, a) => Number(v).toLocaleString('en-US', { maximumFractionDigits: a === 'ETH' ? 6 : a === 'OPT' || a === 'TITN' || a === 'TGT' ? 0 : 2 });
                const net = { base: 'Base', ethereum: 'Ethereum', arbitrum: 'Arbitrum', osmosis: 'Osmosis', optio: 'Optio' };
                const ORDER = ['OPT', 'TITN', 'TGT', 'ETH', 'USDC', 'USDT', 'OSMO'];
                const start = async () => {
                    const W = window.opticWallets;
                    if (!W) { setTimeout(start, 200); return; }
                    const name = (a) => W.label(a) || (a.slice(0, 6) + '…' + a.slice(-4));
                    const saved = W.listSaved();
                    if (!saved.length) { $('pf-status').textContent = 'Add your wallets below to see your portfolio.'; return; }
                    // asset -> { total, parts: [{ where, amount, note }] }
                    const assets = {}, prices = {}, priceNote = {};
                    const add = (asset, where, amount, note) => {
                        if (!(amount > (asset === 'ETH' ? 0.0000001 : 0.000001))) return;
                        const a = assets[asset] = assets[asset] || { total: 0, parts: [] };
                        a.total += amount; a.parts.push({ where, amount, note });
                    };
                    const render = (loading) => {
                        const rows = Object.keys(assets).sort((x, y) => (ORDER.indexOf(x) + 1 || 99) - (ORDER.indexOf(y) + 1 || 99));
                        let total = 0;
                        $('pf-body').innerHTML = rows.map(k => {
                            const a = assets[k], p = prices[k], v = p != null ? a.total * p : null;
                            if (v != null) total += v;
                            const where = a.parts.map(x => `${esc(x.where)}: ${qty(x.amount, k)}${x.note ? ' <span style="color: var(--text-muted);">' + esc(x.note) + '</span>' : ''}`).join('<br>');
                            return `<tr><td><strong>${esc(k)}</strong></td><td style="text-align: right; white-space: nowrap;">${qty(a.total, k)}</td><td style="text-align: right; white-space: nowrap;" title="${esc(priceNote[k] || '')}">${p != null ? '$' + Number(p).toLocaleString('en-US', { maximumFractionDigits: p < 1 ? 6 : 2 }) : '–'}</td><td style="text-align: right; white-space: nowrap;">${v != null ? usd(v) : '–'}</td><td style="font-size: 12px;">${where}</td></tr>`;
                        }).join('') || '<tr><td colspan="5" style="text-align: center; color: var(--text-secondary);">Nothing found yet.</td></tr>';
                        $('pf-foot').innerHTML = rows.length ? `<tr class="tr-total"><td colspan="3"><strong>Total</strong></td><td style="text-align: right;"><strong>${usd(total)}</strong></td><td></td></tr>` : '';
                        const tile = (k, v, s) => `<div style="border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;"><div style="font-size: 12px; color: var(--text-secondary);">${k}</div><div style="font-size: 18px; font-weight: 700;">${v}</div><div style="font-size: 12px; color: var(--text-muted);">${s || ''}</div></div>`;
                        $('pf-tiles').innerHTML = tile('Total value', usd(total), `${rows.length} assets across ${saved.length} wallets`) +
                            rows.filter(k => prices[k] != null).slice(0, 5).map(k => tile(k, usd(assets[k].total * prices[k]), qty(assets[k].total, k) + ' ' + k)).join('');
                        $('pf-status').textContent = loading ? 'Still reading some wallets…' : `Values at today\x27s prices (OPT: ${priceNote.OPT || 'latest available'}). Staked and unbonding amounts are included and noted; OPT locked in staking cannot be sold until it is unlocked.`;
                    };
                    const jobs = [];
                    // OPT on each Optio wallet: liquid, staked, unbonding and unclaimed rewards.
                    for (const w of saved.filter(w => W.valid(w.a)))
                        jobs.push(fetch('/api/staking/positions?address=' + encodeURIComponent(w.a)).then(r => r.ok ? r.json() : null).then(j => {
                            if (!j || !j.totals) return;
                            const t = j.totals, n = name(w.a);
                            add('OPT', `${n} · Optio`, t.balanceOpt, 'in wallet');
                            add('OPT', `${n} · Optio`, t.stakedOpt, `staked${t.lockedOpt > 0.01 ? ' (' + qty(t.lockedOpt, 'OPT') + ' locked)' : ''}`);
                            add('OPT', `${n} · Optio`, t.unbondingOpt, 'unbonding');
                            add('OPT', `${n} · Optio`, t.rewardsOpt, 'unclaimed rewards');
                            render(true);
                        }).catch(() => { }));
                    // ETH, USDC, USDT, TITN, TGT on Ethereum / Arbitrum / Base, and USDC, OPT, OSMO on Osmosis.
                    jobs.push(fetch('/api/evm/balances').then(r => r.ok ? r.json() : null).then(j => {
                        if (!j) return;
                        for (const [k, v] of Object.entries(j.prices || {})) if (v > 0) prices[k === 'USDC.e' ? 'USDC' : k] = prices[k === 'USDC.e' ? 'USDC' : k] ?? v;
                        for (const w of j.wallets || []) for (const ch of w.chains) for (const b of ch.balances)
                            add(b.symbol === 'USDC.e' ? 'USDC' : b.symbol, `${name(w.address)} · ${net[ch.chain] || ch.chain}`, b.amount, b.symbol === 'USDC.e' ? 'USDC.e (bridged)' : '');
                        render(true);
                    }).catch(() => { }));
                    // Staked TITN (not in the wallet balance) and the TITN price.
                    if (saved.some(w => W.validEvm(w.a)))
                        jobs.push(fetch('/api/titn/summary').then(r => r.ok ? r.json() : null).then(j => {
                            if (!j) return;
                            for (const w of j.wallets || []) add('TITN', `${name(w.address)} · Base`, w.titnStaked, 'staked');
                            if (j.price && j.price.usd) prices.TITN = j.price.usd;
                            render(true);
                        }).catch(() => { }));
                    // OPT price: latest daily close.
                    jobs.push(fetch('/api/price/daily').then(r => r.ok ? r.json() : null).then(j => {
                        const last = j && j.rows && j.rows[j.rows.length - 1];
                        if (last) { prices.OPT = last.priceUsd; priceNote.OPT = `${last.from || 'CoinMarketCap'} ${last.date}`; }
                    }).catch(() => { }));
                    await Promise.all(jobs);
                    render(false);
                };
                start();
            })();
        </script>
        """;
}
