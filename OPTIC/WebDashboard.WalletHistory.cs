// Wallet history sheet shared by pages that list wallets (Exchange Flows): how a wallet's OPT built up over time, its
// largest senders and recipients (tap one to follow it, Back returns), and the transfers between two wallets when one is
// opened from the other. Open with window.opticHistory.open(address, { subtitle }).
public static partial class WebDashboard
{
    static void AppendWalletHistoryScript(System.Text.StringBuilder sb) => sb.Append(WalletHistoryHtml);

    const string WalletHistoryHtml = """
        <style>
            .nm-tag { display: inline-block; font-weight: 700; border-radius: 6px; padding: 1px 7px; line-height: 1.45; overflow-wrap: anywhere; }
            .nm-named { background: color-mix(in srgb, var(--accent-primary) 16%, transparent); color: var(--accent-primary); }
            .nm-shared { background: color-mix(in srgb, #8250df 16%, transparent); color: #8250df; }
            .nm-mine { background: #f5a623; color: #1f1300; box-shadow: 0 0 0 1px #c47f00 inset; }
            .wh-btn { font-size: 11px; padding: 2px 10px; margin-top: 4px; min-height: 0; background: var(--surface); color: var(--accent-primary); border: 1px solid var(--accent-primary); box-shadow: none; text-transform: none; letter-spacing: 0; }
            .wh-modal { position: fixed; inset: 0; z-index: 1000; background: rgba(0, 0, 0, 0.45); overflow-y: auto; -webkit-overflow-scrolling: touch; }
            .wh-modal[hidden] { display: none; }
            .wh-panel { background: var(--bg-primary, var(--surface, #fff)); color: var(--text-primary); width: min(920px, calc(100% - 32px)); margin: 32px auto; border-radius: 10px; padding: 12px 16px 16px; box-shadow: 0 8px 30px rgba(0, 0, 0, 0.3); font-size: 13px; }
            .wh-head { display: flex; justify-content: space-between; gap: 8px; position: sticky; top: 0; background: inherit; padding: 4px 0 8px; z-index: 1; }
            .wh-mbtn { font-size: 13px; padding: 4px 12px; min-height: 0; background: var(--surface); color: var(--text-primary); border: 1px solid var(--border); box-shadow: none; text-transform: none; letter-spacing: 0; }
            .wh-title { display: flex; flex-direction: column; gap: 2px; margin-bottom: 8px; overflow-wrap: anywhere; }
            .wh-title > strong { font-size: 16px; }
            .wh-title > span { color: var(--text-secondary); }
            .wh-cp { background: none; border: none; box-shadow: none; padding: 0; min-height: 0; color: var(--accent-primary); text-decoration: underline; text-transform: none; letter-spacing: 0; font: inherit; font-weight: 600; text-align: left; cursor: pointer; overflow-wrap: anywhere; }
            .wh-cp .nm-tag { text-decoration: none; }
            .wh-sum { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 6px 14px; margin-bottom: 6px; }
            .wh-sum b, .wh-cols b, .wh-between > b { display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); }
            .wh-cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 12px; margin-top: 8px; }
            .wh-cols table { width: 100%; border-collapse: collapse; }
            .wh-cols td { padding: 3px 6px; border-bottom: 1px solid var(--border); }
            .wh-cols td.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
            .wh-between { border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; margin-bottom: 8px; }
            .wh-between-sum { display: flex; flex-wrap: wrap; gap: 4px 16px; margin: 4px 0 6px; }
            .wh-xfers { max-height: 260px; overflow-y: auto; padding-right: 14px; }
            .wh-xfers > div { display: grid; grid-template-columns: 92px minmax(0, 1fr) auto; gap: 8px; padding: 3px 0; border-bottom: 1px solid var(--border); font-variant-numeric: tabular-nums; }
            .wh-out { color: var(--error); }
            .wh-in { color: var(--success, #107c10); }
            .wh-months { max-height: 420px; overflow-y: auto; }
            .wh-mcard { border: 1px solid var(--border); border-radius: 6px; padding: 6px 8px; margin: 0 0 6px; background: var(--surface); }
            .wh-mhead { display: flex; justify-content: space-between; gap: 8px; font-variant-numeric: tabular-nums; }
            .wh-mflows { display: flex; flex-wrap: wrap; gap: 2px 12px; font-size: 12px; color: var(--text-secondary); margin-top: 2px; font-variant-numeric: tabular-nums; }
            @media (max-width: 640px) {
                .wh-panel { width: 100%; margin: 0; border-radius: 0; min-height: 100%; padding: 10px 12px 24px; }
                .wh-xfers > div { grid-template-columns: 78px minmax(0, 1fr) auto; font-size: 12px; }
            }
        </style>
        <script>
        (() => {
            if (window.opticHistory) return;
            const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
            const fmt = (v) => Math.round(v || 0).toLocaleString('en-US');
            const fmtShort = (v) => Math.abs(v) >= 1e9 ? (v / 1e9).toFixed(2) + 'B' : Math.abs(v) >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : Math.abs(v) >= 1e3 ? (v / 1e3).toFixed(0) + 'K' : Math.round(v).toString();
            const shortAddr = (a) => a && a.length > 20 ? a.slice(0, 10) + '…' + a.slice(-6) : (a || '');
            const DISTRO = 'optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst';
            let names = {}, globalNames = {};
            const namesReady = Promise.all([
                fetch('/api/address-book').then(r => r.ok ? r.json() : {}).catch(() => ({})),
                fetch('/api/address-book/global').then(r => r.ok ? r.json() : {}).catch(() => ({})),
            ]).then(([n, g]) => { names = n || {}; globalNames = g || {}; });
            const isMine = (a) => !!window.opticWallets?.list().some(x => x.a === a);
            // Name tags: your own wallets amber, names you gave blue, shared names purple.
            const tag = (a) => {
                if (isMine(a)) return `<span class="nm-tag nm-mine">★ ${esc(names[a] || window.opticWallets.label(a) || 'My wallet')}</span>`;
                if (names[a]) return `<span class="nm-tag nm-named">${esc(names[a])}</span>`;
                const g = globalNames[a];
                if (g) return `<span class="nm-tag nm-shared">${esc(g.name + (g.email ? ' · ' + g.email : ''))}</span>`;
                return '';
            };
            const who = (a) => tag(a) || esc(a === DISTRO ? 'Distro Wallet' : shortAddr(a));
            const ledgers = new Map();
            const getLedger = (a) => {
                if (!ledgers.has(a)) ledgers.set(a, fetch('/api/wallet/ledger?address=' + encodeURIComponent(a)).then(async r => {
                    const L = await r.json();
                    if (!r.ok) throw new Error(L.error || ('HTTP ' + r.status));
                    return L;
                }).catch(e => { ledgers.delete(a); throw e; }));
                return ledgers.get(a);
            };

            const build = (address, L) => {
                const genesis = globalNames[address]?.genesisOpt != null ? Number(globalNames[address].genesisOpt) : 0;
                const months = new Map(), ins = new Map(), outs = new Map();
                const tot = { distro: 0, rewards: 0, otherIn: 0, sent: 0, fees: 0 };
                for (const e of L.entries || []) {
                    if (e.category === 'module') continue;
                    const m = String(e.timeUtc).slice(0, 7), amt = Number(e.amountOpt) || 0;
                    if (!months.has(m)) months.set(m, { distro: 0, rewards: 0, otherIn: 0, sent: 0 });
                    const row = months.get(m);
                    if (e.direction === 'in') {
                        if (e.category === 'distribution') { row.distro += amt; tot.distro += amt; }
                        else if (e.category === 'reward') { row.rewards += amt; tot.rewards += amt; }
                        else { row.otherIn += amt; tot.otherIn += amt; ins.set(e.counterparty, (ins.get(e.counterparty) || 0) + amt); }
                    } else {
                        row.sent += amt;
                        if (e.category === 'fee') tot.fees += amt;
                        else { tot.sent += amt; outs.set(e.counterparty, (outs.get(e.counterparty) || 0) + amt); }
                    }
                }
                let cD = 0, cR = 0, cO = 0, cS = 0;
                const series = [...months.keys()].sort().map(m => {
                    const r = months.get(m);
                    cD += r.distro; cR += r.rewards; cO += r.otherIn; cS += r.sent;
                    return { m, ...r, cD, cR, cO, balance: genesis + cD + cR + cO - cS };
                });
                const net = genesis + tot.distro + tot.rewards + tot.otherIn - tot.sent - tot.fees;
                const W = 640, H = 190, padL = 46, padB = 22, padT = 8;
                const maxY = Math.max(1, ...series.map(s => genesis + s.cD + s.cR + s.cO));
                const x = (i) => padL + (series.length <= 1 ? (W - padL - 8) / 2 : i * (W - padL - 8) / (series.length - 1));
                const y = (v) => padT + (H - padT - padB) * (1 - v / maxY);
                const area = (top, bottom, color) => series.length ? `<path d="M${series.map((s, i) => `${x(i).toFixed(1)},${y(top(s)).toFixed(1)}`).join(' L')} L${series.map((s, i) => `${x(i).toFixed(1)},${y(bottom(s)).toFixed(1)}`).reverse().join(' L')} Z" fill="${color}" opacity="0.75"></path>` : '';
                const g0 = () => genesis, g1 = (s) => genesis + s.cD, g2 = (s) => g1(s) + s.cR, g3 = (s) => g2(s) + s.cO;
                const ticks = [0, 0.25, 0.5, 0.75, 1].map(f => `<line x1="${padL}" x2="${W - 8}" y1="${y(maxY * f)}" y2="${y(maxY * f)}" stroke="var(--border)" stroke-width="0.5"></line><text x="${padL - 4}" y="${y(maxY * f) + 3}" text-anchor="end" font-size="9" fill="var(--text-muted)">${fmtShort(maxY * f)}</text>`).join('');
                const step = Math.max(1, Math.ceil(series.length / 8));
                const labels = series.map((s, i) => i % step === 0 || i === series.length - 1 ? `<text x="${x(i)}" y="${H - 6}" text-anchor="middle" font-size="9" fill="var(--text-muted)">${s.m}</text>` : '').join('');
                const line = series.length ? `<polyline points="${series.map((s, i) => `${x(i).toFixed(1)},${y(Math.max(0, s.balance)).toFixed(1)}`).join(' ')}" fill="none" stroke="var(--text-primary)" stroke-width="1.6"></polyline>` : '';
                const key = (c, t) => `<span style="display: inline-flex; gap: 4px; align-items: center; margin-right: 12px;"><i style="width: 10px; height: 10px; background: ${c}; display: inline-block; border-radius: 2px;"></i>${t}</span>`;
                const cpRows = (list) => list.map(([a, v]) => `<tr><td><button type="button" class="wh-cp" data-wh-open="${esc(a)}" data-wh-parent="${esc(address)}">${who(a)}</button></td><td class="num">${fmt(v)}</td></tr>`).join('');
                const top = (map) => {
                    const list = [...map.entries()].sort((a, b) => b[1] - a[1]);
                    if (!list.length) return '<tr><td colspan="2" style="color: var(--text-secondary);">None</td></tr>';
                    return cpRows(list.slice(0, 6)) + (list.length > 6 ? `<tr><td colspan="2"><details><summary style="cursor: pointer; font-size: 12px;">All ${list.length}</summary><table><tbody>${cpRows(list.slice(6))}</tbody></table></details></td></tr>` : '');
                };
                const sum = (k, v) => `<div><b>${k}</b>${v}</div>`;
                return `<div class="wh-sum">${sum('Genesis allocation', fmt(genesis))}${sum('From Distro Wallet', fmt(tot.distro))}${sum('Staking rewards', fmt(tot.rewards))}${sum('From other wallets', fmt(tot.otherIn))}${sum('Sent out', fmt(tot.sent) + (tot.fees ? ` <span style="color: var(--text-muted); font-size: 11px;">+ ${fmt(tot.fees)} fees</span>` : ''))}${sum('Received − sent', fmt(net))}</div>
                    <div style="font-size: 12px; margin: 6px 0;">${genesis ? key('#8a8886', 'Genesis') : ''}${key('var(--accent-primary)', 'Distro Wallet')}${key('#107c10', 'Staking rewards')}${key('#ca5010', 'Other wallets')}${key('var(--text-primary)', 'Balance (received − sent)')}</div>
                    <svg viewBox="0 0 ${W} ${H}" style="width: 100%; max-width: 760px; height: auto; display: block;" role="img" aria-label="Cumulative OPT received and balance">${ticks}${genesis ? area(g0, () => 0, '#8a8886') : ''}${area(g1, g0, 'var(--accent-primary)')}${area(g2, g1, '#107c10')}${area(g3, g2, '#ca5010')}${line}${labels}</svg>
                    <div class="wh-cols"><div><b>Largest senders to this wallet</b><table><tbody>${top(ins)}</tbody></table></div><div><b>Largest recipients from this wallet</b><table><tbody>${top(outs)}</tbody></table></div></div>
                    <details style="margin-top: 6px;"><summary style="cursor: pointer;">Month by month</summary><div class="wh-months">${series.map(s => `<div class="wh-mcard"><div class="wh-mhead"><strong>${s.m}</strong><span>Balance <strong>${fmt(s.balance)}</strong></span></div><div class="wh-mflows">${s.distro ? `<span>Distro +${fmt(s.distro)}</span>` : ''}${s.rewards ? `<span>Rewards +${fmt(s.rewards)}</span>` : ''}${s.otherIn ? `<span>From others +${fmt(s.otherIn)}</span>` : ''}${s.sent ? `<span class="wh-out">Sent −${fmt(s.sent)}</span>` : ''}</div></div>`).join('')}</div></details>
                    <div style="font-size: 11px; color: var(--text-muted); margin-top: 6px;">From ${(L.entries || []).length.toLocaleString('en-US')} ledger entries on the Optio chain. Staking and unstaking are left out (the OPT stays in the wallet). Tap a sender or recipient to see its history.${L.truncated ? ' <strong>The history hit the 20,000-transaction cap; older entries are missing.</strong>' : ''}</div>`;
            };

            const modal = document.createElement('div');
            modal.className = 'wh-modal';
            modal.hidden = true;
            document.body.appendChild(modal);
            const stack = [];
            let token = 0;
            const render = async () => {
                const cur = stack[stack.length - 1];
                if (!cur) { modal.hidden = true; modal.innerHTML = ''; document.body.style.overflow = ''; return; }
                const my = ++token;
                modal.hidden = false;
                document.body.style.overflow = 'hidden';
                await namesReady;
                const a = cur.address, parent = cur.parent;
                const head = `<div class="wh-head">${stack.length > 1 ? '<button type="button" class="wh-mbtn" data-wh-back>‹ Back</button>' : '<span></span>'}<button type="button" class="wh-mbtn" data-wh-close aria-label="Close">Close ✕</button></div>
                    <div class="wh-title"><strong>${who(a)}</strong><span class="address-cell">${esc(a)}</span>${cur.subtitle ? `<span>${esc(cur.subtitle)}</span>` : ''}</div>`;
                const panel = (between, body) => `<div class="wh-panel">${head}${between}${body}</div>`;
                modal.innerHTML = panel('', '<div style="color: var(--text-secondary);">Reading…</div>');
                let between = '';
                if (parent) {
                    try {
                        const PL = await getLedger(parent);
                        const xs = (PL.entries || []).filter(e => e.counterparty === a && e.category !== 'fee');
                        const inSum = xs.filter(e => e.direction === 'in').reduce((s, e) => s + e.amountOpt, 0), outSum = xs.filter(e => e.direction !== 'in').reduce((s, e) => s + e.amountOpt, 0);
                        between = `<div class="wh-between"><b>Between ${who(parent)} and this wallet</b><div class="wh-between-sum">${inSum ? `<span>Sent to ${who(parent)}: <strong>${fmt(inSum)}</strong></span>` : ''}${outSum ? `<span>Received from ${who(parent)}: <strong>${fmt(outSum)}</strong></span>` : ''}${!xs.length ? '<span>No direct transfers found.</span>' : ''}</div>
                            ${xs.length ? `<div class="wh-xfers">${xs.slice().reverse().map(e => `<div><span>${String(e.timeUtc).slice(0, 10)}</span><span>${e.direction === 'in' ? 'Sent' : 'Received'}</span><strong class="${e.direction === 'in' ? 'wh-out' : 'wh-in'}">${e.direction === 'in' ? '−' : '+'}${fmt(e.amountOpt)}</strong></div>`).join('')}</div>` : ''}</div>`;
                    } catch { }
                }
                if (my !== token) return;
                modal.innerHTML = panel(between, '<div style="color: var(--text-secondary);">Reading this wallet&#39;s history from the chain… large wallets can take a minute.</div>');
                let body;
                try { body = build(a, await getLedger(a)); }
                catch (error) { body = `<div style="color: var(--error);">Could not read the history: ${esc(error.message)}</div>`; }
                if (my !== token) return;
                modal.innerHTML = panel(between, body);
                modal.scrollTop = 0;
            };
            document.addEventListener('click', (e) => {
                const o = e.target.closest('[data-wh-open]');
                if (o) { e.preventDefault(); stack.push({ address: o.dataset.whOpen, parent: o.dataset.whParent || null, subtitle: o.dataset.whSubtitle || '' }); render(); return; }
                if (e.target.closest('[data-wh-back]')) { stack.pop(); render(); return; }
                if (e.target.closest('[data-wh-close]') || e.target === modal) { stack.length = 0; render(); }
            });
            document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && stack.length) { stack.length = 0; render(); } });
            window.opticHistory = { open: (address, opts = {}) => { stack.length = 0; stack.push({ address, parent: null, subtitle: opts.subtitle || '' }); render(); } };
        })();
        </script>
        """;
}
