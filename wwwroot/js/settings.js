// File: wwwroot/js/settings.js
window.SettingsHelper = {
    load: function (key) {
        try { return window.localStorage.getItem(key); } catch (e) { return null; }
    },

    save: function (key, json) {
        try { window.localStorage.setItem(key, json); return true; } catch (e) { return false; }
    },

    setTitle: function (title) {
        document.title = title || 'Bingo';
    },

    // Đọc file ảnh từ <input type="file" id=...>, thu nhỏ rồi trả về data URL
    // mime: 'image/jpeg' | 'image/png' | 'auto' (giữ PNG nếu ảnh gốc không phải JPEG)
    pickImage: function (inputId, maxSize, mime, quality) {
        return new Promise(function (resolve) {
            const input = document.getElementById(inputId);
            const file = input && input.files && input.files[0];
            if (!file) { resolve(null); return; }

            const reader = new FileReader();
            reader.onerror = function () { resolve(null); };
            reader.onload = function () {
                const img = new Image();
                img.onerror = function () { resolve(null); };
                img.onload = function () {
                    let w = img.naturalWidth, h = img.naturalHeight;
                    const scale = Math.min(1, maxSize / Math.max(w, h));
                    w = Math.max(1, Math.round(w * scale));
                    h = Math.max(1, Math.round(h * scale));

                    const canvas = document.createElement('canvas');
                    canvas.width = w;
                    canvas.height = h;
                    const ctx = canvas.getContext('2d');

                    let outMime = mime;
                    if (mime === 'auto') outMime = (file.type === 'image/jpeg') ? 'image/jpeg' : 'image/png';
                    if (outMime === 'image/jpeg') {
                        ctx.fillStyle = '#ffffff';
                        ctx.fillRect(0, 0, w, h);
                    }
                    ctx.drawImage(img, 0, 0, w, h);
                    input.value = '';
                    resolve(canvas.toDataURL(outMime, quality));
                };
                img.src = reader.result;
            };
            reader.readAsDataURL(file);
        });
    },

    // In HTML (thẻ Bingo) bằng iframe ẩn — không bị chặn popup
    printHtml: function (html) {
        const old = document.getElementById('bingo-print-frame');
        if (old) old.remove();
        const f = document.createElement('iframe');
        f.id = 'bingo-print-frame';
        f.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden';
        f.onload = function () {
            try {
                f.contentWindow.focus();
                f.contentWindow.print();
            } catch (e) { window.showCustomError('Không mở được hộp thoại in: ' + e); }
        };
        document.body.appendChild(f);
        f.srcdoc = html;
    },

    // Đọc file văn bản (JSON nhập thiết lập)
    readText: function (inputId) {
        return new Promise(function (resolve) {
            const input = document.getElementById(inputId);
            const file = input && input.files && input.files[0];
            if (!file) { resolve(null); return; }
            const reader = new FileReader();
            reader.onerror = function () { resolve(null); };
            reader.onload = function () { input.value = ''; resolve(String(reader.result)); };
            reader.readAsText(file);
        });
    }
};
