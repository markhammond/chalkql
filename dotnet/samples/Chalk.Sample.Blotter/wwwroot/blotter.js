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

        // Each with room beyond what it took for a sort's arrow and rank, which come and go.
        const heads = [...table.querySelectorAll("thead th")];
        const widths = heads.map(th => th.getBoundingClientRect().width + (th.classList.contains("expander") ? 0 : 16));
        heads.forEach((th, i) => { th.style.width = `${widths[i]}px`; });
        table.style.minWidth = `${widths.reduce((sum, width) => sum + width, 0)}px`;
        table.style.tableLayout = "fixed";
        return true;
    },

    scrollToTop(element) {
        element.scrollTo({ top: 0, behavior: "instant" });
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
