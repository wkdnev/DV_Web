// ============================================================================
// document-search.js - Search page interop helpers
// ============================================================================
window.DocSearch = {
    // === Recent Searches (localStorage) ===
    getRecentSearches: function (key) {
        try { return JSON.parse(localStorage.getItem(key) || '[]'); }
        catch { return []; }
    },
    addRecentSearch: function (key, term, maxItems) {
        if (!term || !term.trim()) return;
        try {
            var items = JSON.parse(localStorage.getItem(key) || '[]');
            items = items.filter(function (s) { return s.toLowerCase() !== term.toLowerCase(); });
            items.unshift(term.trim());
            if (items.length > maxItems) items = items.slice(0, maxItems);
            localStorage.setItem(key, JSON.stringify(items));
        } catch (e) { console.warn('DocSearch: localStorage error', e); }
    },
    clearRecentSearches: function (key) {
        localStorage.removeItem(key);
    },

    // === CSV Export ===
    exportCsv: function (csvContent, filename) {
        var BOM = '\uFEFF';
        var blob = new Blob([BOM + csvContent], { type: 'text/csv;charset=utf-8;' });
        var link = document.createElement('a');
        link.href = URL.createObjectURL(blob);
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(link.href);
    },

    // === Resizable Details Panel ===
    initResize: function (handleSelector, panelSelector, minHeight, maxHeight) {
        var handle = document.querySelector(handleSelector);
        var panel = document.querySelector(panelSelector);
        if (!handle || !panel) return;
        handle.addEventListener('mousedown', function (e) {
            e.preventDefault();
            var startY = e.clientY;
            var startHeight = panel.offsetHeight;
            function onMove(ev) {
                var delta = startY - ev.clientY;
                var h = Math.min(Math.max(startHeight + delta, minHeight), maxHeight);
                panel.style.maxHeight = h + 'px';
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.userSelect = '';
                document.body.style.cursor = '';
            }
            document.body.style.userSelect = 'none';
            document.body.style.cursor = 'ns-resize';
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
    },

    // === Scroll table row into view ===
    scrollRowIntoView: function (index) {
        var row = document.querySelector('.ds-table tbody tr:nth-child(' + (index + 1) + ')');
        if (row) row.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }
};
