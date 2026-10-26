// Reminders bell in the top bar (every page): unlock dates and rewards ready to claim. The dashboard works them out from
// your staked positions and keeps the latest list in this browser, so the bell shows them on every other page too. Each
// can go to your calendar as an .ics file, so the alert itself comes from your own calendar app.
// Selling days of a followed OPT Sell Plan come from the server (/api/sell-plan/today) on every page.
public static partial class WebDashboard
{
    const string BellHtml = """

                <div class="nav-bell" id="nav-bell">
                    <button type="button" class="nav-bell-btn" id="nav-bell-btn" aria-label="Reminders" aria-haspopup="true" aria-expanded="false" aria-controls="nav-bell-panel" title="Reminders">
                        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9"/><path d="M10.3 21a1.94 1.94 0 0 0 3.4 0"/></svg>
                        <span class="nav-bell-count" id="nav-bell-count" hidden></span>
                    </button>
                    <div class="nav-bell-panel" id="nav-bell-panel" role="dialog" aria-label="Reminders" hidden>
                        <div class="nav-bell-head"><strong>Reminders</strong><span id="nav-bell-note"></span><button type="button" id="nav-bell-all">Add all to calendar</button></div>
                        <div id="nav-bell-list"></div>
                    </div>
                </div>
                <style>
                    .nav-bell { position: relative; order: 5; flex: 0 0 auto; }
                    .nav-bell-btn { position: relative; display: inline-flex; align-items: center; justify-content: center; width: 40px; height: 40px; min-height: 40px; padding: 0;
                        background: var(--surface); color: var(--text-primary); border: 1px solid var(--border); border-radius: 8px; box-shadow: none; text-transform: none; letter-spacing: 0; }
                    .nav-bell-btn:hover { transform: none; box-shadow: none; color: var(--accent-primary); }
                    .nav-bell-count { position: absolute; top: -5px; right: -5px; min-width: 20px; height: 20px; padding: 0 5px; border-radius: 10px; background: #d13438; color: #fff;
                        border: 2px solid var(--bg-secondary, #fff); box-sizing: content-box; font-size: 12px; font-weight: 700; line-height: 20px; text-align: center; }
                    .nav-bell-panel { position: absolute; top: calc(100% + 8px); right: 0; z-index: 1000; width: min(400px, calc(100vw - 24px)); max-height: min(70vh, 560px); overflow-y: auto;
                        background: var(--surface, #fff); border: 1px solid var(--border); border-radius: 10px; box-shadow: 0 10px 30px rgba(0,0,0,0.25); padding: 10px 12px; text-align: left; }
                    .nav-bell-head { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 6px; font-size: 14px; }
                    .nav-bell-head span { font-size: 12px; color: var(--text-secondary); }
                    .nav-bell-head button, .nav-bell-item button { font-size: 11px; padding: 2px 10px; min-height: 0; margin-left: auto; text-transform: none; letter-spacing: 0; }
                    .nav-bell-item { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; padding: 6px 0; border-top: 1px solid var(--border); font-size: 13px; }
                    .nav-bell-item .when { min-width: 88px; font-weight: 600; }
                    .nav-bell-item .ready { color: var(--success, #107c10); }
                    .nav-bell-item .what { flex: 1 1 180px; }
                    .nav-bell-item .what a { color: inherit; font-weight: 600; }
                    .nav-bell-item .sub { font-size: 11px; color: var(--text-secondary); }
                    .nav-bell-empty { font-size: 13px; color: var(--text-secondary); padding: 6px 0; }
                    @media (max-width: 900px) {
                        .nav-bell { order: 1; margin-left: auto; }
                        .nav-bell ~ .nav-toggle { margin-left: 0; order: 2; }
                        .nav-bell-panel { position: fixed; top: 60px; left: 12px; right: 12px; width: auto; }
                    }
                </style>
                <script>
                    (function () {
                        const $ = (id) => document.getElementById(id);
                        const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                        const dayMs = 86400000;
                        const key = () => 'optic.reminders.' + ((window.opticUser || {}).name || '');
                        let items = [], loadedAt = 0, sellItems = [], titnItems = [];
                        const load = () => { try { const j = JSON.parse(localStorage.getItem(key()) || 'null'); if (j && j.items) { items = j.items; loadedAt = j.t; } } catch (e) { } };
                        // An all-day calendar event on the date, with an alert the evening before.
                        const ics = (list) => {
                            const d = (t) => new Date(t).toISOString().slice(0, 10).replace(/-/g, '');
                            const txt = (v) => String(v).replace(/[\\,;]/g, m => '\\' + m).replace(/\n/g, ' ');
                            const stamp = new Date().toISOString().replace(/[-:]/g, '').slice(0, 15) + 'Z';
                            return ['BEGIN:VCALENDAR', 'VERSION:2.0', 'PRODID:-//OPTIC//Reminders//EN'].concat(list.flatMap((r, i) => ['BEGIN:VEVENT', `UID:optic-${d(r.t)}-${i}-${r.title.length}@optic`, `DTSTAMP:${stamp}`, `DTSTART;VALUE=DATE:${d(r.t)}`, `DTEND;VALUE=DATE:${d(r.t + dayMs)}`,
                                `SUMMARY:${txt(r.title)}`, `DESCRIPTION:${txt(r.detail + ' (from OPTIC)')}`, 'BEGIN:VALARM', 'TRIGGER:-PT15H', 'ACTION:DISPLAY', `DESCRIPTION:${txt(r.title)}`, 'END:VALARM', 'END:VEVENT']), ['END:VCALENDAR']).join('\r\n');
                        };
                        const saveIcs = (list, name) => {
                            const a = document.createElement('a');
                            a.href = URL.createObjectURL(new Blob([ics(list)], { type: 'text/calendar' }));
                            a.download = name;
                            a.click();
                            setTimeout(() => URL.revokeObjectURL(a.href), 1000);
                        };
                        // Upcoming items are those within 400 days; days left are worked out now, not when the list was saved.
                        const current = () => sellItems.concat(titnItems, items).map(r => ({ ...r, days: r.ready ? 0 : Math.ceil((r.t - Date.now()) / dayMs) }))
                            .filter(r => r.ready || (r.days > 0 && r.days <= 400));
                        const render = () => {
                            const list = current(), ready = list.filter(r => r.ready).length, soon = list.filter(r => !r.ready && r.days <= 14).length;
                            const badge = $('nav-bell-count');
                            badge.hidden = !(ready + soon);
                            badge.textContent = ready + soon;
                            $('nav-bell-btn').title = $('nav-bell-btn').ariaLabel = list.length ? `Reminders: ${ready} ready now${soon ? `, ${soon} in the next 2 weeks` : ''}` : 'Reminders';
                            const next = list.find(r => !r.ready);
                            $('nav-bell-note').textContent = ready ? `${ready} ready now` : next ? `next in ${next.days} day${next.days === 1 ? '' : 's'}` : '';
                            $('nav-bell-all').hidden = !list.some(r => !r.ready);
                            const onHome = location.pathname === '/' || location.pathname.endsWith('/my-wallets');
                            $('nav-bell-list').innerHTML = list.map((r, i) => `<div class="nav-bell-item">
                                <span class="when${r.ready ? ' ready' : ''}">${r.ready ? 'Ready now' : new Date(r.t).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' })}</span>
                                <span class="what">${r.url ? `<a href="${esc(r.url)}">${esc(r.title)}</a>` : esc(r.title)}${r.ready ? '' : ` <span class="sub">(in ${r.days} day${r.days === 1 ? '' : 's'})</span>`}<div class="sub">${esc(r.detail)}</div></span>
                                ${r.ready ? '' : `<button type="button" data-remind="${i}">Add to calendar</button>`}</div>`).join('')
                                || `<div class="nav-bell-empty">${loadedAt ? 'Nothing to claim and no unlocks in the next 400 days.' : onHome ? 'Reading your stakes…' : 'Open the <a href="/">Dashboard</a> once to load your reminders.'}</div>`;
                            if (loadedAt && !onHome) $('nav-bell-list').insertAdjacentHTML('beforeend', `<div class="nav-bell-empty" style="font-size: 11px;">As of ${new Date(loadedAt).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })} · <a href="/">refresh on the Dashboard</a></div>`);
                        };
                        // Called by the dashboard each time it works the list out.
                        window.opticReminders = {
                            set(list) {
                                items = list.map(r => ({ t: r.t, ready: !!r.ready, title: r.title, detail: r.detail }));
                                loadedAt = Date.now();
                                try { localStorage.setItem(key(), JSON.stringify({ t: loadedAt, items })); } catch (e) { }
                                render();
                            }
                        };
                        const open = (on) => { $('nav-bell-panel').hidden = !on; $('nav-bell-btn').setAttribute('aria-expanded', on); };
                        $('nav-bell-btn').addEventListener('click', (e) => { e.stopPropagation(); open($('nav-bell-panel').hidden); });
                        document.addEventListener('click', (e) => { if (!$('nav-bell').contains(e.target)) open(false); });
                        document.addEventListener('keydown', (e) => { if (e.key === 'Escape') open(false); });
                        $('nav-bell-list').addEventListener('click', (e) => {
                            const b = e.target.closest('[data-remind]');
                            if (b) saveIcs([current()[Number(b.dataset.remind)]], 'optic-reminder.ics');
                        });
                        $('nav-bell-all').addEventListener('click', () => { const up = current().filter(r => !r.ready); if (up.length) saveIcs(up, 'optic-reminders.ics'); });
                        // The user's name arrives with the page's settings script, after this one.
                        // Today's sale from the OPT Sell Plan you follow (nothing when no plan is saved).
                        const fmt = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 0 });
                        const loadSell = () => fetch('/api/sell-plan/today?tz=' + encodeURIComponent((() => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch (e) { return ''; } })())).then(r => r.ok ? r.json() : null).then(j => {
                            sellItems = [];
                            if (j && j.active) {
                                const st = j.status === 'behind' ? `behind the plan by ${fmt(j.behindBy)} OPT` : j.status === 'ahead' ? `ahead of the plan by ${fmt(j.aheadBy)} OPT` : j.status === 'done' ? 'goal reached' : 'on track';
                                if (j.leftToday >= Math.max(1000, j.target * 0.02))
                                    sellItems.push({ t: Date.now(), ready: true, url: '/page/opt-sell-plan', title: `Sell about ${fmt(j.leftToday)} OPT today`, detail: `OPT Sell Plan · ${st} · ${fmt(j.soldTotal)} of ${fmt(j.goalOpt)} sold` });
                                else if (j.next)
                                    sellItems.push({ t: Date.parse(j.next.date + 'T00:00:00Z'), ready: false, url: '/page/opt-sell-plan', title: `Sell about ${fmt(j.next.opt)} OPT`, detail: `OPT Sell Plan · ${st}${j.soldToday > 0 ? ` · ${fmt(j.soldToday)} sold today` : ''}` });
                            }
                            render();
                        }).catch(() => { });
                        window.opticReminders.refreshSell = loadSell;
                        // Big TITN treasury moves and vesting claims from the last 3 days (when TITN alerts are on).
                        const loadTitn = () => fetch('/api/titn/watch/recent').then(r => r.ok ? r.json() : null).then(j => {
                            titnItems = ((j && j.items) || []).map(x => ({ t: Date.now(), ready: true, url: '/page/titn', title: x.text, detail: 'TITN unlock · ' + new Date(x.time).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }) }));
                            render();
                        }).catch(() => { });
                        window.opticReminders.refreshTitn = loadTitn;
                        const init = () => { load(); render(); loadSell(); loadTitn(); };
                        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
                    })();
                </script>
""";
}
