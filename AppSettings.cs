using System.Globalization;
using System.Text.Json.Serialization;

namespace Bingo
{
    /// <summary>Toàn bộ thiết lập của chương trình (lưu trong localStorage của trình duyệt).</summary>
    public class AppSettings
    {
        public const string DefaultBackground = "images/logo.jpg";

        public string Title { get; set; } = "BINGO";

        /// <summary>Đường dẫn ảnh hoặc data URL. null/rỗng = không dùng ảnh nền.</summary>
        public string? BackgroundImage { get; set; } = DefaultBackground;

        /// <summary>Độ phủ trắng lên ảnh nền (0 = rõ nhất, 1 = mờ hết).</summary>
        public double BackgroundOverlay { get; set; } = 0.7;

        public int TotalNumbers { get; set; } = 75;
        public int MinWinCount { get; set; } = 4;
        public int MaxWinCount { get; set; } = 5;
        public int ConfettiSeconds { get; set; } = 15;

        // ----- Màu bóng -----
        public const string DefaultBallColor = "#28a745";

        /// <summary>Màu bóng dạng #rrggbb (áp dụng cho bảng số, bóng bay, bóng đã quay và thẻ in).</summary>
        public string BallColor { get; set; } = DefaultBallColor;

        // ----- Thẻ Bingo 5x5 (trúng khi đủ 1 hàng / cột / đường chéo; ô giữa FREE) -----
        /// <summary>Số người tham gia. 0 = không phát hành thẻ.</summary>
        public int Participants { get; set; } = 0;

        /// <summary>Số thẻ phát cho mỗi người.</summary>
        public int CardsPerPerson { get; set; } = 1;

        /// <summary>Tổng số thẻ phát hành = người tham gia x thẻ mỗi người.</summary>
        [JsonIgnore]
        public int CardsIssued => Math.Max(0, Participants) * Math.Max(1, CardsPerPerson);

        public List<RewardSetting> Rewards { get; set; } = new();

        public static AppSettings CreateDefault() => new()
        {
            Rewards = new List<RewardSetting>
            {
                new() { Id = 1, Name = "Giải Nhất", ImagePath = "images/reward1.png" },
                new() { Id = 2, Name = "Giải Nhì", ImagePath = "images/reward2.png" },
                new() { Id = 3, Name = "Giải Ba", ImagePath = "images/reward3.png" },
                new() { Id = 4, Name = "Giải Tư", ImagePath = "images/reward4.png" },
                new() { Id = 5, Name = "Giải Khuyến Khích", ImagePath = "images/reward5.png" },
            }
        };

        /// <summary>Chuẩn hóa về #rrggbb hợp lệ (tránh chèn chuỗi lạ vào CSS/HTML).</summary>
        public static string NormalizeColor(string? c)
        {
            if (string.IsNullOrWhiteSpace(c)) return DefaultBallColor;
            c = c.Trim();
            if (c.Length == 4 && c[0] == '#') c = "#" + string.Concat(c.Skip(1).Select(ch => $"{ch}{ch}"));
            bool ok = c.Length == 7 && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit);
            return ok ? c.ToLowerInvariant() : DefaultBallColor;
        }

        /// <summary>Màu chữ (đen/trắng) dễ đọc trên nền màu <paramref name="hex"/>.</summary>
        public static string ContrastText(string hex)
        {
            hex = NormalizeColor(hex);
            int r = Convert.ToInt32(hex.Substring(1, 2), 16);
            int g = Convert.ToInt32(hex.Substring(3, 2), 16);
            int b = Convert.ToInt32(hex.Substring(5, 2), 16);
            double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            return lum > 0.6 ? "#111111" : "#ffffff";
        }

        /// <summary>Biến CSS dùng chung cho mọi quả bóng.</summary>
        public static string BuildBallVars(string? color)
        {
            var c = NormalizeColor(color);
            return $"--ball-color:{c};--ball-text:{ContrastText(c)};";
        }

        /// <summary>Chuỗi CSS "background" gồm ảnh nền + lớp phủ trắng.</summary>
        public static string BuildBackgroundStyle(string? image, double overlay)
        {
            if (string.IsNullOrWhiteSpace(image))
                return "background: #e6e6ff;";

            var a = Math.Clamp(overlay, 0, 1).ToString("0.##", CultureInfo.InvariantCulture);
            var safe = image.Replace("'", "%27").Replace("\n", "").Replace("\r", "");
            return $"background: linear-gradient(rgba(255,255,255,{a}), rgba(255,255,255,{a})), url('{safe}'); " +
                   "background-size: cover; background-repeat: no-repeat; background-position: center center;";
        }
    }

    public class RewardSetting
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string ImagePath { get; set; } = "";
    }
}
