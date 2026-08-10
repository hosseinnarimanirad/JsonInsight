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

    // Ctrl+D and F5, forwarded to .NET. Registered on the document rather than on a component so a
    // shortcut works wherever focus happens to be, which is what makes it a shortcut.
    registerShortcuts: function (dotNetRef) {
        document.addEventListener('keydown', function (e) {
            if (e.ctrlKey && !e.shiftKey && !e.altKey && (e.key === 'd' || e.key === 'D')) {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnToggleTheme');
                return;
            }

            if (e.key === 'F5') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnReload');
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

    // Select a range in the pane, which is how a find result is shown. Scrolled into view first,
    // because a match selected 400 lines below the fold is a match nobody can see - and the browser
    // only scrolls to a selection on focus, not on setSelectionRange.
    select: function (element, start, length) {
        if (!element) {
            return;
        }

        element.focus();
        element.setSelectionRange(start, start + length);

        // Rough but effective: put the matched line near the middle rather than at the very bottom.
        const before = element.value.slice(0, start);
        const line = before.split('\n').length - 1;
        const lineHeight = parseFloat(getComputedStyle(element).lineHeight) || 18;
        element.scrollTop = Math.max(0, (line * lineHeight) - (element.clientHeight / 2));
    },
};
