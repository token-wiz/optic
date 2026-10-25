// My Wallets: every asset across all saved wallets in one place (OPT liquid / staked / unbonding / rewards on Optio,
// OPT, USDC and OSMO on Osmosis, ETH, USDC, USDT, TITN (held and staked), TGT and PATRIOT on Ethereum, Arbitrum and Base,
// BNB, NILA (held and staked) and USDT on BNB Chain), and a Staked table: how much of each token is staked, when it
// unlocks and what it has earned.
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
                <h3 style="margin: 16px 0 6px; font-size: 15px;">Staked</h3>
                <div id="pf-stake-status" style="font-size: 13px; color: var(--text-secondary); margin-bottom: 6px;"></div>
                <div class="data-table-wrap">
                    <table class="data-table">
                        <thead><tr><th>Token</th><th>Wallet</th><th style="text-align: right;">Staked</th><th>Since</th><th>Unlocks</th><th style="text-align: right;">Earned so far</th><th style="text-align: right;">Earned by unlock</th><th style="text-align: right;">Value now</th></tr></thead>
                        <tbody id="pf-stake-body"></tbody>
                    </table>
                </div>
            </div>
        </div>
        <script>
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                const usd = (v) => '$' + Number(v || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                const qty = (v, a) => Number(v).toLocaleString('en-US', { maximumFractionDigits: a === 'ETH' || a === 'BNB' ? 6 : a === 'OPT' || a === 'TITN' || a === 'TGT' || a === 'PATRIOT' ? 0 : 2 });
                const day = (v) => v ? new Date(v).toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' }) : '';
                const net = { base: 'Base', ethereum: 'Ethereum', arbitrum: 'Arbitrum', bsc: 'BNB Chain', osmosis: 'Osmosis', gonka: 'Gonka', optio: 'Optio' };
                // One request per page for data the Portfolio and My Wallets cards both read.
                window.opticGet = window.opticGet || (() => { const c = {}; return (u) => c[u] || (c[u] = fetch(u).then(r => r.ok ? r.json() : null)); })();
                const ORDER = ['USDC', 'OPT', 'TITN', 'NILA', 'GNK', 'TGT', 'PATRIOT', 'ETH', 'BNB', 'USDT', 'OSMO']; // cash (USDC) first
                const start = async () => {
                    const W = window.opticWallets;
                    if (!W) { setTimeout(start, 200); return; }
                    const name = (a) => W.label(a) || (a.slice(0, 6) + '…' + a.slice(-4));
                    const saved = W.listSaved();
                    if (!saved.length) { $('pf-status').textContent = 'Add your wallets below to see your portfolio.'; return; }
                    // asset -> { total, parts: [{ where, amount, note }] }
                    const assets = {}, prices = {}, priceNote = {};
                    // Staked positions: { asset, where, amount, since, unlocks, unlockNote, earned, atUnlock, rewardAsset }
                    const stakes = [];
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
                        const order = (k) => ORDER.indexOf(k) + 1 || 99;
                        $('pf-stake-body').innerHTML = stakes.slice().sort((x, y) => order(x.asset) - order(y.asset) || String(x.unlocks || '9').localeCompare(String(y.unlocks || '9'))).map(s => {
                            const rp = prices[s.rewardAsset || s.asset], vp = prices[s.asset];
                            const value = vp != null ? s.amount * vp + (s.earned && rp != null ? s.earned * rp : 0) : null;
                            const unlocked = s.unlocks && Date.parse(s.unlocks) <= Date.now();
                            const ra = s.rewardAsset || s.asset;
                            return `<tr><td><strong>${esc(s.asset)}</strong></td><td style="font-size: 12px;">${esc(s.where)}</td><td style="text-align: right; white-space: nowrap;">${qty(s.amount, s.asset)}</td><td style="white-space: nowrap;">${s.since ? day(s.since) : '–'}</td>` +
                                `<td style="white-space: nowrap;">${s.unlocks ? (unlocked ? '<strong>Unlocked</strong> ' + day(s.unlocks) : day(s.unlocks)) : '<span style="color: var(--text-muted);">' + esc(s.unlockNote || '–') + '</span>'}</td>` +
                                `<td style="text-align: right; white-space: nowrap;">${s.earned != null ? qty(s.earned, ra) + ' ' + esc(ra) : '–'}${s.earnedNote ? '<br><span style="font-size: 11px; color: var(--text-muted);">' + esc(s.earnedNote) + '</span>' : ''}</td>` +
                                `<td style="text-align: right; white-space: nowrap;">${s.atUnlock != null ? qty(s.atUnlock, ra) + ' ' + esc(ra) : '–'}</td><td style="text-align: right; white-space: nowrap;" title="Staked amount plus rewards earned so far, at today\x27s price">${value != null ? usd(value) : '–'}</td></tr>`;
                        }).join('') || '<tr><td colspan="8" style="text-align: center; color: var(--text-secondary);">' + (loading ? 'Reading your stakes…' : 'Nothing staked.') + '</td></tr>';
                        $('pf-stake-status').textContent = stakeNote;
                        $('pf-status').textContent = loading ? 'Still reading some wallets…' : `Values at today\x27s prices (OPT: ${priceNote.OPT || 'latest available'}). Staked and unbonding amounts are included and noted; OPT locked in staking cannot be sold until it is unlocked.`;
                    };
                    let stakeNote = '';
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
                            // Staked OPT by unlock date (x/lockup locks), the rest unlocked; unbonding OPT with its date.
                            for (const l of j.locks || []) stakes.push({ asset: 'OPT', where: `${n} · Optio`, amount: l.opt, unlocks: l.unlockDate + 'T00:00:00Z', earned: null });
                            const free = (t.stakedOpt || 0) - (t.lockedOpt || 0);
                            if (free > 0.01) stakes.push({ asset: 'OPT', where: `${n} · Optio`, amount: free, unlockNote: `No lock: unstake any time (${t.unbondingDays ? Math.round(t.unbondingDays) + '-day' : ''} unbonding)`, earned: t.rewardsOpt, earnedNote: 'unclaimed staking rewards on this wallet' });
                            else if (t.rewardsOpt > 0.01 && stakes.some(s => s.asset === 'OPT' && s.where === `${n} · Optio`)) stakes.find(s => s.asset === 'OPT' && s.where === `${n} · Optio`).earned = t.rewardsOpt;
                            for (const u of t.unbondingEntries || []) stakes.push({ asset: 'OPT', where: `${n} · Optio (unbonding)`, amount: u.opt, unlocks: u.completes, earned: null });
                            render(true);
                        }).catch(() => { }));
                    // ETH, USDC, USDT, TITN, TGT on Ethereum / Arbitrum / Base, and USDC, OPT, OSMO on Osmosis.
                    jobs.push(window.opticGet('/api/evm/balances').then(j => {
                        if (!j) return;
                        for (const [k, v] of Object.entries(j.prices || {})) if (v > 0) prices[k === 'USDC.e' ? 'USDC' : k] = prices[k === 'USDC.e' ? 'USDC' : k] ?? v;
                        for (const w of j.wallets || []) for (const ch of w.chains) for (const b of ch.balances)
                            add(b.symbol === 'USDC.e' ? 'USDC' : b.symbol, `${name(w.address)} · ${net[ch.chain] || ch.chain}`, b.amount, b.symbol === 'USDC.e' ? 'USDC.e (bridged)' : '');
                        render(true);
                    }).catch(() => { }));
                    // Staked TITN (not in the wallet balance) and the TITN price.
                    if (saved.some(w => W.validEvm(w.a)))
                        jobs.push(window.opticGet('/api/titn/summary').then(j => {
                            if (!j) return;
                            for (const w of j.wallets || []) {
                                add('TITN', `${name(w.address)} · Base`, w.titnStaked, 'staked');
                                if (w.titnStaked > 0.01) stakes.push({ asset: 'TITN', where: `${name(w.address)} · Base`, amount: w.titnStaked, unlockNote: 'No lock date reported by TitnStaking', earned: w.pendingUsdc > 0.005 ? w.pendingUsdc : null, rewardAsset: 'USDC', earnedNote: w.pendingUsdc > 0.005 ? 'USDC not claimed yet' : '' });
                            }
                            if (j.price && j.price.usd) prices.TITN = j.price.usd;
                            render(true);
                        }).catch(() => { }));
                    // NILA stakes on BNB Chain: each stake's amount, unlock date and reward (read from the staking contract).
                    if (saved.some(w => W.validEvm(w.a)))
                        jobs.push(window.opticGet('/api/stakes').then(j => {
                            if (!j) return;
                            if (j.prices && j.prices.NILA) prices.NILA = prices.NILA ?? j.prices.NILA;
                            for (const s of j.stakes || []) {
                                add(s.asset, `${name(s.wallet)} · ${net[s.chain] || s.chain}`, s.amount, `staked, ${s.unlocked ? 'unlocked' : 'unlocks ' + day(s.unlocks)}`);
                                stakes.push({ asset: s.asset, where: `${name(s.wallet)} · ${net[s.chain] || s.chain}`, amount: s.amount, since: s.staked, unlocks: s.unlocks, earned: s.earnedSoFar, atUnlock: s.rewardAtUnlock, rewardAsset: s.rewardAsset,
                                    earnedNote: s.unclaimedReward > 0.000001 ? (s.unlocked ? 'ready to claim' : 'builds up until the unlock date') : '' });
                            }
                            stakeNote = (j.notes || []).join(' ');
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
