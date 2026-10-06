// Ore scrollbars.
// Browsers can't put a texture on native scrollbars, so app.css hides them
// (html.ore-scrollbars) and this draws a track + thumb on top of anything that scrolls.
(function () {
    // touch screens keep their normal scrollbars
    if (!window.matchMedia('(hover: hover) and (pointer: fine)').matches) return;

    var SIZE = 18;      // track thickness in px
    var MIN_THUMB = 28; // shortest thumb we'll draw
    var bars = [];
    var seen = new Set();
    var queued = false;

    // Lives on <html>, not <body>, so Blazor's enhanced navigation doesn't wipe it.
    var host = document.createElement('div');
    host.className = 'ore-sb-host';
    document.documentElement.appendChild(host);
    document.documentElement.classList.add('ore-scrollbars');

    function scroller(b) {
        return b.doc ? document.scrollingElement : b.el;
    }

    function makeBar(b, axis) {
        var vertical = axis === 'y';
        var track = document.createElement('div');
        var thumb = document.createElement('div');
        track.className = 'ore-sb ore-sb-' + axis;
        thumb.className = 'ore-sb-thumb';
        track.appendChild(thumb);
        host.appendChild(track);

        var bar = { vertical: vertical, track: track, thumb: thumb, len: 0, thumbLen: 0, thumbPos: 0, range: 0 };

        thumb.addEventListener('pointerdown', function (e) {
            var s = scroller(b);
            var startPointer = vertical ? e.clientY : e.clientX;
            var startScroll = vertical ? s.scrollTop : s.scrollLeft;
            var perPx = bar.range / Math.max(1, bar.len - bar.thumbLen);

            thumb.setPointerCapture(e.pointerId);
            track.classList.add('dragging');

            function move(ev) {
                var d = ((vertical ? ev.clientY : ev.clientX) - startPointer) * perPx;
                if (vertical) s.scrollTop = startScroll + d;
                else s.scrollLeft = startScroll + d;
            }
            function up() {
                thumb.removeEventListener('pointermove', move);
                thumb.removeEventListener('pointerup', up);
                thumb.removeEventListener('pointercancel', up);
                track.classList.remove('dragging');
            }
            thumb.addEventListener('pointermove', move);
            thumb.addEventListener('pointerup', up);
            thumb.addEventListener('pointercancel', up);
            e.preventDefault();
        });

        // clicking the empty track jumps a page toward the click
        track.addEventListener('pointerdown', function (e) {
            if (e.target !== track) return;
            var s = scroller(b);
            var r = track.getBoundingClientRect();
            var at = vertical ? e.clientY - r.top : e.clientX - r.left;
            var dir = at < bar.thumbPos ? -1 : 1;
            if (vertical) s.scrollBy(0, dir * s.clientHeight * 0.9);
            else s.scrollBy(dir * s.clientWidth * 0.9, 0);
        });

        // wheel over the bar should still scroll
        track.addEventListener('wheel', function (e) {
            scroller(b).scrollBy(e.deltaX, e.deltaY);
            e.preventDefault();
        }, { passive: false });

        return bar;
    }

    function attach(el, isDoc) {
        var b = { el: el, doc: isDoc };
        b.y = makeBar(b, 'y');
        b.x = makeBar(b, 'x');
        bars.push(b);
    }

    function drop(i) {
        var b = bars[i];
        host.removeChild(b.y.track);
        host.removeChild(b.x.track);
        bars.splice(i, 1);
    }

    function draw(bar, show, x, y, len, pos, size, client, top) {
        var t = bar.track;
        if (!show) {
            t.style.display = 'none';
            return;
        }
        t.style.display = 'block';
        t.style.left = Math.round(x) + 'px';
        t.style.top = Math.round(y) + 'px';
        t.style.width = (bar.vertical ? SIZE : len) + 'px';
        t.style.height = (bar.vertical ? len : SIZE) + 'px';
        t.style.zIndex = top ? 1100 : 100;

        var thumbLen = Math.min(len, Math.max(MIN_THUMB, Math.round(len * client / size)));
        var range = size - client;
        var thumbPos = range > 0 ? Math.round(Math.min(1, Math.max(0, pos / range)) * (len - thumbLen)) : 0;
        bar.len = len;
        bar.thumbLen = thumbLen;
        bar.thumbPos = thumbPos;
        bar.range = range;

        // the texture has 2px of empty space before the box and 4px after (shadow),
        // so the element is a bit bigger than the part you actually see
        var th = bar.thumb.style;
        if (bar.vertical) {
            th.left = '-1px'; th.width = (SIZE + 4) + 'px';
            th.top = (thumbPos - 2) + 'px'; th.height = (thumbLen + 6) + 'px';
        } else {
            th.top = '-1px'; th.height = (SIZE + 4) + 'px';
            th.left = (thumbPos - 2) + 'px'; th.width = (thumbLen + 6) + 'px';
        }
    }

    function docOverflowHidden() {
        var ov = getComputedStyle(document.documentElement).overflowY;
        if (ov === 'visible') ov = getComputedStyle(document.body).overflowY;
        return ov === 'hidden' || ov === 'clip';
    }

    function update(b) {
        var s = scroller(b);
        var left, top, w, h, canY, canX, onTop = false;

        if (b.doc) {
            left = 0; top = 0; w = s.clientWidth; h = s.clientHeight;
            var locked = docOverflowHidden();
            canY = !locked && s.scrollHeight > h + 1;
            canX = !locked && s.scrollWidth > w + 1;
        } else {
            var r = b.el.getBoundingClientRect();
            var cs = getComputedStyle(b.el);
            left = r.left + b.el.clientLeft; top = r.top + b.el.clientTop;
            w = b.el.clientWidth; h = b.el.clientHeight;
            canY = /auto|scroll/.test(cs.overflowY) && b.el.scrollHeight > h + 1;
            canX = /auto|scroll/.test(cs.overflowX) && b.el.scrollWidth > w + 1;

            // while a modal is open only its own scrollers get bars
            var modal = b.el.closest('.modal');
            onTop = !!modal;
            if (!modal && document.querySelector('.modal.show')) canY = canX = false;

            var offscreen = r.bottom < 0 || r.top > window.innerHeight || r.right < 0 || r.left > window.innerWidth;
            if (w < SIZE * 2 || h < SIZE * 2 || offscreen) canY = canX = false;
        }

        draw(b.y, canY, left + w - SIZE, top, canX ? h - SIZE : h, s.scrollTop, s.scrollHeight, h, onTop);
        draw(b.x, canX, left, top + h - SIZE, canY ? w - SIZE : w, s.scrollLeft, s.scrollWidth, w, onTop);
    }

    function updateAll() {
        queued = false;
        for (var i = bars.length - 1; i >= 0; i--) {
            if (!bars[i].doc && !bars[i].el.isConnected) drop(i);
            else update(bars[i]);
        }
    }

    function schedule() {
        if (queued) return;
        queued = true;
        requestAnimationFrame(updateAll);
    }

    function check(el) {
        if (seen.has(el) || el.closest('.ore-sb-host')) return;
        var cs = getComputedStyle(el);
        if (/auto|scroll/.test(cs.overflowY + ' ' + cs.overflowX)) {
            seen.add(el);
            attach(el, false);
        }
    }

    function scan(node) {
        if (node.nodeType !== 1) return;
        check(node);
        var kids = node.getElementsByTagName('*');
        for (var i = 0; i < kids.length; i++) check(kids[i]);
    }

    attach(document.documentElement, true);
    scan(document.body);

    new MutationObserver(function (muts) {
        for (var i = 0; i < muts.length; i++) {
            for (var j = 0; j < muts[i].addedNodes.length; j++) scan(muts[i].addedNodes[j]);
        }
        schedule();
    }).observe(document.body, { childList: true, subtree: true, characterData: true });

    window.addEventListener('scroll', schedule, true);
    window.addEventListener('resize', schedule);
    setInterval(schedule, 500); // catches layout changes that don't touch the DOM
    schedule();
})();
