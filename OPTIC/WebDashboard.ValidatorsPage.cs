// Validators page: all validators with explorer-style staking figures, network totals and your own stake.
public static partial class WebDashboard
{
    static void AppendValidatorsExplorerCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(ValidatorsExplorerHtml);
    }

    const string ValidatorsExplorerHtml = """
        <style>
            .vx { grid-column: 1 / -1; }
            .vx .card-content { display: flex; flex-direction: column; gap: 14px; }
            .vx-note { font-size: 13px; color: var(--text-secondary); margin: 0; max-width: 1100px; line-height: 1.5; }
            .vx-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
            .vx-tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 10px; }
            .vx-tile { border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px; background: var(--surface); }
            .vx-tile .k { font-size: 12px; color: var(--text-secondary); }
            .vx-tile .v { font-size: 20px; font-weight: 700; margin-top: 2px; font-variant-numeric: tabular-nums; }
            .vx-tile .s { font-size: 12px; color: var(--text-muted); margin-top: 2px; }
            .vx .vx-tab, .vx .vx-btn { font-size: 12px; padding: 4px 12px; background: var(--surface); color: var(--text-primary); border: 1px solid var(--border); box-shadow: none; text-transform: none; letter-spacing: 0; font-weight: 600; min-height: 0; }
            .vx .vx-tab[aria-pressed="true"] { border-color: var(--accent-primary); color: var(--accent-primary); }
            .vx input.vx-search { flex: 1 1 240px; max-width: 360px; font-size: 13px; padding: 5px 8px; min-height: 0; }
            .vx td.num, .vx th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
            .vx th[data-sort] { cursor: pointer; user-select: none; }
            .vx th[data-sort]::after { content: ''; margin-left: 4px; }
            .vx th[data-dir="desc"]::after { content: '▼'; font-size: 9px; }
            .vx th[data-dir="asc"]::after { content: '▲'; font-size: 9px; }
            .vx-sub { display: block; font-size: 11px; color: var(--text-muted); white-space: nowrap; }
            .vx-mono { font-family: var(--font-mono, monospace); font-size: 11px; color: var(--text-muted); }
            .vx-badge { display: inline-block; font-size: 11px; font-weight: 600; border-radius: 10px; padding: 1px 8px; border: 1px solid currentColor; white-space: nowrap; }
            .vx-badge.ok { color: var(--success, #107c10); }
            .vx-badge.bad { color: var(--error); }
            .vx-badge.warn { color: var(--warning); }
            .vx-badge.muted { color: var(--text-muted); }
            .vx-bar { height: 4px; background: var(--border); border-radius: 2px; margin-top: 3px; overflow: hidden; min-width: 60px; }
            .vx-bar > i { display: block; height: 100%; background: var(--accent-primary); }
            .vx tr.vx-main { cursor: pointer; }
            .vx tr.vx-main.vx-mine > td:first-child { box-shadow: inset 3px 0 0 var(--accent-primary); }
            .vx tr.vx-detail > td { background: color-mix(in srgb, var(--accent-primary) 4%, transparent); }
            .vx-dl { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 6px 18px; font-size: 13px; padding: 6px 2px; }
            .vx-dl div { overflow-wrap: anywhere; }
            .vx-dl b { display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); }
            .vx table.data-table { min-width: 980px; }
            .vx details summary { cursor: pointer; font-size: 14px; font-weight: 600; }
            @media (max-width: 640px) {
                .vx-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
                .vx-tile .v { font-size: 17px; }
                .vx .data-table-wrap { overflow: visible; max-height: none !important; }
                .vx table.data-table { min-width: 0; display: block; }
                .vx table.data-table thead { display: none; }
                .vx table.data-table tbody { display: block; }
                .vx table.data-table tr.vx-main { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 6px 12px; padding: 10px 12px; margin: 0 0 8px; border: 1px solid var(--border); border-radius: 8px; }
                .vx table.data-table tr.vx-detail { display: block; margin: -6px 0 10px; }
                .vx table.data-table tr.vx-detail > td { display: block; }
                .vx table.data-table tr.vx-main td { display: block; border: none; padding: 0; text-align: left !important; white-space: normal; }
                .vx table.data-table tr.vx-main td::before { content: attr(data-label); display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); }
                .vx table.data-table tr.vx-main td.vx-wide { grid-column: 1 / -1; }
                .vx tr.vx-main.vx-mine > td:first-child { box-shadow: none; }
                .vx tr.vx-main.vx-mine { border-left: 4px solid var(--accent-primary); }
            }
        </style>
        <div class="card vx">
            <div class="card-content">
                <div id="vx-status" class="vx-note">Reading validators from the chain…</div>
                <div id="vx-tiles" class="vx-tiles" hidden></div>
                <div class="vx-row">
                    <button type="button" class="vx-tab" data-tab="active" aria-pressed="true">Active</button>
                    <button type="button" class="vx-tab" data-tab="inactive">Inactive</button>
                    <button type="button" class="vx-tab" data-tab="mine">Staked by me</button>
                    <button type="button" class="vx-tab" data-tab="all">All</button>
                    <input type="search" id="vx-search" class="vx-search" placeholder="Search name or address" autocomplete="off" spellcheck="false">
                    <button type="button" id="vx-refresh" class="vx-btn">Refresh</button>
                </div>
                <div id="vx-wallets" class="vx-row" style="font-size: 12px; color: var(--text-secondary);"></div>
                <div class="data-table-wrap"><table class="data-table"><thead><tr>
                    <th class="num" data-sort="rank">#</th>
                    <th data-sort="moniker">Validator</th>
                    <th class="num" data-sort="tokensOpt">Voting power (OPT)</th>
                    <th class="num" data-sort="cumulativeShare">Cumulative</th>
                    <th class="num" data-sort="commission">Commission</th>
                    <th class="num" data-sort="selfBondedOpt">Self-bonded (OPT)</th>
                    <th class="num" data-sort="delegators">Delegators</th>
                    <th class="num" data-sort="uptime">Uptime</th>
                    <th data-sort="status">Status</th>
                    <th class="num" data-sort="mine">Your stake (OPT)</th>
                </tr></thead><tbody id="vx-body"></tbody></table></div>
                <p class="vx-note">Click a validator for its addresses, description, balances and signing record. Voting power is the OPT bonded to it; cumulative shows how much of the network&#39;s stake sits with it and every larger validator. Uptime counts the blocks it signed in the chain&#39;s slashing window. Highlighted rows are validators your wallet is staked to.</p>
                <details id="vx-params-box" hidden>
                    <summary>Network staking parameters</summary>
                    <div id="vx-params" class="vx-dl" style="margin-top: 8px;"></div>
                </details>
            </div>
        </div>
        <script>
        (() => {
            const $ = (id) => document.getElementById(id);
            const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
            const fmtOpt = (v) => v == null ? '–' : Number(v).toLocaleString('en-US', { maximumFractionDigits: 2 });
            const fmtBig = (v) => v == null ? '–' : Math.abs(v) >= 1e9 ? (v / 1e9).toLocaleString('en-US', { maximumFractionDigits: 2 }) + 'B' : Math.abs(v) >= 1e6 ? (v / 1e6).toLocaleString('en-US', { maximumFractionDigits: 2 }) + 'M' : fmtOpt(v);
            const fmtPct = (v, d = 2) => v == null || !isFinite(v) ? '–' : (v * 100).toLocaleString('en-US', { maximumFractionDigits: d }) + '%';
            const fmtTime = (v) => v && !String(v).startsWith('1970') ? String(v).replace('T', ' ').replace(/\.\d+/, '').replace('Z', ' UTC').slice(0, 20) : '–';
            const short = (a) => a && a.length > 24 ? a.slice(0, 14) + '…' + a.slice(-6) : (a || '');
            const params = new URLSearchParams(location.search);
            // Your stake: the wallet in the link, else every saved Optio wallet added together.
            const wallet = params.get('address') || '';
            const myWallets = () => wallet ? [wallet] : (window.opticWallets?.list() || []).map(w => w.a);
            let data = null, tab = params.get('tab') || 'active', sortKey = 'tokensOpt', sortDir = 'desc', open = new Set();

            const statusBadge = (v) => v.status === 'bonded' ? '<span class="vx-badge ok">Active</span>'
                : v.status === 'jailed' ? '<span class="vx-badge bad">Jailed</span>'
                : v.status === 'unbonding' ? '<span class="vx-badge warn">Unbonding</span>'
                : '<span class="vx-badge muted">Inactive</span>';
            const mine = (v) => data?.you?.stake?.[v.operatorAddress] || 0;
            const sortVal = (v, k) => k === 'commission' ? v.commission.rate : k === 'uptime' ? v.uptime?.share : k === 'mine' ? mine(v) : k === 'moniker' ? (v.moniker || '').toLowerCase() : k === 'status' ? v.status : v[k];

            const tiles = () => {
                const n = data.network, p = n.pool, t = (k, v, s) => `<div class="vx-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s || ''}</div></div>`;
                const you = data.you;
                $('vx-tiles').hidden = false;
                $('vx-tiles').innerHTML =
                    t('Active validators', `${n.counts.active} <span style="font-size: 13px; font-weight: 400;">of ${esc(n.parameters.maxValidators ?? '–')}</span>`, `${n.counts.total} in total${n.counts.jailed ? `, ${n.counts.jailed} jailed` : ''}`) +
                    t('Bonded', fmtBig(p.bondedOpt) + ' OPT', `${fmtPct(p.bondedRatio)} of supply`) +
                    t('Not bonded (unbonding)', fmtBig(p.notBondedOpt) + ' OPT', 'unbonding or with inactive validators') +
                    t('Total supply', fmtBig(p.supplyOpt) + ' OPT', p.inflation ? `inflation ${fmtPct(p.inflation)}` : 'no x/mint inflation (rewards come from x/distro)') +
                    t('Unbonding period', `${n.parameters.unbondingDays ? Math.round(n.parameters.unbondingDays) : '–'} days`, `community pool ${fmtBig(p.communityPoolOpt)} OPT`) +
                    t('Latest block', Number(n.height || 0).toLocaleString('en-US'), fmtTime(n.blockTime)) +
                    (you ? t('Your stake', fmtBig(you.totalStakedOpt) + ' OPT', `${Object.keys(you.stake).length} validator(s) · rewards ${fmtOpt(you.totalRewardsOpt)} OPT`) : '');
                const q = n.parameters;
                $('vx-params-box').hidden = false;
                $('vx-params').innerHTML = [
                    ['Chain', esc(n.chainId)], ['Max active validators', esc(q.maxValidators)], ['Unbonding period', `${q.unbondingDays ? Math.round(q.unbondingDays) : '–'} days`],
                    ['Minimum commission', fmtPct(q.minCommission)], ['Community tax', fmtPct(q.communityTax)],
                    ['Signed blocks window', esc(q.signedBlocksWindow)], ['Minimum signed per window', fmtPct(q.minSignedPerWindow)],
                    ['Downtime jail', q.downtimeJailSeconds ? `${Math.round(q.downtimeJailSeconds / 60)} minutes` : '–'],
                    ['Slash for downtime', fmtPct(q.slashDowntime)], ['Slash for double signing', fmtPct(q.slashDoubleSign)],
                    ['Data', `${esc(n.source)} · ${fmtTime(n.asOfUtc)}`],
                ].map(([k, v]) => `<div><b>${k}</b>${v}</div>`).join('');
            };

            const detail = (v) => {
                const u = v.uptime, c = v.commission;
                const link = v.website ? `<a href="${esc(/^https?:\/\//i.test(v.website) ? v.website : 'https://' + v.website)}" target="_blank" rel="noopener noreferrer">${esc(v.website)}</a>` : '–';
                return `<div class="vx-dl">
                    <div><b>Operator address</b><span class="vx-mono">${esc(v.operatorAddress)}</span></div>
                    <div><b>Operator account</b><span class="vx-mono">${esc(v.accountAddress || '–')}</span>${v.accountAddress ? ` · <a href="/page/address-distributions?address=${encodeURIComponent(v.accountAddress)}">distributions</a>` : ''}</div>
                    <div><b>Consensus address</b><span class="vx-mono">${esc(v.consensusAddress || '–')}</span></div>
                    <div><b>Website</b>${link}</div>
                    <div><b>Identity</b>${esc(v.identity || '–')}</div>
                    <div><b>Security contact</b>${esc(v.securityContact || '–')}</div>
                    <div style="grid-column: 1 / -1;"><b>Details</b>${esc(v.details || '–')}</div>
                    <div><b>Commission</b>${fmtPct(c.rate)} now · max ${fmtPct(c.maxRate)} · can change ${fmtPct(c.maxChangeRate)} a day<span class="vx-sub">last changed ${fmtTime(c.updated)}</span></div>
                    <div><b>Self-bonded</b>${fmtOpt(v.selfBondedOpt)} OPT${v.selfBondShare != null ? ` (${fmtPct(v.selfBondShare)} of its stake)` : ''}<span class="vx-sub">minimum self-delegation ${fmtOpt(v.minSelfDelegationOpt)} OPT</span></div>
                    <div><b>Operator account balance</b>${fmtOpt(v.operatorBalanceOpt)} OPT</div>
                    <div><b>Commission not yet withdrawn</b>${fmtOpt(v.commissionOwedOpt)} OPT</div>
                    <div><b>Rewards owed to its delegators</b>${fmtOpt(v.outstandingRewardsOpt)} OPT</div>
                    <div><b>Signing record</b>${u ? `${u.missed} missed of the last ${u.window} blocks · signing since block ${esc(u.startHeight)}${u.tombstoned ? ' · <span class="vx-badge bad">Tombstoned</span>' : ''}${fmtTime(u.jailedUntil) !== '–' ? `<span class="vx-sub">jailed until ${fmtTime(u.jailedUntil)}</span>` : ''}` : 'No signing info'}</div>
                    ${v.status !== 'bonded' && v.unbondingHeight && v.unbondingHeight !== '0' ? `<div><b>Unbonding</b>since block ${esc(v.unbondingHeight)} · completes ${fmtTime(v.unbondingTime)}</div>` : ''}
                    ${mine(v) ? `<div><b>Your stake</b>${fmtOpt(mine(v))} OPT · pending rewards ${fmtOpt(data.you.rewards[v.operatorAddress])} OPT · <a href="/page/my-staking${wallet ? '?address=' + encodeURIComponent(wallet) : ''}">My Staking</a></div>` : ''}
                </div>`;
            };

            const render = () => {
                if (!data) return;
                const q = $('vx-search').value.trim().toLowerCase();
                let rows = data.network.validators.filter(v =>
                    tab === 'all' ? true : tab === 'active' ? v.status === 'bonded' : tab === 'mine' ? mine(v) > 0 : v.status !== 'bonded');
                if (q) rows = rows.filter(v => [v.moniker, v.operatorAddress, v.accountAddress, v.identity, v.website].some(x => String(x || '').toLowerCase().includes(q)));
                rows.sort((a, b) => {
                    const x = sortVal(a, sortKey), y = sortVal(b, sortKey);
                    if (x == null && y == null) return 0; if (x == null) return 1; if (y == null) return -1;
                    return (x < y ? -1 : x > y ? 1 : 0) * (sortDir === 'asc' ? 1 : -1);
                });
                document.querySelectorAll('.vx th[data-sort]').forEach(th => th.dataset.dir = th.dataset.sort === sortKey ? sortDir : '');
                $('vx-body').innerHTML = rows.map(v => {
                    const u = v.uptime, my = mine(v);
                    const upBadge = !u || u.share == null || v.status !== 'bonded' ? '–' : `<span class="vx-badge ${u.share >= 0.95 ? 'ok' : u.share >= (data.network.parameters.minSignedPerWindow || 0.5) ? 'warn' : 'bad'}">${fmtPct(u.share, 0)}</span><span class="vx-sub">${u.missed} missed</span>`;
                    return `<tr class="vx-main${my ? ' vx-mine' : ''}" data-op="${esc(v.operatorAddress)}">
                        <td class="num" data-label="Rank">${v.rank ?? '–'}</td>
                        <td class="vx-wide" data-label="Validator"><strong>${esc(v.moniker || '(no name)')}</strong><span class="vx-mono" style="display: block;">${esc(short(v.operatorAddress))}</span></td>
                        <td class="num" data-label="Voting power">${fmtOpt(v.tokensOpt)}<span class="vx-sub">${v.status === 'bonded' ? fmtPct(v.votingPowerShare) : 'not in active set'}</span></td>
                        <td class="num" data-label="Cumulative">${v.cumulativeShare != null ? fmtPct(v.cumulativeShare, 1) + `<div class="vx-bar"><i style="width: ${Math.min(100, v.cumulativeShare * 100).toFixed(1)}%;"></i></div>` : '–'}</td>
                        <td class="num" data-label="Commission">${fmtPct(v.commission.rate)}<span class="vx-sub">max ${fmtPct(v.commission.maxRate, 0)}</span></td>
                        <td class="num" data-label="Self-bonded">${fmtOpt(v.selfBondedOpt)}<span class="vx-sub">${v.selfBondShare != null ? fmtPct(v.selfBondShare) : ''}</span></td>
                        <td class="num" data-label="Delegators">${v.delegators == null ? '–' : v.delegators.toLocaleString('en-US')}</td>
                        <td class="num" data-label="Uptime">${upBadge}</td>
                        <td data-label="Status">${statusBadge(v)}</td>
                        <td class="num" data-label="Your stake">${my ? fmtOpt(my) : '–'}</td>
                    </tr>${open.has(v.operatorAddress) ? `<tr class="vx-detail"><td colspan="10">${detail(v)}</td></tr>` : ''}`;
                }).join('') || '<tr><td colspan="10" style="text-align: center; color: var(--text-secondary);">No validators match.</td></tr>';
                document.querySelectorAll('.vx-tab').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.tab === tab)));
            };

            const renderWallets = () => {
                const list = myWallets(), label = (a) => window.opticWallets?.label(a) || short(a);
                $('vx-wallets').innerHTML = wallet
                    ? `Your stake shown for ${esc(label(wallet))}. <a href="/page/validators${tab !== 'active' ? '?tab=' + encodeURIComponent(tab) : ''}">Show all my wallets</a>`
                    : list.length
                        ? `Your stake shown for all your Optio wallets: ${list.map(a => esc(label(a))).join(', ')}.`
                        : 'Save your wallet on <a href="/page/my-wallets">My Wallets</a> to see your own stake here.';
            };

            const load = async (refresh) => {
                $('vx-refresh').disabled = true;
                $('vx-status').textContent = 'Reading validators from the chain…';
                try {
                    const get = async (a, fresh) => {
                        const q = new URLSearchParams();
                        if (a) q.set('address', a);
                        if (fresh) q.set('refresh', '1');
                        const r = await fetch('/api/validators?' + q.toString());
                        const j = await r.json();
                        if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
                        return j;
                    };
                    // The first call refreshes the validator list if asked; the rest reuse it and add each wallet's stake.
                    const [first, ...rest] = myWallets();
                    const j = await get(first, refresh);
                    if (rest.length && j.you) {
                        for (const o of await Promise.all(rest.map(a => get(a, false)))) {
                            if (!o.you) continue;
                            for (const k of ['stake', 'rewards']) for (const [op, v] of Object.entries(o.you[k] || {})) j.you[k][op] = (j.you[k][op] || 0) + v;
                            j.you.totalStakedOpt += o.you.totalStakedOpt; j.you.totalRewardsOpt += o.you.totalRewardsOpt;
                        }
                        j.you.address = '';
                    }
                    data = j;
                    $('vx-status').textContent = `${j.network.counts.active} active of ${j.network.counts.total} validators · block ${Number(j.network.height || 0).toLocaleString('en-US')} · as of ${fmtTime(j.network.asOfUtc)}`;
                    tiles(); renderWallets(); render();
                } catch (error) {
                    $('vx-status').textContent = 'Could not read validators: ' + error.message;
                } finally { $('vx-refresh').disabled = false; }
            };

            document.querySelectorAll('.vx-tab').forEach(b => b.addEventListener('click', () => { tab = b.dataset.tab; render(); }));
            document.querySelectorAll('.vx th[data-sort]').forEach(th => th.addEventListener('click', () => {
                const k = th.dataset.sort;
                if (sortKey === k) sortDir = sortDir === 'asc' ? 'desc' : 'asc';
                else { sortKey = k; sortDir = ['moniker', 'rank', 'commission', 'status'].includes(k) ? 'asc' : 'desc'; }
                render();
            }));
            $('vx-search').addEventListener('input', render);
            $('vx-refresh').addEventListener('click', () => load(true));
            $('vx-body').addEventListener('click', (e) => {
                if (e.target.closest('a')) return;
                const row = e.target.closest('tr.vx-main');
                if (!row) return;
                const op = row.dataset.op;
                if (open.has(op)) open.delete(op); else open.add(op);
                render();
            });
            load(false);
        })();
        </script>
        """;
}
