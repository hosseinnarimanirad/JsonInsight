// The handful of things a webview can do that C# cannot reach directly.
//
// Deliberately small: every one of these exists because there is no managed equivalent, not because
// it was more convenient here. Anything that could live in a view model does.
window.jsonInsight = {

    // The clipboard. navigator.clipboard is permission-gated and absent on some Linux webviews, so
    // the textarea fallback stays - it is the only path that works on WebKitGTK without a secure
    // context, and this app is loaded from a file origin.
    copyText: async function (text) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch {
            // Fall through - a rejected permission is not worth reporting twice.
        }

        try {
            const area = document.createElement('textarea');
            area.value = text;
            area.style.position = 'fixed';
            area.style.opacity = '0';
            document.body.appendChild(area);
            area.select();
            const ok = document.execCommand('copy');
            document.body.removeChild(area);
            return ok;
        } catch {
            return false;
        }
    },

    // Which theme the OS is set to. The WPF app reads a Windows registry key for this; the webview
    // already knows, and knows it the same way on all three platforms.
    prefersDark: function () {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    // Stamps the theme on the document element. Not a Blazor attribute binding, because <html> is
    // outside the component tree - and putting the app's root token scope on a wrapper div instead
    // would leave the page background, the scrollbars and the error strip on the other theme.
    setTheme: function (name) {
        document.documentElement.setAttribute('data-theme', name);
    },

    // Ctrl+D, Ctrl+F and Ctrl+Z, forwarded to .NET. Registered on the document rather than on a
    // component so a shortcut works wherever focus happens to be, which is what makes it a shortcut.
    registerShortcuts: function (dotNetRef) {
        document.addEventListener('keydown', function (e) {
            // Swallowed rather than handled. This app has no reload command, and letting F5 through
            // asks the webview to reload the page - which here means throwing away every loaded
            // source, the open project and any queued edit, with no confirmation.
            if (e.key === 'F5') {
                e.preventDefault();
                return;
            }

            // Ctrl+Z belongs to the editor pane's text and to nothing else, so it is claimed only
            // while the caret is actually in the pane; everywhere else the webview keeps it. Undo of
            // the DOCUMENT is a button, deliberately - see JsonEditorVm.UndoText.
            if (e.ctrlKey && !e.shiftKey && !e.altKey && (e.key === 'z' || e.key === 'Z')) {
                if (e.target && e.target.classList && e.target.classList.contains('pane-text')) {
                    e.preventDefault();
                    dotNetRef.invokeMethodAsync('OnPaneUndo');
                }
                return;
            }

            if (e.ctrlKey && !e.shiftKey && !e.altKey && (e.key === 'd' || e.key === 'D')) {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnToggleTheme');
                return;
            }

            if (e.ctrlKey && !e.shiftKey && !e.altKey && (e.key === 'f' || e.key === 'F')) {
                // Only the editor pane answers this; it says so by having a find bar to open.
                const handled = dotNetRef.invokeMethodAsync('OnFind');
                if (handled) {
                    e.preventDefault();
                }
            }
        });
    },

    // Focus, for the search boxes and the find bar. Blazor can set an element reference but cannot
    // focus one without a round trip, and these are focused the moment they appear.
    focus: function (element) {
        if (element) {
            element.focus();
            if (element.select) {
                element.select();
            }
        }
    },

    // Where the caret is in the editor pane. Find needs it to know where "next" starts from, and it
    // is the one piece of state that lives entirely in the DOM - there is no managed copy to read.
    caret: function (element) {
        return element ? (element.selectionStart || 0) : 0;
    },

    // Select a range in the pane and put it on screen, without stealing focus - which is what
    // stepping through matches needs.
    //
    // The find box has to keep the caret or the second Enter goes into the JSON instead of finding
    // the next match, which is exactly the defect this replaced. The selection is still set, so the
    // pane opens on the match when it is clicked into; what shows the match meanwhile is the
    // highlight layer, not the browser's selection - an unfocused textarea does not paint one.
    reveal: function (element, start, length) {
        if (!element) {
            return;
        }

        element.setSelectionRange(start, start + length);
        window.jsonInsight.scrollTo(element, start);
    },

    // Puts the line an offset falls on near the middle of the pane rather than at the very bottom.
    // Rough - it assumes unwrapped lines - and good enough: being off by a line or two still leaves
    // the match on screen, which is the whole requirement.
    scrollTo: function (element, offset) {
        const before = element.value.slice(0, offset);
        const line = before.split('\n').length - 1;
        const lineHeight = parseFloat(getComputedStyle(element).lineHeight) || 18;

        element.scrollTop = Math.max(0, (line * lineHeight) - (element.clientHeight / 2));
        window.jsonInsight.syncHighlights(element);
    },

    // Keeps the highlight layer lined up with the text it sits behind.
    //
    // The layer is a separate element holding the same string with <mark>s in it, so the only thing
    // that can put the two out of register is scroll position. Registered once per pane; re-calling
    // it replaces the previous handler rather than stacking a second one.
    trackScroll: function (element, layer) {
        if (!element) {
            return;
        }

        // A null layer detaches: the highlight element is only rendered while there is something to
        // highlight, and writing to the one that was there before it went is writing to a node that
        // is no longer in the document.
        element._hlLayer = layer || null;

        if (!element._hlBound) {
            element._hlBound = function () {
                const target = element._hlLayer;
                if (target) {
                    target.scrollTop = element.scrollTop;
                    target.scrollLeft = element.scrollLeft;
                }
            };

            element.addEventListener('scroll', element._hlBound, { passive: true });
        }

        element._hlBound();
    },

    syncHighlights: function (element) {
        if (element && element._hlBound) {
            element._hlBound();
        }
    },
};
