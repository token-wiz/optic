using System.Net;

// Top navigation, grouped by what the user is doing. One table drives the menu, the active-group highlight and the
// one-line description under each page title.
public static partial class WebDashboard
{
    sealed record NavPage(string Slug, string Title, string Hint);
    sealed record NavGroup(string Label, NavPage[] Pages, bool AdminOnly = false);

    static readonly NavGroup[] NavGroups =
    {
        new("My OPT", new NavPage[]
        {
            new("my-wallets", "My Wallets", "Your saved wallets and exchanges"),
            new("address-distributions", "My Distributions", "Node and staking payouts to one wallet"),
            new("pnl", "Profit & Loss", "What your OPT cost and what it is worth"),
            new("sell-planner", "Sell Planner", "How much to sell for a target loss this year"),
            new("fifo-sales", "Sales & Lots", "Every sale and the lots it used: FIFO or identified"),
            new("my-staking", "My Staking", "Validators you stake with and your locked OPT"),
            new("my-titn", "My TITN", "THORWallet staking, rewards and holdings on Base"),
            new("wallet", "Wallet Lookup", "Balances and history of any address"),
        }),
        new("Taxes", new NavPage[]
        {
            new("tax-report", "Tax Report", "Income, sales and losses for one tax year"),
            new("liquidity-review", "Liquidity & Distribution Review", "Receipt-level market evidence and reviewed values"),
        }),
        new("Market", new NavPage[]
        {
            new("market-liquidity", "Market Liquidity", "Exchanges, Osmosis pools and sell quotes"),
            new("liquidity-evidence", "Liquidity Evidence", "Market history behind your payouts"),
            new("exchange-flows", "Exchange Flows", "Who sent the most OPT to exchanges without taking it back"),
        }),
        new("Network", new NavPage[]
        {
            new("distributions", "Distributions & Ledger", "Daily OPT paid out across the network"),
            new("top100", "Top 100 Wallets", "Largest OPT holders"),
            new("validators", "Validators", "Every validator: voting power, commission, uptime and balances"),
            new("locks", "Locks & Staking", "Locked and staked OPT"),
            new("counterparties", "Counterparties", "Who sends and receives OPT"),
            new("network", "Network Analysis", "Chain-wide activity"),
        }),
        new("System", new NavPage[]
        {
            new("status", "Node Status", "Health of the connected node"),
            new("sync", "Data Sync", "Local chain data sync"),
        }, AdminOnly: true),
    };

