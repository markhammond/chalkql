// Alerts in the browser: the rules kept in local settings, the sounds, and drawing attention to a page
// in the background. The host decides when an alert fires; this only sounds it.
window.blotterAlerts = (() => {
    const key = "blotter.alerts";
    let audio = null;
    let page = null;
    let unseen = false;

    // Local settings may be off (a private window, blocked storage): the page works without them.
    const read = () => {
        try {
            return JSON.parse(localStorage.getItem(key) ?? "{}") ?? {};
        } catch {
            return {};
        }
    };
    const write = settings => {
        try {
            localStorage.setItem(key, JSON.stringify(settings));
        } catch {
            // Not kept: this page's alerts last as long as it does.
        }
    };

    // The sounds, recordings the host serves (window.blotterSoundUrls, set by the page): each fetched the
    // first time it plays and decoded once, the browser keeping the file. Played through Web Audio, so
    // alerts at once mix.
    const loaded = new Map();
    const load = id => {
        let buffer = loaded.get(id);
        if (!buffer) {
            buffer = fetch(window.blotterSoundUrls[id])
                .then(response => response.ok ? response.arrayBuffer() : Promise.reject(new Error(`${id}: ${response.status}`)))
                .then(bytes => audio.decodeAudioData(bytes));
            buffer.catch(() => loaded.delete(id));
            loaded.set(id, buffer);
        }
        return buffer;
    };

    // The page's icon, drawn: a blotter's rows, and a red dot on them while an alert is unseen.
    const icon = dot => "data:image/svg+xml," + encodeURIComponent(
        `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">`
        + `<rect x="2" y="4" width="28" height="24" rx="5" fill="#3b6fd9"/>`
        + `<path d="M8 12h16M8 17h16M8 22h10" stroke="#fff" stroke-width="2.6" stroke-linecap="round"/>`
        + (dot ? `<circle cx="25" cy="8" r="7" fill="#e5383b" stroke="#fff" stroke-width="2"/>` : "")
        + `</svg>`);
    const link = document.querySelector("link[rel~='icon']") ?? document.head.appendChild(Object.assign(document.createElement("link"), {rel: "icon"}));
    link.type = "image/svg+xml";
    link.href = icon(false);

    // The page in the background: a red dot on its icon until it is looked at. The title stays as it
    // is; no count: what matters is that something fired.
    const mark = () => {
        unseen = true;
        link.href = icon(true);
    };
    const looked = () => {
        if (unseen && !document.hidden && document.hasFocus()) {
            unseen = false;
            link.href = icon(false);
        }
    };
    window.addEventListener("focus", looked);
    document.addEventListener("visibilitychange", looked);

    return {
        // The browser's own id, made once, its rules, and whether it notifies: for the page to give the host.
        load(dotNet) {
            page = dotNet;
            const settings = read();
            if (!settings.browser) {
                settings.browser = crypto.randomUUID();
                write(settings);
            }
            return {
                browser: settings.browser,
                rules: settings.rules ?? [],
                notify: settings.notify === true && "Notification" in window && Notification.permission === "granted",
                sound: audio?.state === "running",
            };
        },

        saveRules(rules) {
            const settings = read();
            settings.rules = rules;
            write(settings);
        },

        // From the click itself: a browser lets a page make sound only once someone has interacted with it.
        enableSound() {
            audio ??= new AudioContext();
            audio.resume().then(() => page?.invokeMethodAsync("SoundEnabled", true));
        },

        // From the click itself too: a browser asks for leave to notify only when someone has asked.
        setNotify(on) {
            const settings = read();
            settings.notify = on;
            write(settings);
            if (on && "Notification" in window && Notification.permission === "default") {
                Notification.requestPermission().then(answer => page?.invokeMethodAsync("NotifyAllowed", answer === "granted"));
            } else {
                page?.invokeMethodAsync("NotifyAllowed", on && "Notification" in window && Notification.permission === "granted");
            }
        },

        // Escape, for the page: the splash closes.
        escape() {
            page?.invokeMethodAsync("OnEscape");
        },

        play(sound) {
            if (audio?.state !== "running" || !window.blotterSoundUrls?.[sound]) {
                return false;
            }
            load(sound).then(buffer => {
                const source = audio.createBufferSource();
                source.buffer = buffer;
                source.connect(audio.destination);
                source.start();
            }, failure => console.warn("an alert's sound could not be played", failure));
            return true;
        },

        // An alert the host fired: sounded, unless its rule is muted; and, in the background, a red dot
        // on the page's icon and, if allowed, a notification from the system.
        fire(alert, notify) {
            if (!alert.muted) {
                this.play(alert.sound);
            }
            if (document.hidden || !document.hasFocus()) {
                mark();
                if (notify && "Notification" in window && Notification.permission === "granted") {
                    // The notification is the alert itself: how many rows, and the latest.
                    const words = `${alert.ruleName}${alert.count > 1 ? ` ×${alert.count}` : ""}`;
                    const shown = new Notification(`Blotter: ${words}`, {
                        body: alert.latest,
                        tag: alert.ruleId,
                        renotify: true
                    });
                    shown.onclick = () => {
                        window.focus();
                        shown.close();
                    };
                }
            }
        },
    };
})();
