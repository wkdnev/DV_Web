// Document Viewer - Unified OpenSeadragon viewer for TIFF/image documents
// Uses server-side tile generation for progressive deep-zoom viewing
// OpenSeadragon 6.0.2

(function () {
    var viewers = {};
    var filters = {};       // per-container filter state
    var keyboardBound = {};
    var measureState = {};  // per-container measurement state
    var annotState = {};    // per-container annotation state
    var scalebarState = {}; // per-container scalebar state

    function defaultFilters() {
        return { brightness: 0, contrast: 0, inverted: false };
    }

    function applyFilters(containerId) {
        var f = filters[containerId] || defaultFilters();
        var container = document.getElementById(containerId);
        if (!container) return;
        var canvas = container.querySelector('.openseadragon-canvas');
        if (!canvas) return;
        var parts = [];
        parts.push('brightness(' + (1 + f.brightness / 100) + ')');
        parts.push('contrast(' + (1 + f.contrast / 100) + ')');
        if (f.inverted) parts.push('invert(1)');
        canvas.style.filter = parts.join(' ');
    }

    // ==================== Scale Bar ====================
    function createScalebar(containerId) {
        var v = viewers[containerId];
        if (!v) return;
        var container = document.getElementById(containerId);
        if (!container) return;

        // Remove existing scalebar
        var existing = container.querySelector('.dv-scalebar');
        if (existing) existing.remove();

        var bar = document.createElement('div');
        bar.className = 'dv-scalebar';
        bar.style.cssText = 'position:absolute;bottom:12px;left:12px;z-index:50;background:rgba(0,0,0,0.7);' +
            'border:1px solid rgba(255,255,255,0.3);border-radius:4px;padding:4px 10px;font-size:11px;color:#eee;' +
            'pointer-events:none;font-family:monospace;display:flex;align-items:center;gap:6px;';

        var line = document.createElement('div');
        line.style.cssText = 'height:2px;background:#fff;min-width:40px;';
        var label = document.createElement('span');
        label.textContent = '';

        bar.appendChild(line);
        bar.appendChild(label);
        container.appendChild(bar);

        scalebarState[containerId] = { bar: bar, line: line, label: label, ppm: 0 };

        function updateScalebar() {
            var state = scalebarState[containerId];
            if (!state || !viewers[containerId]) return;

            var viewer = viewers[containerId];
            var tiledImage = viewer.world.getItemAt(0);
            if (!tiledImage) return;

            var zoom = viewer.viewport.getZoom(true);
            var containerWidth = viewer.viewport.getContainerSize().x;
            var bounds = tiledImage.getBounds();
            var imageWidth = tiledImage.source.width || bounds.width;

            // pixels per screen pixel at current zoom
            var pxPerScreenPx = (imageWidth * zoom) / containerWidth;

            // If user set a pixels-per-meter, use real units; otherwise show image pixels
            var ppm = state.ppm;
            var targetBarWidthPx = 100; // target ~100px wide bar on screen

            if (ppm > 0) {
                // Real-world units
                var metersPerScreenPx = 1 / (ppm * zoom / (containerWidth / bounds.width));
                var totalMeters = metersPerScreenPx * targetBarWidthPx;

                // Find nice round number
                var unit, value, unitLabel;
                if (totalMeters >= 1) {
                    value = niceNumber(totalMeters);
                    unitLabel = value + ' m';
                } else if (totalMeters >= 0.01) {
                    value = niceNumber(totalMeters * 100);
                    unitLabel = value + ' cm';
                } else {
                    value = niceNumber(totalMeters * 1000);
                    unitLabel = value + ' mm';
                }
                var actualPx = targetBarWidthPx * (value / (totalMeters > 0 ? totalMeters : 1));
                state.line.style.width = Math.max(20, Math.min(200, actualPx)) + 'px';
                state.label.textContent = unitLabel;
            } else {
                // Pixel units
                var totalImagePx = pxPerScreenPx * targetBarWidthPx;
                var nicePx = niceNumber(totalImagePx);
                var barWidth = targetBarWidthPx * (nicePx / (totalImagePx > 0 ? totalImagePx : 1));
                state.line.style.width = Math.max(20, Math.min(200, barWidth)) + 'px';
                state.label.textContent = nicePx + ' px';
            }
        }

        function niceNumber(val) {
            var exp = Math.floor(Math.log10(val));
            var base = Math.pow(10, exp);
            var fraction = val / base;
            if (fraction <= 1) return base;
            if (fraction <= 2) return 2 * base;
            if (fraction <= 5) return 5 * base;
            return 10 * base;
        }

        v.addHandler('zoom', updateScalebar);
        v.addHandler('resize', updateScalebar);
        v.addHandler('open', function () { setTimeout(updateScalebar, 200); });
        setTimeout(updateScalebar, 300);
    }

    // ==================== Measurement Tool ====================
    function initMeasure(containerId) {
        if (measureState[containerId]) return;
        measureState[containerId] = {
            active: false,
            points: [],        // current measurement [{x,y}, {x,y}]
            measurements: [],  // completed: [{p1, p2, distance, id, overlay}]
            nextId: 1,
            overlay: null
        };
    }

    function createMeasureOverlay(containerId) {
        var container = document.getElementById(containerId);
        if (!container) return null;
        var existing = container.querySelector('.dv-measure-svg');
        if (existing) existing.remove();

        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('class', 'dv-measure-svg');
        svg.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;z-index:60;pointer-events:none;';
        container.appendChild(svg);
        return svg;
    }

    function viewportToScreen(containerId, point) {
        var v = viewers[containerId];
        if (!v) return { x: 0, y: 0 };
        var px = v.viewport.viewportToViewerElementCoordinates(new OpenSeadragon.Point(point.x, point.y));
        return { x: px.x, y: px.y };
    }

    function screenToViewport(containerId, x, y) {
        var v = viewers[containerId];
        if (!v) return { x: 0, y: 0 };
        var vp = v.viewport.viewerElementToViewportCoordinates(new OpenSeadragon.Point(x, y));
        return { x: vp.x, y: vp.y };
    }

    function measureDistance(containerId, p1, p2) {
        var v = viewers[containerId];
        if (!v) return 0;
        var tiledImage = v.world.getItemAt(0);
        if (!tiledImage) return 0;
        var imgW = tiledImage.source.width || 1;
        var imgH = tiledImage.source.height || 1;
        var bounds = tiledImage.getBounds();
        // Convert viewport coords to image pixel coords
        var dx = (p2.x - p1.x) / bounds.width * imgW;
        var dy = (p2.y - p1.y) / bounds.width * imgW; // aspect ratio maintained via width
        return Math.sqrt(dx * dx + dy * dy);
    }

    function renderMeasurements(containerId) {
        var ms = measureState[containerId];
        if (!ms) return;
        var svg = document.getElementById(containerId)?.querySelector('.dv-measure-svg');
        if (!svg) {
            svg = createMeasureOverlay(containerId);
            if (!svg) return;
        }
        svg.innerHTML = '';

        // Render completed measurements
        ms.measurements.forEach(function (m) {
            var s1 = viewportToScreen(containerId, m.p1);
            var s2 = viewportToScreen(containerId, m.p2);
            addMeasureLine(svg, s1, s2, m.distance, m.id);
        });

        // Render in-progress measurement
        if (ms.points.length === 1 && ms.tempEnd) {
            var s1 = viewportToScreen(containerId, ms.points[0]);
            var s2 = viewportToScreen(containerId, ms.tempEnd);
            var dist = measureDistance(containerId, ms.points[0], ms.tempEnd);
            addMeasureLine(svg, s1, s2, dist, 'temp');
        }
    }

    function addMeasureLine(svg, s1, s2, distance, id) {
        var ns = 'http://www.w3.org/2000/svg';

        // Line
        var line = document.createElementNS(ns, 'line');
        line.setAttribute('x1', s1.x);
        line.setAttribute('y1', s1.y);
        line.setAttribute('x2', s2.x);
        line.setAttribute('y2', s2.y);
        line.setAttribute('stroke', '#ff4444');
        line.setAttribute('stroke-width', '2');
        line.setAttribute('stroke-dasharray', '6,3');
        svg.appendChild(line);

        // End caps
        [s1, s2].forEach(function (p) {
            var circ = document.createElementNS(ns, 'circle');
            circ.setAttribute('cx', p.x);
            circ.setAttribute('cy', p.y);
            circ.setAttribute('r', '4');
            circ.setAttribute('fill', '#ff4444');
            circ.setAttribute('stroke', '#fff');
            circ.setAttribute('stroke-width', '1');
            svg.appendChild(circ);
        });

        // Label
        var mx = (s1.x + s2.x) / 2;
        var my = (s1.y + s2.y) / 2;
        var ppm = scalebarState[containerId]?.ppm || 0;
        var label;
        if (ppm > 0) {
            var meters = distance / ppm;
            if (meters >= 1) label = meters.toFixed(2) + ' m';
            else if (meters >= 0.01) label = (meters * 100).toFixed(1) + ' cm';
            else label = (meters * 1000).toFixed(1) + ' mm';
        } else {
            label = Math.round(distance) + ' px';
        }

        // Background rect for label
        var rect = document.createElementNS(ns, 'rect');
        rect.setAttribute('x', mx - 30);
        rect.setAttribute('y', my - 18);
        rect.setAttribute('width', '60');
        rect.setAttribute('height', '16');
        rect.setAttribute('rx', '3');
        rect.setAttribute('fill', 'rgba(0,0,0,0.8)');
        svg.appendChild(rect);

        var text = document.createElementNS(ns, 'text');
        text.setAttribute('x', mx);
        text.setAttribute('y', my - 7);
        text.setAttribute('text-anchor', 'middle');
        text.setAttribute('fill', '#ff8888');
        text.setAttribute('font-size', '11');
        text.setAttribute('font-family', 'monospace');
        text.textContent = label;
        svg.appendChild(text);

        // Adjust rect width to fit text
        setTimeout(function () {
            var bbox = text.getBBox();
            if (bbox.width > 0) {
                rect.setAttribute('x', bbox.x - 4);
                rect.setAttribute('width', bbox.width + 8);
            }
        }, 0);
    }

    function bindMeasureHandlers(containerId) {
        var v = viewers[containerId];
        if (!v) return;

        // Re-render on viewport change
        v.addHandler('animation', function () {
            var ms = measureState[containerId];
            if (ms && (ms.measurements.length > 0 || ms.points.length > 0)) {
                renderMeasurements(containerId);
            }
        });
    }

    // ==================== Annotations ====================
    function initAnnotations(containerId) {
        if (annotState[containerId]) return;
        annotState[containerId] = {
            active: false,
            annotations: [], // [{id, vpPoint, text, color}]
            nextId: 1,
            pendingPoint: null
        };
    }

    function renderAnnotations(containerId) {
        var as = annotState[containerId];
        if (!as) return;
        var container = document.getElementById(containerId);
        if (!container) return;

        // Remove existing annotation overlays
        container.querySelectorAll('.dv-annot-marker').forEach(function (el) { el.remove(); });

        as.annotations.forEach(function (a) {
            var sp = viewportToScreen(containerId, a.vpPoint);
            var marker = document.createElement('div');
            marker.className = 'dv-annot-marker';
            marker.dataset.annotId = a.id;
            marker.style.cssText = 'position:absolute;z-index:70;pointer-events:auto;cursor:pointer;' +
                'transform:translate(-50%,-100%);left:' + sp.x + 'px;top:' + sp.y + 'px;';

            var pin = document.createElement('div');
            pin.style.cssText = 'width:24px;height:24px;background:' + (a.color || '#4a6cf7') +
                ';border:2px solid #fff;border-radius:50% 50% 50% 0;transform:rotate(-45deg);' +
                'box-shadow:0 2px 6px rgba(0,0,0,0.5);display:flex;align-items:center;justify-content:center;';
            var num = document.createElement('span');
            num.style.cssText = 'transform:rotate(45deg);color:#fff;font-size:10px;font-weight:bold;';
            num.textContent = a.id;
            pin.appendChild(num);
            marker.appendChild(pin);

            if (a.text) {
                var tooltip = document.createElement('div');
                tooltip.style.cssText = 'position:absolute;bottom:28px;left:50%;transform:translateX(-50%);' +
                    'background:rgba(20,20,40,0.95);border:1px solid #555;border-radius:4px;padding:4px 8px;' +
                    'font-size:11px;color:#eee;white-space:nowrap;max-width:200px;overflow:hidden;text-overflow:ellipsis;' +
                    'pointer-events:none;box-shadow:0 2px 8px rgba(0,0,0,0.5);';
                tooltip.textContent = a.text;
                marker.appendChild(tooltip);
                tooltip.style.display = 'none';
                marker.addEventListener('mouseenter', function () { tooltip.style.display = ''; });
                marker.addEventListener('mouseleave', function () { tooltip.style.display = 'none'; });
            }

            // Right-click to delete
            marker.addEventListener('contextmenu', function (e) {
                e.preventDefault();
                var aid = parseInt(marker.dataset.annotId);
                as.annotations = as.annotations.filter(function (ann) { return ann.id !== aid; });
                renderAnnotations(containerId);
            });

            container.appendChild(marker);
        });
    }

    function bindAnnotationHandlers(containerId) {
        var v = viewers[containerId];
        if (!v) return;
        v.addHandler('animation', function () {
            var as = annotState[containerId];
            if (as && as.annotations.length > 0) {
                renderAnnotations(containerId);
            }
        });
    }

    // ==================== Viewer Creation ====================

    async function createTiledViewer(containerId, pageId, options) {
        var opts = options || {};
        var prefixUrl = 'https://cdn.jsdelivr.net/npm/openseadragon@6.0.2/build/openseadragon/images/';

        var response = await fetch('/api/DocumentBlob/page/' + pageId + '/image-info', {
            credentials: 'include'
        });

        if (!response.ok) {
            console.error('Failed to fetch image info for page ' + pageId);
            return null;
        }

        var info = await response.json();
        var tileSize = info.tileSize || 256;
        var maxLevel = info.maxLevel || 0;

        var tileSource = {
            width: info.width,
            height: info.height,
            tileSize: tileSize,
            maxLevel: maxLevel,
            minLevel: Math.max(0, maxLevel - 8),
            getTileUrl: function (level, x, y) {
                return '/api/DocumentBlob/page/' + pageId + '/tile/' + level + '/' + x + '/' + y;
            }
        };

        var viewer = OpenSeadragon({
            id: containerId,
            prefixUrl: prefixUrl,
            tileSources: tileSource,
            animationTime: 0.4,
            blendTime: 0.2,
            constrainDuringPan: true,
            maxZoomPixelRatio: 4,
            minZoomLevel: 0.3,
            visibilityRatio: 0.8,
            zoomPerScroll: 1.3,
            zoomPerClick: 2,
            showNavigationControl: false,
            showZoomControl: false,
            showHomeControl: false,
            showFullPageControl: false,
            showRotationControl: false,
            showNavigator: opts.showNavigator !== undefined ? opts.showNavigator : true,
            navigatorPosition: 'TOP_RIGHT',
            navigatorSizeRatio: 0.15,
            navigatorAutoFade: true,
            gestureSettingsMouse: {
                clickToZoom: false,
                dblClickToZoom: true,
                dblClickDragToZoom: true
            },
            gestureSettingsTouch: {
                pinchToZoom: true,
                flickEnabled: true
            },
            loadTilesWithAjax: true,
            ajaxHeaders: {},
            ajaxWithCredentials: true,
            springStiffness: 12,
            immediateRender: false,
            crossOriginPolicy: false
        });

        return viewer;
    }

    function createSimpleViewer(containerId, imageUrl, options) {
        var opts = options || {};
        var prefixUrl = 'https://cdn.jsdelivr.net/npm/openseadragon@6.0.2/build/openseadragon/images/';

        return OpenSeadragon({
            id: containerId,
            prefixUrl: prefixUrl,
            tileSources: {
                type: 'image',
                url: imageUrl,
                buildPyramid: true
            },
            animationTime: 0.4,
            blendTime: 0.2,
            constrainDuringPan: true,
            maxZoomPixelRatio: 4,
            minZoomLevel: 0.3,
            visibilityRatio: 0.8,
            zoomPerScroll: 1.3,
            zoomPerClick: 2,
            showNavigationControl: false,
            showZoomControl: false,
            showHomeControl: false,
            showFullPageControl: false,
            showRotationControl: false,
            showNavigator: opts.showNavigator !== undefined ? opts.showNavigator : true,
            navigatorPosition: 'TOP_RIGHT',
            navigatorSizeRatio: 0.15,
            navigatorAutoFade: true,
            gestureSettingsMouse: {
                clickToZoom: false,
                dblClickToZoom: true,
                dblClickDragToZoom: true
            },
            gestureSettingsTouch: {
                pinchToZoom: true,
                flickEnabled: true
            },
            springStiffness: 12,
            immediateRender: false
        });
    }

    // ==================== Keyboard Shortcuts ====================

    function bindKeyboard(containerId) {
        if (keyboardBound[containerId]) return;
        keyboardBound[containerId] = true;

        document.addEventListener('keydown', function (e) {
            var tag = (e.target.tagName || '').toLowerCase();
            if (tag === 'input' || tag === 'textarea' || tag === 'select' || e.target.isContentEditable) return;
            if (!viewers[containerId]) return;

            var handled = false;
            switch (e.key) {
                case '+': case '=':
                    DocViewer.zoomIn(containerId); handled = true; break;
                case '-': case '_':
                    DocViewer.zoomOut(containerId); handled = true; break;
                case '0':
                    DocViewer.home(containerId); handled = true; break;
                case 'f': case 'F':
                    if (!e.ctrlKey && !e.metaKey) { DocViewer.fullPage(containerId); handled = true; }
                    break;
                case 'r':
                    if (!e.ctrlKey && !e.metaKey) { DocViewer.rotateRight(containerId); handled = true; }
                    break;
                case 'R':
                    if (!e.ctrlKey && !e.metaKey) { DocViewer.rotateLeft(containerId); handled = true; }
                    break;
                case 'w': case 'W':
                    if (!e.ctrlKey && !e.metaKey) { DocViewer.fitWidth(containerId); handled = true; }
                    break;
                case 'i': case 'I':
                    if (!e.ctrlKey && !e.metaKey) { DocViewer.toggleInvert(containerId); handled = true; }
                    break;
                case 'Escape':
                    // Cancel active measure/annotate modes
                    if (measureState[containerId]?.active) {
                        DocViewer.toggleMeasure(containerId);
                        handled = true;
                    } else if (annotState[containerId]?.active) {
                        DocViewer.toggleAnnotate(containerId);
                        handled = true;
                    }
                    break;
            }
            if (handled) e.preventDefault();
        });
    }

    // ==================== Public API ====================

    window.DocViewer = {

        initTiled: async function (containerId, pageId, options) {
            try {
                if (typeof OpenSeadragon === 'undefined') {
                    console.error('OpenSeadragon library not loaded');
                    return false;
                }

                var container = document.getElementById(containerId);
                if (!container) {
                    console.error('Container not found: ' + containerId);
                    return false;
                }

                if (viewers[containerId]) {
                    viewers[containerId].destroy();
                    viewers[containerId] = null;
                }

                filters[containerId] = defaultFilters();
                initMeasure(containerId);
                initAnnotations(containerId);

                var viewer = await createTiledViewer(containerId, pageId, options);
                if (!viewer) return false;

                viewers[containerId] = viewer;

                viewer.addHandler('open', function () {
                    viewer.viewport.goHome(true);
                    setTimeout(function () { applyFilters(containerId); }, 100);
                });

                viewer.addHandler('open-failed', function (event) {
                    console.error('Failed to open tiled image:', event);
                });

                bindKeyboard(containerId);
                bindMeasureHandlers(containerId);
                bindAnnotationHandlers(containerId);
                createScalebar(containerId);
                createMeasureOverlay(containerId);

                return true;
            } catch (error) {
                console.error('Error initializing tiled viewer:', error);
                return false;
            }
        },

        initSimple: function (containerId, imageUrl, options) {
            try {
                if (typeof OpenSeadragon === 'undefined') {
                    console.error('OpenSeadragon library not loaded');
                    return false;
                }

                var container = document.getElementById(containerId);
                if (!container) {
                    console.error('Container not found: ' + containerId);
                    return false;
                }

                if (viewers[containerId]) {
                    viewers[containerId].destroy();
                    viewers[containerId] = null;
                }

                filters[containerId] = defaultFilters();
                initMeasure(containerId);
                initAnnotations(containerId);

                var viewer = createSimpleViewer(containerId, imageUrl, options);
                viewers[containerId] = viewer;

                viewer.addHandler('open', function () {
                    viewer.viewport.goHome(true);
                    setTimeout(function () { applyFilters(containerId); }, 100);
                });

                bindKeyboard(containerId);
                bindMeasureHandlers(containerId);
                bindAnnotationHandlers(containerId);
                createScalebar(containerId);
                createMeasureOverlay(containerId);

                return true;
            } catch (error) {
                console.error('Error initializing simple viewer:', error);
                return false;
            }
        },

        switchToTiledPage: async function (containerId, pageId) {
            if (!viewers[containerId]) {
                return await this.initTiled(containerId, pageId);
            }

            try {
                var response = await fetch('/api/DocumentBlob/page/' + pageId + '/image-info', {
                    credentials: 'include'
                });

                if (!response.ok) return false;

                var info = await response.json();

                viewers[containerId].open({
                    width: info.width,
                    height: info.height,
                    tileSize: info.tileSize || 256,
                    maxLevel: info.maxLevel || 0,
                    minLevel: Math.max(0, (info.maxLevel || 0) - 8),
                    getTileUrl: function (level, x, y) {
                        return '/api/DocumentBlob/page/' + pageId + '/tile/' + level + '/' + x + '/' + y;
                    }
                });

                return true;
            } catch (error) {
                console.error('Error switching tiled page:', error);
                return false;
            }
        },

        switchToSimpleImage: function (containerId, imageUrl) {
            if (!viewers[containerId]) {
                return this.initSimple(containerId, imageUrl);
            }

            try {
                viewers[containerId].open({
                    type: 'image',
                    url: imageUrl,
                    buildPyramid: true
                });
                return true;
            } catch (error) {
                console.error('Error switching image:', error);
                return false;
            }
        },

        // ==================== Core Controls ====================

        zoomIn: function (containerId) {
            var v = viewers[containerId];
            if (v) { v.viewport.zoomBy(1.5); v.viewport.applyConstraints(); }
        },

        zoomOut: function (containerId) {
            var v = viewers[containerId];
            if (v) { v.viewport.zoomBy(0.67); v.viewport.applyConstraints(); }
        },

        home: function (containerId) {
            var v = viewers[containerId];
            if (v) v.viewport.goHome();
        },

        fullPage: function (containerId) {
            var container = document.getElementById(containerId);
            if (!container) return;
            var parent = container.closest('.dv-viewer-container') || container;
            if (document.fullscreenElement) {
                document.exitFullscreen();
            } else {
                if (parent.requestFullscreen) parent.requestFullscreen();
                else if (parent.webkitRequestFullscreen) parent.webkitRequestFullscreen();
                else if (parent.msRequestFullscreen) parent.msRequestFullscreen();
            }
        },

        rotateLeft: function (containerId) {
            var v = viewers[containerId];
            if (v) v.viewport.setRotation(v.viewport.getRotation() - 90);
        },

        rotateRight: function (containerId) {
            var v = viewers[containerId];
            if (v) v.viewport.setRotation(v.viewport.getRotation() + 90);
        },

        fitWidth: function (containerId) {
            var v = viewers[containerId];
            if (!v) return;
            var tiledImage = v.world.getItemAt(0);
            if (!tiledImage) return;
            var bounds = tiledImage.getBounds();
            v.viewport.fitBounds(new OpenSeadragon.Rect(
                bounds.x, bounds.y, bounds.width,
                bounds.width * (v.viewport.getContainerSize().y / v.viewport.getContainerSize().x)
            ), false);
        },

        // ==================== Image Filters ====================

        setBrightness: function (containerId, value) {
            if (!filters[containerId]) filters[containerId] = defaultFilters();
            filters[containerId].brightness = Math.max(-100, Math.min(100, value));
            applyFilters(containerId);
        },

        setContrast: function (containerId, value) {
            if (!filters[containerId]) filters[containerId] = defaultFilters();
            filters[containerId].contrast = Math.max(-100, Math.min(100, value));
            applyFilters(containerId);
        },

        toggleInvert: function (containerId) {
            if (!filters[containerId]) filters[containerId] = defaultFilters();
            filters[containerId].inverted = !filters[containerId].inverted;
            applyFilters(containerId);
            return filters[containerId].inverted;
        },

        resetFilters: function (containerId) {
            filters[containerId] = defaultFilters();
            applyFilters(containerId);
        },

        getFilters: function (containerId) {
            return filters[containerId] || defaultFilters();
        },

        // ==================== Measurement Tool ====================

        toggleMeasure: function (containerId) {
            var ms = measureState[containerId];
            if (!ms) { initMeasure(containerId); ms = measureState[containerId]; }
            var v = viewers[containerId];
            if (!v) return false;

            ms.active = !ms.active;
            ms.points = [];
            ms.tempEnd = null;

            // Disable annotation mode if measure is activated
            if (ms.active && annotState[containerId]) {
                annotState[containerId].active = false;
            }

            var container = document.getElementById(containerId);
            if (container) {
                container.style.cursor = ms.active ? 'crosshair' : '';
            }

            if (ms.active) {
                // Set up click handler for measuring
                ms._clickHandler = function (event) {
                    if (!ms.active) return;
                    var vp = v.viewport.pointFromPixel(event.position);
                    ms.points.push({ x: vp.x, y: vp.y });

                    if (ms.points.length === 2) {
                        var dist = measureDistance(containerId, ms.points[0], ms.points[1]);
                        ms.measurements.push({
                            id: ms.nextId++,
                            p1: ms.points[0],
                            p2: ms.points[1],
                            distance: dist
                        });
                        ms.points = [];
                        ms.tempEnd = null;
                        renderMeasurements(containerId);
                    }
                };
                ms._moveHandler = function (event) {
                    if (!ms.active || ms.points.length !== 1) return;
                    var vp = v.viewport.pointFromPixel(event.position);
                    ms.tempEnd = { x: vp.x, y: vp.y };
                    renderMeasurements(containerId);
                };
                v.addHandler('canvas-click', ms._clickHandler);
                v.addHandler('canvas-drag', ms._moveHandler);

                // Use a mouse tracker for move events
                ms._tracker = new OpenSeadragon.MouseTracker({
                    element: v.canvas,
                    moveHandler: function (event) {
                        if (!ms.active || ms.points.length !== 1) return;
                        var pos = event.position;
                        var vp = v.viewport.pointFromPixel(pos);
                        ms.tempEnd = { x: vp.x, y: vp.y };
                        renderMeasurements(containerId);
                    }
                });
            } else {
                // Clean up handlers
                if (ms._clickHandler) v.removeHandler('canvas-click', ms._clickHandler);
                if (ms._tracker) { ms._tracker.destroy(); ms._tracker = null; }
                ms._clickHandler = null;
                ms._moveHandler = null;
            }

            return ms.active;
        },

        clearMeasurements: function (containerId) {
            var ms = measureState[containerId];
            if (!ms) return;
            ms.measurements = [];
            ms.points = [];
            ms.tempEnd = null;
            renderMeasurements(containerId);
        },

        getMeasurements: function (containerId) {
            var ms = measureState[containerId];
            if (!ms) return [];
            return ms.measurements.map(function (m) {
                var ppm = scalebarState[containerId]?.ppm || 0;
                var label;
                if (ppm > 0) {
                    var meters = m.distance / ppm;
                    if (meters >= 1) label = meters.toFixed(2) + ' m';
                    else if (meters >= 0.01) label = (meters * 100).toFixed(1) + ' cm';
                    else label = (meters * 1000).toFixed(1) + ' mm';
                } else {
                    label = Math.round(m.distance) + ' px';
                }
                return { id: m.id, distance: label };
            });
        },

        undoMeasurement: function (containerId) {
            var ms = measureState[containerId];
            if (!ms || ms.measurements.length === 0) return;
            ms.measurements.pop();
            renderMeasurements(containerId);
        },

        isMeasureActive: function (containerId) {
            return measureState[containerId]?.active || false;
        },

        // ==================== Annotations ====================

        toggleAnnotate: function (containerId) {
            var as = annotState[containerId];
            if (!as) { initAnnotations(containerId); as = annotState[containerId]; }
            var v = viewers[containerId];
            if (!v) return false;

            as.active = !as.active;

            // Disable measure mode if annotate is activated
            if (as.active && measureState[containerId]?.active) {
                DocViewer.toggleMeasure(containerId);
            }

            var container = document.getElementById(containerId);
            if (container) {
                container.style.cursor = as.active ? 'cell' : '';
            }

            if (as.active) {
                as._clickHandler = function (event) {
                    if (!as.active) return;
                    var vp = v.viewport.pointFromPixel(event.position);
                    as.pendingPoint = { x: vp.x, y: vp.y };

                    // Prompt for annotation text
                    var text = prompt('Enter annotation text (or leave empty for pin only):');
                    if (text === null) { as.pendingPoint = null; return; } // Cancelled

                    var colors = ['#4a6cf7', '#f74a4a', '#4af74a', '#f7c44a', '#c44af7', '#4af7f7'];
                    var colorIdx = as.annotations.length % colors.length;

                    as.annotations.push({
                        id: as.nextId++,
                        vpPoint: as.pendingPoint,
                        text: text || '',
                        color: colors[colorIdx]
                    });
                    as.pendingPoint = null;
                    renderAnnotations(containerId);
                };
                v.addHandler('canvas-click', as._clickHandler);
            } else {
                if (as._clickHandler) v.removeHandler('canvas-click', as._clickHandler);
                as._clickHandler = null;
                as.pendingPoint = null;
            }

            return as.active;
        },

        clearAnnotations: function (containerId) {
            var as = annotState[containerId];
            if (!as) return;
            as.annotations = [];
            renderAnnotations(containerId);
        },

        getAnnotations: function (containerId) {
            var as = annotState[containerId];
            if (!as) return [];
            return as.annotations.map(function (a) {
                return { id: a.id, text: a.text };
            });
        },

        undoAnnotation: function (containerId) {
            var as = annotState[containerId];
            if (!as || as.annotations.length === 0) return;
            as.annotations.pop();
            renderAnnotations(containerId);
        },

        isAnnotateActive: function (containerId) {
            return annotState[containerId]?.active || false;
        },

        // ==================== Scale Bar ====================

        setPixelsPerMeter: function (containerId, ppm) {
            if (scalebarState[containerId]) {
                scalebarState[containerId].ppm = ppm || 0;
            }
            // Re-trigger scalebar update
            createScalebar(containerId);
        },

        toggleScalebar: function (containerId) {
            var state = scalebarState[containerId];
            if (!state) return false;
            var visible = state.bar.style.display !== 'none';
            state.bar.style.display = visible ? 'none' : '';
            return !visible;
        },

        // ==================== Screenshot / Export ====================

        captureViewport: function (containerId) {
            var v = viewers[containerId];
            if (!v) return;
            var osdCanvas = v.drawer.canvas;
            if (!osdCanvas) return;

            var exportCanvas = document.createElement('canvas');
            exportCanvas.width = osdCanvas.width;
            exportCanvas.height = osdCanvas.height;
            var ctx = exportCanvas.getContext('2d');

            var f = filters[containerId] || defaultFilters();
            var filterParts = [];
            filterParts.push('brightness(' + (1 + f.brightness / 100) + ')');
            filterParts.push('contrast(' + (1 + f.contrast / 100) + ')');
            if (f.inverted) filterParts.push('invert(1)');
            ctx.filter = filterParts.join(' ');
            ctx.drawImage(osdCanvas, 0, 0);

            try {
                exportCanvas.toBlob(function (blob) {
                    if (!blob) return;
                    var url = URL.createObjectURL(blob);
                    var a = document.createElement('a');
                    a.href = url;
                    a.download = 'document-viewport-' + new Date().toISOString().slice(0, 19).replace(/[T:]/g, '-') + '.png';
                    document.body.appendChild(a);
                    a.click();
                    document.body.removeChild(a);
                    setTimeout(function () { URL.revokeObjectURL(url); }, 5000);
                }, 'image/png');
            } catch (e) {
                console.error('Screenshot export failed (CORS restriction):', e);
            }
        },

        // ==================== Bookmark URL ====================

        getViewState: function (containerId) {
            var v = viewers[containerId];
            if (!v) return null;
            var center = v.viewport.getCenter();
            return {
                x: Math.round(center.x * 10000) / 10000,
                y: Math.round(center.y * 10000) / 10000,
                z: Math.round(v.viewport.getZoom() * 1000) / 1000,
                r: v.viewport.getRotation()
            };
        },

        restoreViewState: function (containerId, state) {
            var v = viewers[containerId];
            if (!v || !state) return;
            if (state.z != null) v.viewport.zoomTo(state.z, null, true);
            if (state.x != null && state.y != null) {
                v.viewport.panTo(new OpenSeadragon.Point(state.x, state.y), true);
            }
            if (state.r != null) v.viewport.setRotation(state.r);
        },

        updateUrlHash: function (containerId) {
            var state = this.getViewState(containerId);
            if (!state) return;
            var hash = 'x=' + state.x + '&y=' + state.y + '&z=' + state.z + '&r=' + state.r;
            history.replaceState(null, '', window.location.pathname + window.location.search + '#' + hash);
        },

        getUrlHashState: function () {
            var hash = window.location.hash.replace('#', '');
            if (!hash) return null;
            var params = {};
            hash.split('&').forEach(function (pair) {
                var kv = pair.split('=');
                if (kv.length === 2) params[kv[0]] = parseFloat(kv[1]);
            });
            if (isNaN(params.x) || isNaN(params.y) || isNaN(params.z)) return null;
            return params;
        },

        getZoomLevel: function (containerId) {
            var v = viewers[containerId];
            return v ? v.viewport.getZoom() : 1;
        },

        setZoomLevel: function (containerId, zoom) {
            var v = viewers[containerId];
            if (v) { v.viewport.zoomTo(zoom); v.viewport.applyConstraints(); }
        },

        getRotation: function (containerId) {
            var v = viewers[containerId];
            return v ? v.viewport.getRotation() : 0;
        },

        destroy: function (containerId) {
            if (viewers[containerId]) {
                viewers[containerId].destroy();
                delete viewers[containerId];
            }
            delete filters[containerId];
            delete measureState[containerId];
            delete annotState[containerId];
            // Remove overlays
            var container = document.getElementById(containerId);
            if (container) {
                container.querySelectorAll('.dv-scalebar,.dv-measure-svg,.dv-annot-marker').forEach(function (el) { el.remove(); });
            }
            delete scalebarState[containerId];
        }
    };

    // ==================== Legacy API compatibility ====================

    window.initializeOpenSeadragon = function (imageUrl) { DocViewer.initSimple('openseadragon-viewer', imageUrl); };
    window.updateOpenSeadragonImage = function (imageUrl) { DocViewer.switchToSimpleImage('openseadragon-viewer', imageUrl); };
    window.zoomInOpenSeadragon = function () { DocViewer.zoomIn('openseadragon-viewer'); };
    window.zoomOutOpenSeadragon = function () { DocViewer.zoomOut('openseadragon-viewer'); };
    window.homeOpenSeadragon = function () { DocViewer.home('openseadragon-viewer'); };
    window.fullPageOpenSeadragon = function () { DocViewer.fullPage('openseadragon-viewer'); };
    window.rotateLeftOpenSeadragon = function () { DocViewer.rotateLeft('openseadragon-viewer'); };
    window.rotateRightOpenSeadragon = function () { DocViewer.rotateRight('openseadragon-viewer'); };
})();
