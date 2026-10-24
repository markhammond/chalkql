// The alerts panel's condition editor: SQL, highlighted by CodeMirror, completed and checked by the
// host. The host knows the tab's columns, the paths from it to other tabs (a fill's `order.`), and
// the values a column may take; and it asks ChalkQL whether the condition stands. This only draws.
import { EditorView, keymap, placeholder } from "@codemirror/view";
import { EditorState } from "@codemirror/state";
import { defaultKeymap, history, historyKeymap } from "@codemirror/commands";
import { autocompletion, closeBrackets, closeBracketsKeymap, completionKeymap, startCompletion } from "@codemirror/autocomplete";
import { linter } from "@codemirror/lint";
import { sql, StandardSQL } from "@codemirror/lang-sql";
import { defaultHighlightStyle, syntaxHighlighting } from "@codemirror/language";

const theme = EditorView.theme({
    "&": { fontSize: "12px", border: "1px solid var(--line)", borderRadius: "6px", background: "var(--panel)", color: "var(--ink)" },
    "&.cm-focused": { outline: "none", borderColor: "var(--accent, #3b6fd9)" },
    ".cm-content": { fontFamily: "ui-monospace, \"SF Mono\", Menlo, monospace", padding: "3px 0", caretColor: "var(--ink)" },
    ".cm-line": { padding: "0 6px" },
    ".cm-scroller": { maxHeight: "7.5em", overflow: "auto" },
    ".cm-placeholder": { color: "var(--muted)" },
    ".cm-tooltip": { background: "var(--panel)", color: "var(--ink)", border: "1px solid var(--line)", borderRadius: "6px" },
    ".cm-tooltip-autocomplete ul li": { color: "var(--ink)" },
    ".cm-tooltip-autocomplete ul li[aria-selected]": { background: "var(--accent, #3b6fd9)", color: "#fff" },
    ".cm-completionInfo": { background: "var(--panel)", color: "var(--ink)" },
    ".cm-diagnostic": { color: "var(--ink)" },
    ".cm-completionDetail": { color: "var(--muted)", fontStyle: "normal", marginLeft: "1em" },
});

// The host completes: what fits where the cursor is, for this tab.
const completeFrom = host => async context => {
    const text = context.state.doc.toString();
    const answer = await host.invokeMethodAsync("Complete", text, context.pos);
    if (!answer || answer.options.length === 0) {
        return null;
    }
    return {
        from: answer.from,
        options: answer.options.map(o => ({ label: o.label, type: o.type, detail: o.detail, info: o.info ?? undefined, apply: o.apply ?? undefined, boost: o.boost })),
        validFor: /^[\w']*$/,
    };
};

// The host checks: names it does not know, where they are, and then what ChalkQL makes of the whole.
const checkWith = host => linter(async view => {
    const text = view.state.doc.toString();
    const found = await host.invokeMethodAsync("Check", text);
    const length = view.state.doc.length;
    return found.map(d => ({
        from: Math.min(d.from, length),
        to: Math.min(Math.max(d.to, d.from), length),
        severity: d.severity,
        message: d.message,
    }));
}, { delay: 300 });

window.blotterEditor = {
    create(element, host, text, hint) {
        const view = new EditorView({
            parent: element,
            state: EditorState.create({
                doc: text,
                extensions: [
                    history(),
                    closeBrackets(),
                    sql({ dialect: StandardSQL, upperCaseKeywords: true }),
                    syntaxHighlighting(defaultHighlightStyle),
                    autocompletion({ override: [completeFrom(host)], activateOnTyping: true, icons: false }),
                    checkWith(host),
                    placeholder(hint),
                    EditorView.lineWrapping,
                    theme,

                    // A dot after a path, or a quote after an operator, asks for what may follow at once.
                    EditorView.inputHandler.of((view, from, to, inserted) => {
                        if (inserted === "." || inserted === "'" || inserted === "(") {
                            setTimeout(() => startCompletion(view), 0);
                        }
                        return false;
                    }),
                    EditorView.updateListener.of(update => {
                        if (update.docChanged) {
                            host.invokeMethodAsync("OnChanged", update.state.doc.toString());
                        }
                    }),
                    keymap.of([...closeBracketsKeymap, ...completionKeymap, ...historyKeymap, ...defaultKeymap]),
                ],
            }),
        });
        element.editor = view;
    },

    // The page sets the text: the tab's filters, as a condition.
    set(element, text) {
        const view = element?.editor;
        if (view && view.state.doc.toString() !== text) {
            view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text } });
        }
    },

    dispose(element) {
        element?.editor?.destroy();
    },
};
