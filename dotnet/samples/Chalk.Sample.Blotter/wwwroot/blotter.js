// The blotter's scroll container: tells the page where it is scrolled to, at most every 50 ms, so
// the page knows whether its first row is the blotter's first row or one it is holding in place.
window.blotter = {
    watchScroll(element, page) {
        let pending = false;
        const report = () => {
            pending = false;
            page.invokeMethodAsync("OnScrolled", element.scrollTop);
        };
        element.addEventListener("scroll", () => {
            if (!pending) {
                pending = true;
                setTimeout(report, 50);
            }
        }, { passive: true });

        // For checking from a script, where a page in the background is sent no scroll events.
        element.reportScroll = report;

        // For the share button, which asks the page for its link, and for Escape.
        element.page = page;

        // Scrolled down, the row cut by the top edge fades; at the top, the newest row does not.
        const frame = element.parentElement;
        element.addEventListener("scroll", () => {
            frame.toggleAttribute("data-scrolled", element.scrollTop > 0);
        }, { passive: true });
    },

    // Fixes every column at the width it has now, with rows in it, and lays the table out by those
    // widths from then on, rather than by the rows each render brings: so the headings do not shift
    // as the grid scrolls. A value wider than its column is cut short with an ellipsis. Not while a
    // row is still a placeholder, whose empty cells would make its columns too narrow.
    freezeColumns(element) {
        const table = element.querySelector("table");
        if (!table) {
            return false;
        }

        if (table.style.tableLayout === "fixed") {
            return true;
        }

        if (!table.querySelector("tbody span.cell") || table.querySelector(".grid-cell-placeholder")) {
            return false;
        }

        // A heading keeps room for its sort arrow whether or not the column is sorted; a little more
        // for the rank a sort by several columns adds.
        const heads = [...table.querySelectorAll("thead th")];
        const widths = heads.map(th => th.getBoundingClientRect().width + (th.classList.contains("expander") ? 0 : 2));
        heads.forEach((th, i) => { th.style.width = `${widths[i]}px`; });
        table.style.minWidth = `${widths.reduce((sum, width) => sum + width, 0)}px`;
        table.style.tableLayout = "fixed";

        // The frozen columns stay in place as the grid scrolls sideways, each as far from the left as
        // the frozen columns before it are wide: known now the widths are fixed.
        table.id ||= `blotter-${Math.random().toString(36).slice(2)}`;
        const rules = [];
        let left = 0;
        heads.forEach((th, i) => {
            if (th.classList.contains("frozen")) {
                rules.push(`#${table.id} tr > :nth-child(${i + 1}) { left: ${left}px; }`);
                left += widths[i];
            }
        });
        document.getElementById(`${table.id}-pins`)?.remove();
        const style = document.createElement("style");
        style.id = `${table.id}-pins`;
        style.textContent = rules.join("\n");
        document.head.appendChild(style);

        // An attribute of the page's own, which the grid does not render and so never resets.
        table.dataset.pinned = "";
        return true;
    },

    // Lets the columns size themselves again, as a different set of them needs; freezeColumns fixes them anew.
    unfreezeColumns(element) {
        const table = element.querySelector("table");
        if (!table) {
            return;
        }

        table.querySelectorAll("thead th").forEach(th => { th.style.width = ""; });
        table.style.minWidth = "";
        table.style.tableLayout = "";
        delete table.dataset.pinned;
        document.getElementById(`${table.id}-pins`)?.remove();
    },

    scrollToTop(element) {
        element.scrollTo({ top: 0, behavior: "instant" });
    },

    // Scrolls to a place, once the grid is tall enough to: it may not be yet, its rows' count just read.
    // Then tells the page where it is, as a scroll would.
    scrollTo(element, top) {
        let tries = 0;
        const go = () => {
            element.scrollTo({ top, behavior: "instant" });
            if (Math.abs(element.scrollTop - top) > 1 && ++tries < 40) {
                setTimeout(go, 50);
            } else {
                element.reportScroll();
            }
        };
        go();
    },

    // Copies the rows in view, at least half of each, under the headings of the columns shown: as text
    // a tab between values, which a sheet takes as cells, and as a table, for what pastes one. Each
    // value as the grid shows it, but a time with its date and an id whole.
    copyRows(button) {
        const scroller = document.querySelector(".scroller");
        const table = scroller?.querySelector("table");
        if (!table) {
            return;
        }

        // Below the headings, which stay in place as the grid scrolls (the cells do; their row does not).
        const box = scroller.getBoundingClientRect();
        const heads = [...table.querySelectorAll("thead th")];
        const below = heads[heads.length - 1].getBoundingClientRect().bottom;
        const shown = heads.map((th, i) => i).filter(i => !heads[i].classList.contains("expander"));
        const label = th => [...(th.querySelector(".sort")?.childNodes ?? [])]
            .filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join("").trim();
        const value = td => {
            const cell = td?.querySelector(".cell");
            return !cell ? "" : cell.classList.contains("id") && cell.title ? cell.title : cell.textContent.replace("↗", "").trim();
        };
        const rows = [...table.querySelectorAll("tbody tr")]
            .filter(tr => tr.querySelector(".cell"))
            .filter(tr => {
                const r = tr.getBoundingClientRect();
                return Math.min(r.bottom, box.bottom) - Math.max(r.top, below) >= r.height / 2;
            })
            .map(tr => shown.map(i => value(tr.children[i])));
        const titles = shown.map(i => label(heads[i]));

        const text = [titles, ...rows].map(row => row.join("\t")).join("\n");
        const escape = s => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
        const html = "<table><thead><tr>" + titles.map(t => `<th>${escape(t)}</th>`).join("") + "</tr></thead><tbody>"
            + rows.map(row => "<tr>" + row.map(v => `<td>${escape(v)}</td>`).join("") + "</tr>").join("") + "</tbody></table>";
        navigator.clipboard.write([new ClipboardItem({
            "text/plain": new Blob([text], { type: "text/plain" }),
            "text/html": new Blob([html], { type: "text/html" }),
        })]).then(
            () => this.say(button, `${rows.length} rows copied`),
            failure => this.say(button, `Not copied: ${failure.message}`));
    },

    // Opens or closes the link menu under the link icon; a click anywhere else closes it.
    toggleShare(button) {
        const wrap = button.closest(".share-wrap");
        if (wrap.toggleAttribute("data-open")) {
            const close = event => {
                if (!wrap.contains(event.target)) {
                    wrap.removeAttribute("data-open");
                    document.removeEventListener("click", close, true);
                }
            };
            document.addEventListener("click", close, true);
        }
    },

    // Copies a link to the view, private (its data shrouded) or readable: the page writes it, and the
    // clipboard is handed the promise of it in the click itself, as Safari asks.
    shareView(item, shrouded) {
        const wrap = item.closest(".share-wrap");
        const icon = wrap.querySelector(".icon-button");
        wrap.removeAttribute("data-open");
        const scroller = document.querySelector(".scroller");
        const link = scroller.page.invokeMethodAsync("ShareLink", scroller.scrollTop, shrouded);
        navigator.clipboard.write([new ClipboardItem({
            "text/plain": link.then(url => new Blob([url], { type: "text/plain" })),
        })]).then(
            () => this.say(icon, shrouded ? "Private link copied" : "Readable link copied"),
            failure => this.say(icon, `Not copied: ${failure.message}`));
    },

    // A word under a button, for a moment.
    say(button, words) {
        button.dataset.said = words;
        clearTimeout(button.saying);
        button.saying = setTimeout(() => delete button.dataset.said, 1800);
    },

    // Holds what is in view in place while rows are let in above it: notes the first order row in
    // view and where it sits in the scrolled content, and as soon as the page changes, scrolls by as
    // far as the row has moved down the content, before the browser paints; then tells the page,
    // which lets in no more until then. Where the row sits in the content, not on the screen, so a
    // scroll the reader makes meanwhile is kept; by a row, not by height, because a table of millions
    // of rows is taller than a browser lets an element be, so its height does not grow.
    insertAbove(element, page) {
        let done = false;
        let observer = null;
        const finish = () => {
            if (!done) {
                done = true;
                observer?.disconnect();
                page.invokeMethodAsync("OnRevealed");
            }
        };

        const box = element.getBoundingClientRect();
        const header = element.querySelector("thead")?.getBoundingClientRect().height ?? 0;
        const first = [...element.querySelectorAll("tbody span.cell.id[title]")]
            .find(cell => cell.getBoundingClientRect().top >= box.top + header);
        if (!first) {
            finish();
            return;
        }

        const id = first.title;
        const down = cell => cell.getBoundingClientRect().top - box.top + element.scrollTop;
        const was = down(first);
        observer = new MutationObserver(() => {
            const now = [...element.querySelectorAll("tbody span.cell.id[title]")].find(cell => cell.title === id);
            const moved = now ? down(now) - was : 0;
            if (Math.abs(moved) >= 1) {
                element.scrollTop += moved;
                finish();
            }
        });
        observer.observe(element, { childList: true, subtree: true, characterData: true });
        setTimeout(finish, 2000);
    },
};

// Escape closes what is open over the page: the link menu here, the grid's editor, alerts and column
// chooser on the grid, and the splash that asks for sound.
document.addEventListener("keydown", event => {
    // An editor that took the key, closing its list of suggestions, leaves the rest open.
    if (event.key !== "Escape" || event.defaultPrevented) {
        return;
    }

    document.querySelectorAll(".share-wrap[data-open]").forEach(wrap => wrap.removeAttribute("data-open"));
    document.querySelector(".scroller")?.page?.invokeMethodAsync("OnEscape");
    window.blotterAlerts?.escape();
});
