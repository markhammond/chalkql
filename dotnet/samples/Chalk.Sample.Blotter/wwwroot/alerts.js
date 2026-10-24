// Alerts in the browser: the rules kept in local settings, the sounds, and drawing attention to a page
// in the background. The host decides when an alert fires; this only sounds it.
window.blotterAlerts = (() => {
    const key = "blotter.alerts";
    let audio = null;
    let page = null;
    let flashing = null;
    let unseen = false;
    const title = document.title;

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

    // The sounds, drawn with Web Audio: nothing to load, and every alert at once mixes as it plays.
    const tone = (at, type, from, to, length, peak) => {
        const oscillator = audio.createOscillator();
        const gain = audio.createGain();
        oscillator.type = type;
        oscillator.frequency.setValueAtTime(from, at);
        if (to !== from) {
            oscillator.frequency.exponentialRampToValueAtTime(to, at + length);
        }
        gain.gain.setValueAtTime(0.0001, at);
        gain.gain.exponentialRampToValueAtTime(peak, at + 0.01);
        gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
        oscillator.connect(gain).connect(audio.destination);
        oscillator.start(at);
        oscillator.stop(at + length + 0.05);
    };
    const noise = (at, length, from, to, peak) => {
        const buffer = audio.createBuffer(1, Math.ceil(audio.sampleRate * length), audio.sampleRate);
        const data = buffer.getChannelData(0);
        for (let i = 0; i < data.length; i++) {
            data[i] = Math.random() * 2 - 1;
        }
        const source = audio.createBufferSource();
        source.buffer = buffer;
        const band = audio.createBiquadFilter();
        band.type = "bandpass";
        band.Q.value = 2;
        band.frequency.setValueAtTime(from, at);
        band.frequency.exponentialRampToValueAtTime(to, at + length);
        const gain = audio.createGain();
        gain.gain.setValueAtTime(peak, at);
        gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
        source.connect(band).connect(gain).connect(audio.destination);
        source.start(at);
    };
    const sounds = {
        // A drawer's clunk, then two bright bells: cha-ching.
        Register(at) {
            noise(at, 0.08, 900, 300, 0.5);
            for (const [delay, pitch] of [[0.09, 2093], [0.22, 2637]]) {
                tone(at + delay, "sine", pitch, pitch, 0.6, 0.25);
                tone(at + delay, "sine", pitch * 2.76, pitch * 2.76, 0.25, 0.06);
            }
        },
        // An alarm rising twice.
        Klaxon(at) {
            for (const delay of [0, 0.45]) {
                tone(at + delay, "sawtooth", 420, 840, 0.38, 0.12);
                tone(at + delay, "square", 210, 420, 0.38, 0.05);
            }
        },
        // A crackle falling away, under a sharp drop.
        Zap(at) {
            noise(at, 0.35, 6000, 300, 0.6);
            tone(at, "square", 1600, 90, 0.3, 0.12);
        },
        // A ray-gun shot: a falling sweep, and its echo.
        Laser(at) {
            tone(at, "sawtooth", 2400, 180, 0.32, 0.14);
            tone(at + 0.12, "sawtooth", 2400, 180, 0.32, 0.05);
        },
    };

    // The page's icon, drawn: a blotter's rows, and a red dot on them while an alert is unseen.
    const icon = dot => "data:image/svg+xml," + encodeURIComponent(
        `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">`
        + `<rect x="2" y="4" width="28" height="24" rx="5" fill="#3b6fd9"/>`
        + `<path d="M8 12h16M8 17h16M8 22h10" stroke="#fff" stroke-width="2.6" stroke-linecap="round"/>`
        + (dot ? `<circle cx="25" cy="8" r="7" fill="#e5383b" stroke="#fff" stroke-width="2"/>` : "")
        + `</svg>`);
    const link = document.querySelector("link[rel~='icon']") ?? document.head.appendChild(Object.assign(document.createElement("link"), { rel: "icon" }));
    link.type = "image/svg+xml";
    link.href = icon(false);

    // The page in the background: a red dot on its icon, and its title flashing the rule's name, until
    // it is looked at. No count: what matters is that something fired, and what.
    const flash = rule => {
        unseen = true;
        link.href = icon(true);
        clearInterval(flashing);
        let on = false;
        flashing = setInterval(() => {
            on = !on;
            document.title = on ? `🔔 ${rule}` : title;
        }, 1000);
    };
    const looked = () => {
        if (unseen && !document.hidden && document.hasFocus()) {
            clearInterval(flashing);
            flashing = null;
            unseen = false;
            link.href = icon(false);
            document.title = title;
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

        play(sound) {
            if (audio?.state !== "running" || !sounds[sound]) {
                return false;
            }
            sounds[sound](audio.currentTime + 0.02);
            return true;
        },

        // An alert the host fired: sounded, unless its rule is muted; and, in the background, the title
        // flashes and, if allowed, the system shows a notification.
        fire(alert, notify) {
            if (!alert.muted) {
                this.play(alert.sound);
            }
            if (document.hidden || !document.hasFocus()) {
                flash(alert.ruleName);
                if (notify && "Notification" in window && Notification.permission === "granted") {
                    // The notification is the alert itself: how many rows, and the latest.
                    const words = `${alert.ruleName}${alert.count > 1 ? ` ×${alert.count}` : ""}`;
                    const shown = new Notification(`Blotter: ${words}`, { body: alert.latest, tag: alert.ruleId, renotify: true });
                    shown.onclick = () => { window.focus(); shown.close(); };
                }
            }
        },
    };
})();