    static string NavMenuHtml(string page)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($@"
                    <li class=""menu-section""><a href=""/"" class=""menu-item{(page == "dashboard" ? " active" : "")}"">Dashboard</a></li>");
        foreach (var g in NavGroups)
        {
            var active = g.Pages.Any(p => p.Slug == page);
            if (g.AdminOnly) sb.Append(@"
                    <!--ADMIN-ONLY-->");
            sb.Append($@"
                    <li class=""menu-section{(active ? " has-active" : "")}"">
                        <span class=""menu-label"" tabindex=""0"">{WebUtility.HtmlEncode(g.Label)}</span>
                        <div class=""menu-dropdown"">");
            foreach (var p in g.Pages)
                sb.Append($@"
                            <a href=""/page/{p.Slug}"" class=""menu-item{(p.Slug == page ? " active" : "")}""><span>{WebUtility.HtmlEncode(p.Title)}</span><span class=""menu-hint"">{WebUtility.HtmlEncode(p.Hint)}</span></a>");
            sb.Append(@"
                        </div>
                    </li>");
            if (g.AdminOnly) sb.Append(@"
                    <!--/ADMIN-ONLY-->");
        }
        return sb.ToString();
    }

    static string PageHintHtml(string page)
    {
        var hint = NavGroups.SelectMany(g => g.Pages).FirstOrDefault(p => p.Slug == page)?.Hint;
        return hint is null ? "" : $@"<div class=""header-subtitle"">{WebUtility.HtmlEncode(hint)}</div>";
    }

    // Menu button shown on small screens; opens the menu as a full-width list.
    const string NavToggleHtml = @"
                <button type=""button"" class=""nav-toggle"" aria-expanded=""false"" aria-controls=""top-menu"" onclick=""var n=this.closest('.top-nav');var o=n.classList.toggle('nav-open');this.setAttribute('aria-expanded',o);this.textContent=o?'Close':'Menu';"">Menu</button>";

    // Phones and small tablets. Comes after the stylesheet's own breakpoints so it wins.
    const string MobileCss = @"
        .nav-toggle { display: none; }

        @media (max-width: 900px) {
            .top-nav-inner { flex-wrap: wrap; align-items: center; gap: 8px; padding: 8px 14px; }
            .brand-link { min-width: 0; }
            .brand-link img { height: 32px; }
            .nav-toggle {
                display: inline-flex; margin-left: auto; min-height: 40px; padding: 8px 14px;
                background: var(--surface); color: var(--text-primary); border: 1px solid var(--border);
                box-shadow: none; text-transform: none; letter-spacing: 0; font-size: 15px; font-weight: 650;
            }
            .nav-toggle:hover { transform: none; box-shadow: none; background: var(--surface); }
            .top-menu, .header-links { display: none; }
            .top-nav.nav-open .top-menu {
                display: flex; order: 3; width: 100%; flex-direction: column; align-items: stretch; gap: 0;
                overflow-y: auto; overflow-x: hidden; max-height: calc(100vh - 120px); padding: 4px 0;
            }
            .top-nav.nav-open .header-links {
                display: flex; order: 4; width: 100%; justify-content: flex-start; gap: 8px;
                border-top: 1px solid var(--border); padding: 8px 0 4px;
            }
            .menu-section { width: 100%; }
            .menu-label, .menu-section.has-active > .menu-label {
                display: block; width: 100%; padding: 12px 4px 4px; background: none; border: none;
                font-size: 12px; text-transform: uppercase; letter-spacing: 0.06em; color: var(--text-muted);
            }
            .menu-label::after { content: none; }
            .menu-dropdown {
                position: static; display: grid; gap: 2px; min-width: 0; padding: 0 0 6px;
                opacity: 1; visibility: visible; transform: none; box-shadow: none; border: none; background: transparent;
            }
            .menu-section > .menu-item, .menu-dropdown .menu-item { width: 100%; padding: 10px 12px; font-size: 15px; min-height: 44px; }
            .data-table th { top: 0; }
        }

        @media (max-width: 600px) {
            html, body { overflow-x: hidden; }
            main { padding: 12px; }
            header { padding: 12px 14px; }
            h1 { font-size: 19px; }
            .header-kicker { display: none; }
            .card-content { padding: 12px; }
            .card h2 { padding: 12px; font-size: 15px; }
            .report-cards, .metrics-grid { gap: 10px; }
            .metrics-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
            .metric-card { min-height: 0; padding: 14px; }
            /* Form fields stack full width; 16px text stops iOS zooming into fields. */
            .form-group { flex: 1 1 100% !important; max-width: 100%; min-width: 0; }
            input, select, textarea { font-size: 16px !important; max-width: 100%; }
            button { min-height: 44px; }
            .modal-content { max-width: calc(100vw - 24px); padding: 20px; }
            .data-table-wrap { -webkit-overflow-scrolling: touch; }
            .data-table th, .data-table td { padding: 8px 9px; }
        }
";

    const string NavCss = @"
        .menu-section.has-active > .menu-label {
            background: #eff6fc;
            border-color: #deecf9;
            color: var(--accent-primary);
        }

        .menu-dropdown .menu-item {
            display: flex;
            flex-direction: column;
            align-items: flex-start;
            gap: 1px;
            white-space: normal;
        }

        .menu-dropdown {
            min-width: 260px;
        }

        .menu-hint {
            font-size: 12px;
            font-weight: 400;
            color: var(--text-muted);
        }

        .menu-item.active .menu-hint {
            color: var(--accent-primary);
            opacity: 0.8;
        }
";
}
