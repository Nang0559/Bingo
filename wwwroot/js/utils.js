// File: wwwroot/js/utils.js

// Hàm này lấy chuỗi base64 và tên file, sau đó buộc trình duyệt tải xuống
window.SaveFile = function (filename, base64Data) {
    const link = document.createElement('a');
    link.href = base64Data; // Chuỗi base64 có thể dùng trực tiếp làm href
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};
window.showCustomError = function (message, title = 'Lỗi Phát Sinh!') {
    const overlay = document.getElementById('custom-alert-overlay');
    if (!overlay) {
        window.alert("LỖI PHÁT SINH: " + message);
        return;
    }

    document.getElementById('custom-alert-title').innerText = title;
    document.getElementById('custom-alert-message').innerText = message;

    // HIỂN THỊ
    overlay.style.display = 'flex';
};