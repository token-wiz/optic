// My Wallets: every asset across all saved wallets in one place (OPT liquid / staked / unbonding / rewards on Optio,
// OPT, USDC and OSMO on Osmosis, ETH, USDC, USDT, TITN (held and staked), TGT and PATRIOT on Ethereum, Arbitrum and Base,
// BNB, NILA (held and staked) and USDT on BNB Chain), and a Staked table: how much of each token is staked, when it
// unlocks and what it has earned.
public static partial class WebDashboard
{
    static void AppendPortfolioCard(System.Text.StringBuilder sb) => sb.Append(PortfolioHtml);

    const string PortfolioHtml = """
        <div class="card" style="grid-column: 1 / -1;" id="pf-card">
            <h2 style="display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap;">Portfolio<a id="pf-manage" href="/page/my-wallets" style="font-size: 12px; font-weight: 400;">Manage wallets</a></h2>
            <div class="card-content">
                <div id="pf-status" style="font-size: 13px; color: var(--text-secondary); margin-bottom: 8px;">Reading your wallets…</div>
                <div id="pf-remind" hidden style="border: 1px solid var(--border); border-radius: 8px; padding: 8px 12px; margin-bottom: 10px;">
                    <div style="display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 4px;"><strong style="font-size: 14px;">Reminders</strong><span id="pf-remind-note" style="font-size: 12px; color: var(--text-secondary);"></span><button type="button" id="pf-remind-all" style="font-size: 11px; padding: 2px 10px; margin-left: auto;">Add all to calendar</button></div>
                    <div id="pf-remind-list" style="display: flex; flex-direction: column; gap: 4px; font-size: 13px;"></div>
                </div>
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
                const qty = (v, a) => Number(v).toLocaleString('en-US', { maximumFractionDigits: a === 'ETH' || a === 'BNB' || a === 'SOL' ? 6 : a === 'OPT' || a === 'TITN' || a === 'TGT' || a === 'PATRIOT' ? 0 : 2 });
                const day = (v) => v ? new Date(v).toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' }) : '';
                const net = { base: 'Base', ethereum: 'Ethereum', arbitrum: 'Arbitrum', bsc: 'BNB Chain', osmosis: 'Osmosis', gonka: 'Gonka', solana: 'Solana', optio: 'Optio' };
                // One request per page for data the Portfolio and My Wallets cards both read.
                window.opticGet = window.opticGet || (() => { /* Last visit's answers (saved in this browser per user) are returned at once and replaced when the fresh ones arrive; each arrival fires 'optic-fresh' so the page can redraw. */ const mem = {}, key = (u) => 'optic.cache.' + ((window.opticUser || {}).name || '') + '.' + u; let timer = null; const get = (u) => { if (mem[u]) return mem[u]; let old = null; try { old = JSON.parse(localStorage.getItem(key(u)) || 'null'); } catch (e) { } const live = fetch(u).then(r => r.ok ? r.json() : null).then(j => { if (j) { try { localStorage.setItem(key(u), JSON.stringify({ t: Date.now(), j })); } catch (e) { } } return j; }); if (!old || !old.j) return mem[u] = live; get.cachedAt = Math.min(get.cachedAt || Infinity, old.t); get.waiting = (get.waiting || 0) + 1; live.catch(() => null).then(j => { mem[u] = Promise.resolve(j || old.j); get.waiting--; clearTimeout(timer); timer = setTimeout(() => window.dispatchEvent(new Event('optic-fresh')), 300); }); return mem[u] = Promise.resolve(old.j); }; return get; })();
                if (location.pathname.endsWith('/my-wallets')) $('pf-manage').hidden = true; // the wallet list is right below
                const ORDER = ['USDC', 'OPT', 'TITN', 'NILA', 'GNK', 'TGT', 'PATRIOT', 'ETH', 'SOL', 'BNB', 'USDT', 'OSMO']; // cash (USDC) first
                // Reminders: unlock dates and rewards ready to claim, from the staked positions. Each can go to your calendar
                // (an .ics file your phone or computer opens), so the reminder comes from your own calendar app.
                let reminders = [];
                const dayMs = 86400000;
                const renderReminders = (stakes) => {
                    const now = Date.now(), list = [];
                    const fmtAmt = (v, a) => Number(v).toLocaleString('en-US', { maximumFractionDigits: a === 'OPT' || a === 'NILA' ? 0 : 2 });
                    for (const st of stakes) {
                        const ra = st.rewardAsset || st.asset;
                        if (st.unlocks) {
                            const t = Date.parse(st.unlocks), days = Math.ceil((t - now) / dayMs);
                            const what = st.where.includes('unbonding') ? 'finishes unbonding' : 'unlocks';
                            if (days > 0) list.push({ t, ready: false, title: `${fmtAmt(st.amount, st.asset)} ${st.asset} ${what}`, detail: st.where + (st.atUnlock ? ` · reward at unlock ${fmtAmt(st.atUnlock, ra)} ${ra}` : ''), days });
                            else if (st.asset === 'NILA') list.push({ t, ready: true, title: `${fmtAmt(st.amount, 'NILA')} NILA unlocked${st.earned > 0 ? ` with ${fmtAmt(st.earned, 'NILA')} NILA reward` : ''}`, detail: st.where + ' · claiming is paused on the V1 staking site until the MindChain migration' });
                        } else if (st.earned > 0.005 && st.rewardAsset === 'USDC') {
                            list.push({ t: now, ready: true, title: `${fmtAmt(st.earned, 'USDC')} USDC rewards ready to claim`, detail: `${st.asset} staking · ${st.where}` });
                        }
                    }
                    list.sort((x, y) => (y.ready - x.ready) || (x.t - y.t));
                    reminders = list.filter(r => r.ready || r.days <= 400);
                    $('pf-remind').hidden = !reminders.length;
                    const ready = reminders.filter(r => r.ready).length;
                    $('pf-remind-note').textContent = ready ? `${ready} ready now` : `next in ${reminders[0]?.days ?? 0} days`;
                    $('pf-remind-list').innerHTML = reminders.slice(0, 8).map((r, i) => `<div style="display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap;">
                        <span style="min-width: 92px; font-weight: 600; ${r.ready ? 'color: var(--success, #107c10);' : ''}">${r.ready ? 'Ready now' : new Date(r.t).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' })}</span>
                        <span>${esc(r.title)}${r.ready ? '' : ` <span style="color: var(--text-secondary);">(in ${r.days} day${r.days === 1 ? '' : 's'})</span>`}<div style="font-size: 11px; color: var(--text-secondary);">${esc(r.detail)}</div></span>
                        ${r.ready ? '' : `<button type="button" data-remind="${i}" style="font-size: 11px; padding: 1px 8px; margin-left: auto;">Add to calendar</button>`}</div>`).join('')
                        + (reminders.length > 8 ? `<div style="font-size: 12px; color: var(--text-secondary);">and ${reminders.length - 8} more in the Staked table below</div>` : '');
                };
                // An all-day calendar event on the date, with an alert the morning before.
                const ics = (items) => {
                    const d = (t) => new Date(t).toISOString().slice(0, 10).replace(/-/g, '');
                    const txt = (v) => String(v).replace(/[\\,;]/g, m => '\\' + m).replace(/\n/g, ' ');
                    const stamp = new Date().toISOString().replace(/[-:]/g, '').slice(0, 15) + 'Z';
                    return ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//OPTIC//Reminders//EN'].concat(items.flatMap((r, i) => ['BEGIN:VEVENT', `UID:optic-${d(r.t)}-${i}-${r.title.length}@optic`, `DTSTAMP:${stamp}`, `DTSTART;VALUE=DATE:${d(r.t)}`, `DTEND;VALUE=DATE:${d(r.t + dayMs)}`,
                        `SUMMARY:${txt(r.title)}`, `DESCRIPTION:${txt(r.detail + ' (from OPTIC)')}`, 'BEGIN:VALARM', 'TRIGGER:-PT15H', 'ACTION:DISPLAY', `DESCRIPTION:${txt(r.title)}`, 'END:VALARM', 'END:VEVENT']), ['END:VCALENDAR']).join('\r\n');
                };
                const saveIcs = (items, name) => {
                    const a = document.createElement('a');
                    a.href = URL.createObjectURL(new Blob([ics(items)], { type: 'text/calendar' }));
                    a.download = name;
                    a.click();
                    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
                };
                document.getElementById('pf-remind-list').addEventListener('click', (e) => {
                    const b = e.target.closest('[data-remind]');
                    if (b) saveIcs([reminders[Number(b.dataset.remind)]], 'optic-reminder.ics');
                });
                document.getElementById('pf-remind-all').addEventListener('click', () => {
                    const upcoming = reminders.filter(r => !r.ready);
                    if (upcoming.length) saveIcs(upcoming, 'optic-reminders.ics');
                });
                const start = async () => {
                    const W = window.opticWallets;
                    if (!W) { setTimeout(start, 200); return; }
                    const name = (a) => W.label(a) || (a.slice(0, 6) + '…' + a.slice(-4));
                    const saved = W.listSaved();
                    if (!saved.length) { $('pf-status').innerHTML = 'Add your wallets on <a href="/page/my-wallets">My Wallets</a> to see your portfolio.'; return; }
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
                        renderReminders(stakes);
                        $('pf-status').textContent = loading ? 'Still reading some wallets…' : `Values at today\x27s prices (OPT: ${priceNote.OPT || 'latest available'}). Staked and unbonding amounts are included and noted; OPT locked in staking cannot be sold until it is unlocked.`;
                    };
                    let stakeNote = '';
                    const jobs = [];
                    // OPT on each Optio wallet: liquid, staked, unbonding and unclaimed rewards.
                    for (const w of saved.filter(w => W.valid(w.a)))
                        jobs.push(window.opticGet('/api/staking/positions?address=' + encodeURIComponent(w.a)).then(j => {
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
                    jobs.push(window.opticGet('/api/price/daily').then(j => {
                        const last = j && j.rows && j.rows[j.rows.length - 1];
                        if (last) { prices.OPT = last.priceUsd; priceNote.OPT = `${last.from || 'CoinMarketCap'} ${last.date}`; }
                    }).catch(() => { }));
                    await Promise.all(jobs);
                    render(false);
                    // Shown from the last visit while fresh figures load; redrawn as they arrive.
                    const G = window.opticGet;
                    if (G.waiting > 0 && G.cachedAt) $('pf-status').textContent = `Showing your figures from ${new Date(G.cachedAt).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })} while they refresh… ` + $('pf-status').textContent;
                };
                start();
                window.addEventListener('optic-fresh', () => start());
            })();
        </script>
        """;
}
