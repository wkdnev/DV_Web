// PDF.js Viewer for DocViewer
// Uses PDF.js to render PDFs on canvas elements instead of browser native viewer
// Loaded as a regular script — PdfJsViewer is immediately available on window.

(function () {
    var pdfjsLib = null;
    var viewers = {};

    // Lazy-load PDF.js on first use via dynamic import
    async function ensurePdfJs() {
        if (pdfjsLib) return pdfjsLib;
        pdfjsLib = await import('https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.2.67/pdf.min.mjs');
        pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.2.67/pdf.worker.min.mjs';
        return pdfjsLib;
    }

    function createPageDiv(viewport, pageNum) {
        var pageDiv = document.createElement('div');
        pageDiv.className = 'pdfjs-page';
        pageDiv.style.position = 'relative';
        pageDiv.style.margin = '8px auto';
        pageDiv.style.width = viewport.width + 'px';
        pageDiv.style.height = viewport.height + 'px';
        pageDiv.style.boxShadow = '0 2px 8px rgba(0,0,0,0.3)';
        pageDiv.setAttribute('data-page-number', pageNum);
        return pageDiv;
    }

    function createCanvas(viewport) {
        var canvas = document.createElement('canvas');
        canvas.width = viewport.width;
        canvas.height = viewport.height;
        canvas.style.display = 'block';
        return canvas;
    }

    window.PdfJsViewer = {

        render: async function (containerId, pdfUrl, scale) {
            var container = document.getElementById(containerId);
            if (!container) return 0;

            var lib = await ensurePdfJs();

            // Destroy previous instance
            if (viewers[containerId]) {
                if (viewers[containerId].pdf) {
                    viewers[containerId].pdf.destroy();
                }
                viewers[containerId] = null;
            }

            container.innerHTML = '';

            try {
                var pdf = await lib.getDocument(pdfUrl).promise;
                var totalPages = pdf.numPages;
                viewers[containerId] = { pdf: pdf, scale: scale, pages: [] };

                for (var pageNum = 1; pageNum <= totalPages; pageNum++) {
                    var page = await pdf.getPage(pageNum);
                    var viewport = page.getViewport({ scale: scale });

                    var pageDiv = createPageDiv(viewport, pageNum);
                    var canvas = createCanvas(viewport);
                    pageDiv.appendChild(canvas);
                    container.appendChild(pageDiv);

                    await page.render({
                        canvasContext: canvas.getContext('2d'),
                        viewport: viewport
                    }).promise;

                    viewers[containerId].pages.push({ page: page, canvas: canvas, pageDiv: pageDiv });
                }

                return totalPages;
            } catch (error) {
                container.innerHTML = '<div style="color:#ff6b6b;padding:20px;text-align:center;">' +
                    '<p style="margin-top:10px;">Failed to load PDF: ' + error.message + '</p></div>';
                return 0;
            }
        },

        setScale: async function (containerId, newScale) {
            var viewer = viewers[containerId];
            if (!viewer || !viewer.pdf) return;

            viewer.scale = newScale;
            var container = document.getElementById(containerId);
            if (!container) return;
            container.innerHTML = '';
            viewer.pages = [];

            var pdf = viewer.pdf;
            for (var pageNum = 1; pageNum <= pdf.numPages; pageNum++) {
                var page = await pdf.getPage(pageNum);
                var viewport = page.getViewport({ scale: newScale });

                var pageDiv = createPageDiv(viewport, pageNum);
                var canvas = createCanvas(viewport);
                pageDiv.appendChild(canvas);
                container.appendChild(pageDiv);

                await page.render({
                    canvasContext: canvas.getContext('2d'),
                    viewport: viewport
                }).promise;

                viewer.pages.push({ page: page, canvas: canvas, pageDiv: pageDiv });
            }
        },

        scrollToPage: function (containerId, pageNumber) {
            var container = document.getElementById(containerId);
            if (!container) return;
            var pageDiv = container.querySelector('[data-page-number="' + pageNumber + '"]');
            if (pageDiv) {
                pageDiv.scrollIntoView({ behavior: 'smooth', block: 'start' });
            }
        },

        getPageCount: function (containerId) {
            var viewer = viewers[containerId];
            if (!viewer || !viewer.pdf) return 0;
            return viewer.pdf.numPages;
        },

        getCurrentPage: function (containerId) {
            var container = document.getElementById(containerId);
            if (!container) return 1;

            var pages = container.querySelectorAll('.pdfjs-page');
            var containerRect = container.getBoundingClientRect();
            var containerMid = containerRect.top + containerRect.height / 3;

            for (var i = 0; i < pages.length; i++) {
                var rect = pages[i].getBoundingClientRect();
                if (rect.top <= containerMid && rect.bottom > containerMid) {
                    return parseInt(pages[i].getAttribute('data-page-number')) || 1;
                }
            }
            return 1;
        },

        destroy: function (containerId) {
            if (viewers[containerId]) {
                if (viewers[containerId].pdf) {
                    viewers[containerId].pdf.destroy();
                }
                viewers[containerId] = null;
            }
        }
    };
})();
