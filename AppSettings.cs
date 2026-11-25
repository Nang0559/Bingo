using System.Globalization;

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
