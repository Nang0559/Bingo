// File: wwwroot/js/panelDrag.js
// Cho phép kéo thả một khung nổi (position:absolute) tự do bên trong khung chứa của nó.
// - Kéo phần tiêu đề (phần tử có thuộc tính data-drag-handle) để di chuyển.
// - Bấm (không kéo) vẫn hoạt động bình thường (vd. thu gọn / mở rộng).
// - Bấm đúp tiêu đề hoặc gọi PanelDrag.reset để đưa về vị trí mặc định trong CSS.
// - Vị trí được nhớ trong localStorage và luôn được giữ nằm trong khung chứa.
window.PanelDrag = (function () {
    var DRAG_THRESHOLD = 4; // px: di chuyển ít hơn mức này vẫn tính là "bấm"

    function load(key) {
        try {
            var v = JSON.parse(window.localStorage.getItem(key));
            if (v && isFinite(v.x) && isFinite(v.y)) return v;
        } catch (e) { }
        return null;
    }
    function save(key, pos) { try { window.localStorage.setItem(key, JSON.stringify(pos)); } catch (e) { } }
    function forget(key) { try { window.localStorage.removeItem(key); } catch (e) { } }

    // Vị trí/kích thước của el so với góc trong của container
    function relRect(el, container) {
        var c = container.getBoundingClientRect();
        var r = el.getBoundingClientRect();
        return {
            left: r.left - c.left - container.clientLeft,
            top: r.top - c.top - container.clientTop,
            width: r.width, height: r.height,
            cw: container.clientWidth, ch: container.clientHeight
        };
    }

    function place(el, x, y) {
        el.style.left = x + 'px';
        el.style.top = y + 'px';
        el.style.right = 'auto';
        el.style.bottom = 'auto';
    }

    function clampPos(st, x, y) {
        var r = relRect(st.el, st.container);
        return {
            x: Math.min(Math.max(0, x), Math.max(0, r.cw - r.width)),
            y: Math.min(Math.max(0, y), Math.max(0, r.ch - r.height))
        };
    }

    // Chỉ chỉnh lại khi người dùng đã tự đặt vị trí (khi chưa, CSS lo việc neo góc phải dưới)
    function clampIfPlaced(st) {
        if (!document.body.contains(st.el)) { // phần tử đã bị Blazor gỡ: dọn listener
            window.removeEventListener('resize', st.onWinResize);
            if (st.ro) st.ro.disconnect();
            return;
        }
        if (!st.el.style.left) return;
        var r = relRect(st.el, st.container);
        var p = clampPos(st, r.left, r.top);
        if (Math.abs(p.x - r.left) > 0.5 || Math.abs(p.y - r.top) > 0.5) place(st.el, p.x, p.y);
    }

    function init(el, container, key) {
        var handle = el.querySelector('[data-drag-handle]') || el;
        var st = { el: el, container: container, key: key, dragging: false, swallowClick: false };
        el.__panelDrag = st;

        var saved = load(key);
        if (saved) {
            var p0 = clampPos(st, saved.x, saved.y);
            place(el, p0.x, p0.y);
        }

        var pid = null, startX = 0, startY = 0, startLeft = 0, startTop = 0;

        handle.addEventListener('pointerdown', function (e) {
            if (e.button !== undefined && e.button !== 0) return;
            if (e.target.closest && e.target.closest('button, a, input, select, textarea')) return;
            var r = relRect(el, container);
            startX = e.clientX; startY = e.clientY; startLeft = r.left; startTop = r.top;
            pid = e.pointerId;
            st.dragging = false;
            try { handle.setPointerCapture(pid); } catch (err) { }
        });

        handle.addEventListener('pointermove', function (e) {
            if (pid === null || e.pointerId !== pid) return;
            var dx = e.clientX - startX, dy = e.clientY - startY;
            if (!st.dragging) {
                if (Math.abs(dx) < DRAG_THRESHOLD && Math.abs(dy) < DRAG_THRESHOLD) return;
                st.dragging = true;
                el.classList.add('dragging');
            }
            var p = clampPos(st, startLeft + dx, startTop + dy);
            place(el, p.x, p.y);
            e.preventDefault();
        });

        function end(e) {
            if (pid === null || (e && e.pointerId !== pid)) return;
            try { handle.releasePointerCapture(pid); } catch (err) { }
            pid = null;
            if (st.dragging) {
                st.dragging = false;
                el.classList.remove('dragging');
                // Nuốt đúng 1 cú click do trình duyệt sinh ra ngay sau khi thả chuột (kèm hạn chót
                // phòng khi trình duyệt không sinh click), để cú bấm thật sau đó vẫn hoạt động bình thường
                st.swallowClick = true;
                setTimeout(function () { st.swallowClick = false; }, 120);
                var r = relRect(el, container);
                save(key, { x: r.left, y: r.top });
            }
        }
        handle.addEventListener('pointerup', end);
        handle.addEventListener('pointercancel', end);

        // Sau khi kéo, không để cú click làm thu gọn / mở rộng khung
        handle.addEventListener('click', function (e) {
            if (st.swallowClick) { st.swallowClick = false; e.stopPropagation(); e.preventDefault(); }
        }, true);

        handle.addEventListener('dblclick', function () { reset(el.id); });

        st.onWinResize = function () { clampIfPlaced(st); };
        window.addEventListener('resize', st.onWinResize);
        if (window.ResizeObserver) { // khung đổi kích thước (thu gọn / mở rộng / thêm thẻ) vẫn nằm trong vùng quay số
            st.ro = new ResizeObserver(function () { clampIfPlaced(st); });
            st.ro.observe(el);
            st.ro.observe(container);
        }
    }

    function reset(panelId) {
        var el = document.getElementById(panelId);
        if (!el) return;
        el.style.left = el.style.top = el.style.right = el.style.bottom = '';
        el.classList.remove('dragging');
        if (el.__panelDrag) forget(el.__panelDrag.key);
    }

    // Gọi sau mỗi lần Blazor render: an toàn khi gọi nhiều lần (chỉ khởi tạo 1 lần cho mỗi phần tử).
    function ensure(panelId, containerSelector, storageKey) {
        var el = document.getElementById(panelId);
        if (!el) return;
        if (el.__panelDrag) { clampIfPlaced(el.__panelDrag); return; }
        var container = (containerSelector && el.closest(containerSelector)) || el.parentElement;
        init(el, container, storageKey || ('panelpos.' + panelId));
    }

    return { ensure: ensure, reset: reset };
})();
