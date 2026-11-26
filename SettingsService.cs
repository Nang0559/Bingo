using System.Text.Json;
using Microsoft.JSInterop;

namespace Bingo
{
    public class SettingsService
    {
        private const string StorageKey = "bingo.settings.v1";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly IJSRuntime _js;

        public SettingsService(IJSRuntime js) => _js = js;

        public AppSettings Current { get; private set; } = AppSettings.CreateDefault();

        /// <summary>Được gọi sau khi lưu thiết lập mới.</summary>
        public event Action? OnChange;

        public async Task LoadAsync()
        {
            try
            {
                var json = await _js.InvokeAsync<string?>("SettingsHelper.load", StorageKey);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) Current = Normalize(loaded);
                }
            }
            catch
            {
                Current = AppSettings.CreateDefault();
            }
            await ApplyTitleAsync();
        }

        /// <summary>Áp dụng thiết lập. Trả về false nếu không lưu được vào trình duyệt (vẫn áp dụng trong phiên này).</summary>
        public async Task<bool> SaveAsync(AppSettings settings)
        {
            Current = Normalize(settings);
            bool saved;
            try
            {
                saved = await _js.InvokeAsync<bool>("SettingsHelper.save", StorageKey, JsonSerializer.Serialize(Current));
            }
            catch
            {
                saved = false;
            }
            await ApplyTitleAsync();
            OnChange?.Invoke();
            return saved;
        }

        public static AppSettings Clone(AppSettings s) =>
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s)) ?? AppSettings.CreateDefault();

        public static string Export(AppSettings s) => JsonSerializer.Serialize(s, JsonOptions);

        public static AppSettings? TryImport(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                return s == null ? null : Normalize(s);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Ép các giá trị về khoảng hợp lệ để app không bao giờ rơi vào trạng thái lỗi.</summary>
        public static AppSettings Normalize(AppSettings s)
        {
            s.Title = string.IsNullOrWhiteSpace(s.Title) ? "BINGO" : s.Title.Trim();
            s.BackgroundOverlay = Math.Clamp(s.BackgroundOverlay, 0, 1);
            s.TotalNumbers = Math.Clamp(s.TotalNumbers, 20, 99);
            s.MinWinCount = Math.Clamp(s.MinWinCount, 1, 15);
            s.MaxWinCount = Math.Clamp(s.MaxWinCount, s.MinWinCount, 15);
            s.ConfettiSeconds = Math.Clamp(s.ConfettiSeconds, 1, 60);

            s.BallColor = AppSettings.NormalizeColor(s.BallColor);
            s.Participants = Math.Clamp(s.Participants, 0, CardService.MaxParticipants);
            s.CardsPerPerson = Math.Clamp(s.CardsPerPerson, 1, 10);

            s.Rewards ??= new List<RewardSetting>();
            if (s.Rewards.Count == 0)
                s.Rewards = AppSettings.CreateDefault().Rewards;

            // Id phải duy nhất và > 0
            var used = new HashSet<int>();
            var next = Math.Max(1, s.Rewards.Max(r => r.Id) + 1);
            for (int i = 0; i < s.Rewards.Count; i++)
            {
                var r = s.Rewards[i];
                if (r.Id <= 0 || !used.Add(r.Id))
                {
                    r.Id = next++;
                    used.Add(r.Id);
                }
                r.Name = string.IsNullOrWhiteSpace(r.Name) ? $"Giải {i + 1}" : r.Name.Trim();
                if (string.IsNullOrWhiteSpace(r.ImagePath))
                    r.ImagePath = $"images/reward{(i % 5) + 1}.png";
            }
            return s;
        }

        private async Task ApplyTitleAsync()
        {
            try { await _js.InvokeVoidAsync("SettingsHelper.setTitle", Current.Title); } catch { }
        }
    }
}
