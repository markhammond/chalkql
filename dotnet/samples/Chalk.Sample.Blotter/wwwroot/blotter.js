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

    scrollToTop(element) {
        element.scrollTo({ top: 0, behavior: "instant" });
    },
};
