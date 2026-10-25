// Chains > SOVR: the Sovren layer one at a glance (price, latest block, the chain's supply ledger, staking, validators,
// lockup settings and governance) and the SOVR in your saved sovr1... wallets. Data from /api/sovr/overview.
public static partial class WebDashboard
{
    static void AppendSovrPage(System.Text.StringBuilder sb) => sb.Append(SovrPageHtml);

    const string SovrPageHtml = """
        <style>
            .sv { grid-column: 1 / -1; }
            .sv .sv-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 8px; margin-bottom: 12px; }
            .sv .sv-tile { border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; }
            .sv .sv-tile .k { font-size: 12px; color: var(--text-secondary); }
            .sv .sv-tile .v { font-size: 18px; font-weight: 700; }
            .sv .sv-tile .s { font-size: 12px; color: var(--text-muted); }
            .sv .sv-note { font-size: 13px; color: var(--text-secondary); margin: 0 0 8px; max-width: 1040px; }
            .sv .sv-bar { display: flex; height: 14px; border-radius: 7px; overflow: hidden; background: var(--border); margin: 6px 0; }
            .sv .sv-legend { display: flex; flex-wrap: wrap; gap: 4px 14px; font-size: 12px; margin-bottom: 8px; }
            .sv .sv-legend i { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 5px; vertical-align: -1px; }
            .sv h3 { margin: 18px 0 6px; font-size: 16px; }
            .sv .num { text-align: right; white-space: nowrap; }
        </style>
        <div class="card sv">
            <h2>SOVR · Sovren</h2>
            <div class="card-content">
                <p class="sv-note" id="sv-status">Reading the Sovren chain…</p>
                <div class="sv-grid" id="sv-tiles"></div>
                <div id="sv-chart"></div>

                <h3>Your SOVR</h3>
                <div id="sv-mine-note" class="sv-note"></div>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Wallet</th><th class="num">In wallet</th><th class="num">Staked</th><th class="num">Unbonding</th><th class="num">Rewards</th><th class="num">Total</th><th class="num">Value</th></tr></thead><tbody id="sv-mine"></tbody></table></div>

                <h3>Supply</h3>
                <p class="sv-note">From the chain's own supply ledger (x/supply). <strong>Released</strong> has been claimed into accounts; <strong>not yet released</strong> (latent) is held by the protocol for future emissions and rewards; <strong>vesting</strong> is released over time to its owners; <strong>locked</strong> is in lockup positions.</p>
                <div class="sv-bar" id="sv-supply-bar"></div>
                <div class="sv-legend" id="sv-supply-legend"></div>
                <div id="sv-buckets" class="sv-note"></div>

                <h3>Staking and lockup</h3>
                <div class="sv-grid" id="sv-staking"></div>

                <h3>Validators <span id="sv-val-count" style="font-weight: 400; font-size: 13px; color: var(--text-secondary);"></span></h3>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>#</th><th>Validator</th><th class="num">Staked SOVR</th><th class="num">Share</th><th class="num">Commission</th></tr></thead><tbody id="sv-vals"></tbody></table></div>

                <h3>Governance</h3>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>#</th><th>Proposal</th><th>Status</th><th>Voting ended / ends</th></tr></thead><tbody id="sv-gov"></tbody></table></div>
                <p class="sv-note" id="sv-source" style="margin-top: 10px;"></p>
            </div>
        </div>
        <script>
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                const n0 = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 0 });
                const n2 = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 2 });
                const usd = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                const big = (v) => v >= 1e9 ? (v / 1e9).toFixed(2) + 'B' : v >= 1e6 ? (v / 1e6).toFixed(2) + 'M' : v >= 1e3 ? (v / 1e3).toFixed(1) + 'K' : n2(v);
                const pct = (a, b) => b > 0 ? (a / b * 100).toFixed(a / b < 0.01 ? 3 : 1) + '%' : '–';
                const day = (v) => v ? new Date(v).toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' }) : '';
                const ago = (v) => { const s = Math.max(0, (Date.now() - Date.parse(v)) / 1000); return s < 90 ? Math.round(s) + ' s ago' : s < 5400 ? Math.round(s / 60) + ' min ago' : Math.round(s / 3600) + ' h ago'; };
                const tile = (k, v, s) => `<div class="sv-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s || ''}</div></div>`;
                const key = 'optic.cache.' + ((window.opticUser || {}).name || '') + './api/sovr/overview';

                const render = (j, cached) => {
                    const o = j.overview || {}, s = o.supply, st = o.staking || {}, price = o.price;
                    $('sv-status').textContent = cached ? `Showing the figures from ${new Date(cached).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })} while they refresh…`
                        : `Chain ${o.chainId || 'sovr-1'} · read ${new Date(o.readUtc).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}.`;
                    const ph = o.priceHistory || [];
                    const p30 = ph.length > 30 ? ph[ph.length - 31].usd : ph.length ? ph[0].usd : null;
                    const ch = price != null && p30 ? (price - p30) / p30 * 100 : null;
                    $('sv-tiles').innerHTML =
                        tile('Price', price != null ? '$' + Number(price).toLocaleString('en-US', { maximumFractionDigits: 5 }) : '–', ch != null ? `<span style="color: ${ch >= 0 ? 'var(--success, #107c10)' : 'var(--danger, #c50f1f)'};">${ch >= 0 ? '+' : ''}${ch.toFixed(1)}%</span> over ${ph.length > 30 ? '30 days' : ph.length + ' days'} · DefiLlama` : 'DefiLlama') +
                        tile('Latest block', o.height ? n0(o.height) : '–', o.blockTime ? ago(o.blockTime) : '') +
                        (s ? tile('Released supply', big(s.released) + ' SOVR', `${pct(s.released, s.total)} of ${big(s.total)} max${price != null ? ' · ' + usd(s.released * price) + ' at today’s price' : ''}`) : '') +
                        (s && price != null ? tile('Fully diluted value', usd(s.total * price), `${big(s.total)} SOVR × today’s price`) : '') +
                        tile('Staked', big(st.bonded || 0) + ' SOVR', s ? `${pct(st.bonded || 0, s.released)} of released supply` : '') +
                        tile('Validators', n0(st.validatorCount), st.maxValidators ? `active, of up to ${st.maxValidators}` : 'active');
                    // Price chart (last 90 days).
                    if (ph.length >= 2) {
                        const w = 600, h = 70, lo = Math.min(...ph.map(p => p.usd)), hi = Math.max(...ph.map(p => p.usd)), span = hi - lo || 1;
                        const xy = ph.map((p, i) => [(i / (ph.length - 1) * w).toFixed(1), (h - 4 - (p.usd - lo) / span * (h - 8)).toFixed(1)].join(',')).join(' ');
                        $('sv-chart').innerHTML = `<svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" style="width: 100%; height: ${h}px; display: block;" role="img" aria-label="SOVR price, last ${ph.length} days"><polygon points="0,${h} ${xy} ${w},${h}" fill="#1098ad" opacity="0.12"/><polyline points="${xy}" fill="none" stroke="#1098ad" stroke-width="2" vector-effect="non-scaling-stroke"/></svg>`
                            + `<div class="sv-note" style="display: flex; justify-content: space-between; font-size: 11px;"><span>${day(ph[0].date)}</span><span>SOVR price · low $${lo.toFixed(4)} · high $${hi.toFixed(4)}</span><span>${day(ph[ph.length - 1].date)}</span></div>`;
                    }
                    // Your SOVR.
                    const mine = j.mine || [];
                    $('sv-mine-note').innerHTML = mine.length ? '' : 'Add your sovr1... address on <a href="/page/my-wallets">My Wallets</a> to see your SOVR here.';
                    let tq = 0, tv = 0;
                    $('sv-mine').innerHTML = mine.map(m => {
                        if (m.error) return `<tr><td>${esc(m.label || m.address)}</td><td colspan="6" style="color: var(--text-secondary);">Could not read: ${esc(m.error)}</td></tr>`;
                        const t = m.liquid + m.staked + m.unbonding + m.rewards; tq += t; if (price != null) tv += t * price;
                        const unb = (m.unbondingEntries || []).map(e => `${n2(e.amount)} on ${day(e.completes)}`).join(', ');
                        return `<tr><td>${esc(m.label || 'SOVR wallet')}<div style="font-size: 11px; color: var(--text-muted); font-family: monospace;">${esc(m.address.slice(0, 12) + '…' + m.address.slice(-6))}</div></td><td class="num">${n2(m.liquid)}</td><td class="num">${n2(m.staked)}${m.locked > 0 ? `<div style="font-size: 11px;">${n2(m.locked)} locked</div>` : ''}</td><td class="num">${n2(m.unbonding)}${unb ? `<div style="font-size: 11px;">${esc(unb)}</div>` : ''}</td><td class="num">${n2(m.rewards)}</td><td class="num"><strong>${n2(t)}</strong></td><td class="num">${price != null ? usd(t * price) : '–'}</td></tr>`;
                    }).join('') + (mine.length > 1 ? `<tr class="tr-total"><td colspan="5"><strong>Total</strong></td><td class="num"><strong>${n2(tq)}</strong></td><td class="num"><strong>${usd(tv)}</strong></td></tr>` : '')
                        || '<tr><td colspan="7" style="text-align: center; color: var(--text-secondary);">No SOVR wallets saved.</td></tr>';
                    // Supply ledger.
                    if (s) {
                        const parts = [['Released', s.released, '#1098ad'], ['Not yet released', s.latent, '#adb5bd'], ['Vesting', s.vesting, '#f59f00'], ['Locked', s.locked, '#7b61ff']].filter(p => p[1] > 0);
                        $('sv-supply-bar').innerHTML = parts.map(([k, v, c]) => `<div title="${k}: ${n0(v)} SOVR" style="width: ${Math.max(v / s.total * 100, 0.4).toFixed(2)}%; background: ${c};"></div>`).join('');
                        $('sv-supply-legend').innerHTML = parts.map(([k, v, c]) => `<span><i style="background: ${c};"></i><strong>${k}</strong> ${n0(v)} SOVR <span style="color: var(--text-muted);">(${pct(v, s.total)})</span></span>`).join('');
                        const nice = (x) => String(x || '').replace(/_/g, ' ');
                        $('sv-buckets').innerHTML = (o.latentBuckets || []).length ? 'Not yet released, by purpose: ' + o.latentBuckets.map(b => `${esc(nice(b.name))} ${big(b.amount)}`).join(' · ')
                            + (o.auditOk != null ? ` · supply audit at block ${n0(o.auditHeight)}: ${o.auditOk ? 'balances match the ledger' : '<strong>mismatch reported by the chain</strong>'}` : '') : '';
                    }
                    // Staking and lockup.
                    const lk = o.lockup, q = o.claimQueue;
                    $('sv-staking').innerHTML =
                        tile('Unstaking period', st.unbondingDays ? n0(st.unbondingDays) + ' days' : '–', 'staked SOVR takes this long to become spendable') +
                        (st.communityTax != null ? tile('Community tax', (st.communityTax * 100).toFixed(0) + '%', 'of staking rewards, to the community pool') : '') +
                        (lk ? tile('Lockup', n0(lk.lockDays) + ' days', `then ${n0(lk.unbondingDays)}-day unbonding; vesting over ${n0(lk.vestingDays)} days${Number(lk.earlyUnlockPenaltyBps) ? ` · early unlock penalty ${(lk.earlyUnlockPenaltyBps / 100).toFixed(2)}%` : ''}`) : '') +
                        (q ? tile('Vesting positions', n0(q.positions), `${big(q.vesting)} SOVR vesting · ${big(q.next30d)} SOVR released in the next 30 days`) : '');
                    // Validators.
                    const vals = o.validators || [];
                    $('sv-val-count').textContent = vals.length ? `${vals.length} active` : '';
                    $('sv-vals').innerHTML = vals.map((v, i) => `<tr><td>${i + 1}</td><td>${esc(v.moniker || v.operatorAddress)}${v.jailed ? ' <span style="color: var(--danger, #c50f1f);">(jailed)</span>' : ''}</td><td class="num">${n0(v.tokens)}</td><td class="num">${pct(v.tokens, st.bonded)}</td><td class="num">${(v.commission * 100).toFixed(1)}%</td></tr>`).join('')
                        || '<tr><td colspan="5" style="text-align: center; color: var(--text-secondary);">No validators read.</td></tr>';
                    // Governance.
                    $('sv-gov').innerHTML = (o.proposals || []).map(p => `<tr><td>${esc(p.id)}</td><td>${esc(p.title)}</td><td style="text-transform: capitalize;">${esc(p.status)}</td><td style="white-space: nowrap;">${day(p.votingEnd)}</td></tr>`).join('')
                        || '<tr><td colspan="4" style="text-align: center; color: var(--text-secondary);">No proposals read.</td></tr>';
                    $('sv-source').innerHTML = `Read from the Sovren REST API (${esc(o.source || '')}); price from DefiLlama (CoinGecko “sovren”). Explorer: <a href="https://sovrscan.com" target="_blank" rel="noopener">SovrScan</a>. Your SOVR history (on Sovren and on Osmosis) is in the Tax Report and Sales &amp; Lots.`;
                };

                // Last visit's copy at once, then the fresh one.
                let old = null;
                try { old = JSON.parse(localStorage.getItem(key) || 'null'); } catch (e) { }
                if (old && old.j) render(old.j, old.t);
                fetch('/api/sovr/overview').then(r => r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status))).then(j => {
                    try { localStorage.setItem(key, JSON.stringify({ t: Date.now(), j })); } catch (e) { }
                    render(j, null);
                }).catch(e => { $('sv-status').textContent = (old ? 'Showing the last saved figures; ' : '') + 'could not read the Sovren chain just now (' + e.message + ').'; });
            })();
        </script>
        """;
}
