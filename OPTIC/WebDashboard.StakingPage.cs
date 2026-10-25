// My Staking page: validators your wallets are staked to and their locked OPT. All saved Optio wallets by default.
public static partial class WebDashboard
{
    static void AppendMyStakingCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(MyStakingHtml);
    }

    const string MyStakingHtml = """
        <style>
            .ms { grid-column: 1 / -1; }
            .ms .card-content { display: flex; flex-direction: column; gap: 14px; }
            .ms-note { font-size: 13px; color: var(--text-secondary); margin: 0; max-width: 1100px; line-height: 1.5; }
            .ms-row { display: flex; gap: 10px; flex-wrap: wrap; align-items: flex-end; }
            .ms-row .form-group { margin-bottom: 0; }
            .ms-tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 10px; }
            .ms-tile { border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px; background: var(--surface); }
            .ms-tile .k { font-size: 12px; color: var(--text-secondary); }
            .ms-tile .v { font-size: 20px; font-weight: 700; margin-top: 2px; font-variant-numeric: tabular-nums; }
            .ms-tile .s { font-size: 12px; color: var(--text-muted); margin-top: 2px; }
            .ms td.num, .ms th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
            .ms-sub { display: block; font-size: 11px; color: var(--text-muted); }
            .ms-badge { display: inline-block; font-size: 11px; font-weight: 600; border-radius: 10px; padding: 1px 8px; border: 1px solid currentColor; }
            .ms-badge.ok { color: var(--success, #107c10); }
            .ms-badge.bad { color: var(--error); }
            .ms-badge.muted { color: var(--text-muted); }
            .ms .lr-small, .ms .ms-wallet { font-size: 12px; padding: 3px 10px; background: var(--surface); color: var(--text-primary); border: 1px solid var(--border); box-shadow: none; text-transform: none; letter-spacing: 0; font-weight: 600; min-height: 0; }
            .ms .ms-wallet[aria-pressed="true"] { border-color: var(--accent-primary); color: var(--accent-primary); }
            .ms-plan { display: flex; gap: 14px; flex-wrap: wrap; align-items: center; font-size: 13px; padding: 8px 12px; margin: 6px 0 8px; border: 1px solid var(--border); border-radius: 8px; }
            .ms-plan strong { font-variant-numeric: tabular-nums; }
            .ms-plan.over { border-color: var(--error); }
            .ms input.ms-amt { width: 130px; text-align: right; font-size: 13px; padding: 4px 6px; min-height: 0; }
            .ms .ms-max { font-size: 11px; padding: 2px 8px; margin-left: 4px; }
            .ms-warn { display: block; font-size: 11px; color: var(--error); }
            .ms td.ms-free { font-weight: 700; color: var(--success, #107c10); }
            .ms table.data-table { min-width: 760px; }
            @media (max-width: 640px) {
                .ms-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
                .ms-tile .v { font-size: 17px; }
                .ms .data-table-wrap { overflow: visible; max-height: none !important; }
                .ms table.data-table { min-width: 0; display: block; }
                .ms table.data-table thead { display: none; }
                .ms table.data-table tbody { display: block; }
                .ms table.data-table tr { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 6px 12px; padding: 10px 12px; margin: 0 0 8px; border: 1px solid var(--border); border-radius: 8px; }
                .ms table.data-table td { display: block; border: none; padding: 0; text-align: left !important; white-space: normal; overflow-wrap: anywhere; }
                .ms table.data-table td::before { content: attr(data-label); display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); }
                .ms table.data-table td.ms-wide { grid-column: 1 / -1; }
            }
        </style>
        <div class="card ms">
            <div class="card-content">
                <form id="ms-form" class="ms-row">
                    <div class="form-group" style="flex: 1 1 380px;"><label for="ms-address">Wallet (leave blank for all your wallets)</label><input type="text" id="ms-address" placeholder="All my Optio wallets, or paste an optio1... address" autocomplete="off" spellcheck="false"></div>
                    <button type="submit" id="ms-load">Show staking</button>
                </form>
                <div id="ms-status" class="ms-note">Shows the validators your wallets are staked to and their locked OPT.</div>
                <div id="ms-tiles" class="ms-tiles" hidden></div>

                <div id="ms-validators-box" hidden>
                    <h3 style="margin: 0 0 6px; font-size: 16px;">Validators you are staked to</h3>
                    <p class="ms-note">Locks belong to your account, not to a node: the chain only requires your total stake to stay at or above your total locked OPT. So the stake that isn&#39;t locked is <strong>one allowance shared by all nodes</strong>, and each node shows the most you could take from it. Take it all from one node or split it; what you plan on one node lowers what is left for the others. OPT already unbonding is not part of it: it is already unstaked and becomes free on its own.</p>
                    <div id="ms-plan" class="ms-plan"></div>
                    <div class="data-table-wrap"><table class="data-table"><thead><tr>
                        <th>Validator</th><th>Status</th><th class="num">Staked (OPT)</th><th class="num">Can unstake, up to (shared, OPT)</th><th class="num">Plan to unstake (OPT)</th><th class="num">Stays staked (OPT)</th><th class="num">Locked with it when created (OPT)</th><th class="num">Pending rewards (OPT)</th><th class="num">Unbonding (OPT)</th>
                    </tr></thead><tbody id="ms-validators"></tbody></table></div>
                </div>

                <div id="ms-locks-box" hidden>
                    <h3 style="margin: 0 0 6px; font-size: 16px;">Your locks</h3>
                    <p class="ms-note">On Optio a lock belongs to your account, not to a validator: the chain only requires your total staked to stay at or above your total locked. The validator shown is the one the OPT was delegated to in the transaction that created each lock, followed through any extensions; you may have redelegated since.</p>
                    <div class="data-table-wrap" style="margin-top: 6px;"><table class="data-table"><thead><tr>
                        <th>Unlocks</th><th class="num">Locked (OPT)</th><th>Delegated to when locked</th>
                    </tr></thead><tbody id="ms-locks"></tbody></table></div>
                </div>
                <div id="ms-notes" class="ms-note"></div>
            </div>
        </div>
        <script>
        (() => {
            const $ = (id) => document.getElementById(id);
            const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
            const fmtOpt = (v) => v == null ? '–' : Number(v).toLocaleString('en-US', { maximumFractionDigits: 2 });
            const fmtPct = (v) => v == null ? '–' : (v * 100).toLocaleString('en-US', { maximumFractionDigits: 1 }) + '%';
            const short = (a) => a && a.length > 24 ? a.slice(0, 14) + '…' + a.slice(-6) : (a || '');
            const input = $('ms-address');
            const params = new URLSearchParams(location.search);
            // Blank = every saved Optio wallet; an address in the box (or the link) shows just that one.
            input.value = params.get('address') || '';
            const label = (a) => window.opticWallets?.label(a) || short(a);

            const load = async () => {
                const address = input.value.trim().toLowerCase();
                if (address && !/^optio1[02-9ac-hj-np-z]{38,58}$/.test(address)) { $('ms-status').textContent = 'Enter an optio1... wallet address, or leave it blank for all your wallets.'; return; }
                const addrs = address ? [address] : (window.opticWallets?.list() || []).map(w => w.a);
                if (!addrs.length) { $('ms-status').innerHTML = 'Save your Optio wallets on <a href="/page/my-wallets">My Wallets</a>, or enter an address.'; return; }
                const many = addrs.length > 1;
                $('ms-load').disabled = true;
                $('ms-status').textContent = `Reading delegations, locks and lock history from the chain${many ? ' for your ' + addrs.length + ' wallets' : ''}…`;
                try {
                    const list = await Promise.all(addrs.map(async a => {
                        const r = await fetch('/api/staking/positions?address=' + encodeURIComponent(a));
                        const d = await r.json();
                        if (!r.ok) throw new Error((many ? label(a) + ': ' : '') + (d.error || ('HTTP ' + r.status)));
                        return { a, name: label(a), d };
                    }));
                    const add = (k) => list.reduce((s, w) => s + (Number(w.d.totals[k]) || 0), 0);
                    const t = { balanceOpt: add('balanceOpt'), stakedOpt: add('stakedOpt'), unbondingOpt: add('unbondingOpt'), lockedOpt: add('lockedOpt'), freeToUndelegateOpt: add('freeToUndelegateOpt'), rewardsOpt: add('rewardsOpt'),
                        unbondingEntries: list.flatMap(w => w.d.totals.unbondingEntries || []) };
                    // One row per wallet and validator; keys are wallet|operator so each wallet keeps its own allowance.
                    const rows = list.flatMap(w => w.d.validators.map(v => ({ ...v, wallet: w.a, key: w.a + '|' + v.operatorAddress })));
                    const locks = list.flatMap(w => w.d.locks.map(l => ({ ...l, wallet: w.a }))).sort((x, y) => String(x.unlockDate).localeCompare(String(y.unlockDate)));
                    const sub = (a) => many ? `<span class="ms-sub">${esc(label(a))}</span>` : '';
                    const asOf = list.map(w => w.d.asOfUtc).sort()[0];
                    $('ms-status').textContent = `${rows.length} validator position(s)${many ? ' across ' + list.map(w => w.name).join(', ') : ''} · as of ${String(asOf).replace('T', ' ').slice(0, 16)} UTC`;
                    const tile = (k, v, s) => `<div class="ms-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s || ''}</div></div>`;
                    $('ms-tiles').hidden = false;
                    $('ms-tiles').innerHTML =
                        tile('Total OPT', fmtOpt(t.balanceOpt + t.stakedOpt + t.unbondingOpt) + ' OPT', `Balance ${fmtOpt(t.balanceOpt)} + staked ${fmtOpt(t.stakedOpt)} + unbonding ${fmtOpt(t.unbondingOpt)}`) +
                        tile('Staked', fmtOpt(t.stakedOpt) + ' OPT', `${new Set(rows.filter(v => v.stakedOpt > 0).map(v => v.operatorAddress)).size} validator(s)`) +
                        tile('Locked', fmtOpt(t.lockedOpt) + ' OPT', `${locks.length} lock(s)`) +
                        tile('Can unstake (not locked)', fmtOpt(t.freeToUndelegateOpt) + ' OPT', many ? 'in total; each wallet has its own allowance (its staked minus its locked)' : 'in total, shared by all nodes (staked minus locked)') +
                        (t.unbondingOpt ? tile('Already unbonding', fmtOpt(t.unbondingOpt) + ' OPT', t.unbondingEntries.length ? 'free on ' + [...new Set(t.unbondingEntries.map(x => String(x.completes).slice(0, 10)))].join(', ') : '') : '') +
                        tile('Pending rewards', fmtOpt(t.rewardsOpt) + ' OPT', 'claimed when you unstake');
                    const statusBadge = (v) => v.jailed ? '<span class="ms-badge bad">Jailed</span>' : v.status === 'bonded' ? '<span class="ms-badge ok">Active</span>' : `<span class="ms-badge muted">${esc(v.status || 'unknown')}</span>`;
                    $('ms-validators-box').hidden = false;
                    state = { list, rows, plan: new Map() };
                    $('ms-validators').innerHTML = rows.map(v => `<tr data-op="${esc(v.key)}">
                        <td data-label="Validator" class="ms-wide"><strong>${esc(v.moniker)}</strong>${sub(v.wallet)}<span class="ms-sub address-cell" title="${esc(v.operatorAddress)}">${esc(short(v.operatorAddress))}${v.commission ? ' · commission ' + fmtPct(Number(v.commission)) : ''}</span></td>
                        <td data-label="Status">${statusBadge(v)}</td>
                        <td data-label="Staked (OPT)" class="num">${fmtOpt(v.stakedOpt)}<span class="ms-sub">${fmtPct(v.shareOfStake)} of ${many ? 'this wallet’s' : 'your'} stake</span></td>
                        <td data-label="Can unstake, up to (shared)" class="num ms-free" data-free></td>
                        <td data-label="Plan to unstake" class="num">${v.stakedOpt > 0 ? `<input type="number" class="ms-amt" min="0" step="any" placeholder="0" inputmode="decimal" aria-label="OPT to unstake from ${esc(v.moniker)}"><button type="button" class="lr-small ms-max">Max</button><span class="ms-warn" data-warn></span>` : '–'}</td>
                        <td data-label="Stays staked" class="num" data-stays></td>
                        <td data-label="Locked with it when created" class="num">${v.lockedWithThisValidatorOpt > 0 ? fmtOpt(v.lockedWithThisValidatorOpt) + v.lockedWithThisValidator.map(x => `<span class="ms-sub">${fmtOpt(x.opt)} until ${esc(x.unlockDate)}</span>`).join('') : '–'}</td>
                        <td data-label="Pending rewards" class="num">${fmtOpt(v.rewardsOpt)}</td>
                        <td data-label="Unbonding" class="num">${v.unbondingOpt > 0 ? fmtOpt(v.unbondingOpt) + (v.unbondingCompletes ? `<span class="ms-sub">done ${esc(String(v.unbondingCompletes).slice(0, 10))}</span>` : '') : '–'}</td>
                    </tr>`).join('') || `<tr><td colspan="9" style="text-align: center; color: var(--text-secondary);">${many ? 'None of your wallets is' : 'This wallet is not'} staked to any validator.</td></tr>`;
                    recompute();
                    $('ms-locks-box').hidden = false;
                    $('ms-locks').innerHTML = locks.map(l => `<tr>
                        <td data-label="Unlocks">${esc(l.unlockDate)}${sub(l.wallet)}</td>
                        <td data-label="Locked (OPT)" class="num">${fmtOpt(l.opt)}</td>
                        <td data-label="Delegated to when locked" class="ms-wide">${l.parts.map(p => p.validator ? `${fmtOpt(p.opt)} OPT · <strong>${esc(p.moniker)}</strong>` : `${fmtOpt(p.opt)} OPT · <span style="color: var(--text-secondary);">locked without naming a validator</span>`).join('<br>')}</td>
                    </tr>`).join('') || '<tr><td colspan="3" style="text-align: center; color: var(--text-secondary);">No active locks.</td></tr>';
                    const notes = [...new Set(list.flatMap(w => w.d.notes || []))];
                    $('ms-notes').innerHTML = notes.length ? `<ul>${notes.map(n => `<li>${esc(n)}</li>`).join('')}</ul>` : '';
                    history.replaceState(null, '', address ? '?address=' + encodeURIComponent(address) : location.pathname);
                } catch (e) {
                    $('ms-status').innerHTML = `<span style="color: var(--error);">${esc(e.message)}</span>`;
                } finally {
                    $('ms-load').disabled = false;
                }
            };
            // Unstaking planner. The chain rejects any undelegation that would leave a wallet's total staked below its total
            // locked, so each wallet's unlocked OPT is one allowance shared by every node that wallet is staked to.
            let state = null;
            const recompute = () => {
                if (!state) return;
                const many = state.list.length > 1;
                const lines = [];
                let over = false;
                for (const w of state.list) {
                    const free = Number(w.d.totals.freeToUndelegateOpt) || 0;
                    const mineRows = state.rows.filter(v => v.wallet === w.a);
                    const planned = mineRows.reduce((s, v) => s + (state.plan.get(v.key) || 0), 0);
                    const left = free - planned;
                    for (const v of mineRows) {
                        const row = document.querySelector(`#ms-validators tr[data-op="${CSS.escape(v.key)}"]`);
                        if (!row) continue;
                        const own = state.plan.get(v.key) || 0;
                        const maxHere = Math.max(0, Math.min(v.stakedOpt, free - (planned - own)));
                        row.querySelector('[data-free]').textContent = fmtOpt(maxHere);
                        row.querySelector('[data-stays]').textContent = fmtOpt(Math.max(0, v.stakedOpt - own));
                        const warn = row.querySelector('[data-warn]');
                        if (warn) warn.textContent = own > v.stakedOpt + 1e-6 ? 'More than staked on this node' : own > maxHere + 1e-6 ? 'Over this wallet’s unlocked allowance' : '';
                        row.dataset.max = String(maxHere);
                    }
                    if (left < -1e-6) over = true;
                    if (many && !mineRows.length) continue;
                    lines.push(`<span>${many ? `<strong>${esc(w.name)}</strong>: ` : ''}Unlocked allowance <strong>${fmtOpt(free)} OPT</strong></span><span>Planned <strong>${fmtOpt(planned)} OPT</strong></span><span>${left < -1e-6 ? `<strong style="color: var(--error);">Over by ${fmtOpt(-left)} OPT</strong>` : `Left <strong>${fmtOpt(left)} OPT</strong>`}</span>`);
                }
                const box = $('ms-plan');
                box.classList.toggle('over', over);
                box.innerHTML = lines.map(l => `<div style="display: flex; gap: inherit; flex-wrap: wrap; width: 100%;">${l}</div>`).join('') + (state.plan.size ? '<button type="button" class="lr-small" id="ms-plan-clear">Clear plan</button>' : '');
                $('ms-plan-clear')?.addEventListener('click', () => { state.plan.clear(); document.querySelectorAll('#ms-validators input.ms-amt').forEach(i => i.value = ''); recompute(); });
            };
            $('ms-validators').addEventListener('input', (e) => {
                if (!e.target.classList.contains('ms-amt') || !state) return;
                const op = e.target.closest('tr').dataset.op, v = Number(e.target.value);
                if (v > 0) state.plan.set(op, v); else state.plan.delete(op);
                recompute();
            });
            $('ms-validators').addEventListener('click', (e) => {
                if (!e.target.classList.contains('ms-max') || !state) return;
                const row = e.target.closest('tr'), inp = row.querySelector('input.ms-amt');
                const max = Math.floor(Number(row.dataset.max) * 1e6) / 1e6;
                inp.value = max > 0 ? String(max) : '';
                if (max > 0) state.plan.set(row.dataset.op, max); else state.plan.delete(row.dataset.op);
                recompute();
            });

            $('ms-form').addEventListener('submit', (e) => { e.preventDefault(); load(); });
            load();
        })();
        </script>
        """;
}
