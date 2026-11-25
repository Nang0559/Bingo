// File: wwwroot/js/bingoCapture.js

window.BingoCapture = {
    /**
     * Chuyển đổi một phần tử HTML thành chuỗi Base64 của hình ảnh PNG.
     * @param {string} elementId - ID của phần tử cần chụp (ví dụ: 'popup-content-id').
     * @returns {string} Chuỗi Base64 của ảnh.
     */
    captureElement: async function (elementId) {
        const element = document.getElementById(elementId);

        if (!element) {
            console.error(`Không tìm thấy phần tử với ID: ${elementId}`);
            return null;
        }

        // Cấu hình cho html2canvas (tùy chọn)
        const canvas = await html2canvas(element, {
            useCORS: true, // Quan trọng nếu bạn có ảnh bên ngoài
            scale: 2      // Tăng độ phân giải ảnh chụp
        });

        // Chuyển canvas thành chuỗi Base64 (định dạng PNG)
        return canvas.toDataURL("image/png");
    }
};
// File: wwwroot/js/bingoCapture.js (hoặc animation.js)

window.AnimationHelper = {
    /**
     * Lấy vị trí của phần tử được click.
     * @param {string} elementId - ID của quả bóng được click (ví dụ: 'ball-15').
     * @returns {object} Tọa độ { top, left, width, height } của phần tử.
     */
    getClickPosition: function (elementId) {
        const element = document.getElementById(elementId);
        if (element) {
            const rect = element.getBoundingClientRect();
            return {
                top: rect.top,
                left: rect.left,
                width: rect.width,
                height: rect.height
            };
        }
        return null;
    },

    /**
     * Cho quả bóng clone bay tới đích (thay cho eval trong C#).
     */
    flyTo: function (elementId, dx, dy) {
        const el = document.getElementById(elementId);
        if (!el) return;
        requestAnimationFrame(() => requestAnimationFrame(() => {
            el.style.transform = `translate(${dx}px, ${dy}px) scale(1.77)`;
            el.style.opacity = '0.5';
        }));
    }
};
// File: wwwroot/js/bingoCapture.js

//window.ConfettiHelper = {
//    /**
//     * Kích hoạt hiệu ứng pháo hoa Confetti
//     */
//    startConfetti: function () {
//        // Kiểm tra xem confetti có sẵn sàng chưa
//        if (typeof confetti !== 'undefined') {

//            // Cấu hình cơ bản
//            var defaults = {
//                spread: 360,
//                ticks: 100,
//                gravity: 0.3,
//                decay: 0.94,
//                startVelocity: 30,
//                particleCount: 100,
//                scalar: 1.2
//            };

//            // Phóng từ giữa màn hình (hoặc trung tâm popup)
//            confetti(Object.assign({}, defaults, {
//                particleCount: 80,
//                origin: { x: 0.5, y: 0.8 }, // Phóng từ dưới lên
//                colors: ['#bb0000', '#ffffff', '#ffd700', '#007bff']
//            }));

//            // Phóng lần 2 với góc khác để hiệu ứng phong phú hơn
//            confetti(Object.assign({}, defaults, {
//                particleCount: 50,
//                origin: { x: 0.5, y: 0.8 },
//                angle: 270,
//                startVelocity: 20
//            }));
//        }
//    }
//};
// File: wwwroot/js/confettiHelper.js

window.ConfettiHelper = {
    _confettiInterval: null,
    _confettiInstance: null, // Giữ khai báo này để tránh lỗi tiềm ẩn

    startConfetti: function (seconds) {
        // Dọn dẹp interval cũ
        if (this._confettiInterval) {
            clearInterval(this._confettiInterval);
        }

        // Gán thẳng hàm confetti global
        this._confettiInstance = window.confetti;

        // Cấu hình pháo hoa
        const duration = (seconds > 0 ? seconds : 15) * 1000;
        const animationEnd = Date.now() + duration;
        let skew = 1;

        const frame = () => {
            const timeLeft = animationEnd - Date.now();
            if (timeLeft <= 0) return;

            skew = Math.max(0.8, skew - 0.001);

            // 💥 BẮN PHÁO HOA 💥
            if (typeof this._confettiInstance === 'function') {
                this._confettiInstance({
                    particleCount: 1,
                    startVelocity: 0,
                    ticks: Math.max(200, 500 * (timeLeft / duration)),
                    origin: {
                        x: Math.random(),
                        y: (Math.random() * skew) - 0.2
                    },
                    colors: ['#bb0000', '#ffffff', '#007bff', '#ffea00'],
                    shapes: ['circle', 'square'],
                    gravity: 1.2,
                    scalar: 0.8,
                    drift: Math.random() * 0.4 - 0.2,
                    spread: 360,
                    decay: 0.92,
                    zIndex: 99999
                });
            }

            // GỌI LẠI FRAME CHỈ MỘT LẦN (NẾU VẪN CÒN THỜI GIAN)
            requestAnimationFrame(frame);
        };

        // Bắt đầu chạy hiệu ứng lặp
        frame();

        // 💥 HÀM CHÍNH CHO HIỆU ỨNG "LIÊN TỤC" (GỌI LẠI HÀM frame() SAU MỖI 1 GIÂY) 💥
        this._confettiInterval = setInterval(() => {
            // Chỉ cần gọi frame() một lần nữa để bắn một đợt confetti mới
            if (Date.now() < animationEnd) {
                frame();
            } else {
                clearInterval(this._confettiInterval);
            }
        }, 1000); // Mỗi 1 giây bắn một đợt Confetti mới
    },

    stopConfetti: function () {
        if (this._confettiInterval) {
            clearInterval(this._confettiInterval);
            this._confettiInterval = null;
        }
        // Gọi hàm reset toàn cục nếu nó tồn tại
        if (typeof window.confetti.reset === 'function') {
            window.confetti.reset();
        }
        else {
            // 💥 GIẢI PHÁP CUỐI CÙNG: XÓA THỦ CÔNG CANVAS PHÁO HOA 💥
            const canvas = document.querySelector('canvas.confetti-canvas');
            if (canvas) {
                canvas.remove();
            }
        }
    }
};