using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Bingo
{
    /// <summary>Thống kê thẻ tại thời điểm hiện tại của ván.</summary>
    public readonly record struct CardStats(int Total, int Awarded, int Winning, int Missing1, int Missing2)
    {
        /// <summary>Số thẻ chưa được trao thưởng.</summary>
        public int Remaining => Total - Awarded;
    }

    /// <summary>
    /// Phát hành, lưu và thống kê thẻ Bingo.
    /// Mỗi thẻ là 1 dãy số khác nhau; thẻ trúng khi TẤT CẢ số trên thẻ đã được quay
    /// (cùng quy tắc với phần nhập dãy số kiểm tra trúng thưởng).
    /// </summary>
    public class CardService
    {
        public const int MaxParticipants = 5000;
        public const int CardsPerSheet = 8; // 2 cột x 4 hàng trên 1 tờ A4

        private const string StorageKey = "bingo.cards.v1";

        private readonly IJSRuntime _js;
        private readonly SettingsService _settings;

        private CardSet? _set;
        private readonly HashSet<string> _awarded = new();

        public CardService(IJSRuntime js, SettingsService settings)
        {
            _js = js;
            _settings = settings;
            // Khi thiết lập đổi (Lưu) → đồng bộ lại bộ thẻ nếu cần
            _settings.OnChange += () => _ = SyncAsync();
        }

        /// <summary>Báo cho giao diện vẽ lại khi bộ thẻ hoặc danh sách thẻ đã trao thay đổi.</summary>
        public event Action? OnChange;

        public bool HasCards => _set != null && _set.Cards.Count > 0;
        public int Count => _set?.Cards.Count ?? 0;
        public int Sheets => (Count + CardsPerSheet - 1) / CardsPerSheet;

        public class CardSet
        {
            public int Total { get; set; }
            public int NumbersPerCard { get; set; }
            public List<int[]> Cards { get; set; } = new();
        }

        // ---------- Nạp / đồng bộ ----------
        public async Task LoadAsync()
        {
            try
            {
                var json = await _js.InvokeAsync<string?>("SettingsHelper.load", StorageKey);
                if (!string.IsNullOrWhiteSpace(json))
                    _set = JsonSerializer.Deserialize<CardSet>(json);
            }
            catch
            {
                _set = null;
            }
            await SyncAsync();
        }

        /// <summary>Tạo mới bộ thẻ khi thông số đổi; giữ nguyên nếu thông số không đổi (để thẻ đã in vẫn dùng được).</summary>
        public async Task SyncAsync()
        {
            var s = _settings.Current;
            int want = s.CardsIssued;

            if (want <= 0)
            {
                if (_set != null)
                {
                    _set = null;
                    _awarded.Clear();
                    await PersistAsync();
                }
                OnChange?.Invoke();
                return;
            }

            bool same = _set != null
                        && _set.Total == s.TotalNumbers
                        && _set.NumbersPerCard == s.NumbersPerCard
                        && _set.Cards.Count == want;
            if (same) { OnChange?.Invoke(); return; }

            await RegenerateAsync();
        }

        /// <summary>Tạo lại toàn bộ thẻ theo thiết lập đã lưu (thẻ đã in trước đó không còn dùng được).</summary>
        public async Task RegenerateAsync()
        {
            var s = _settings.Current;
            _awarded.Clear();
            _set = s.CardsIssued <= 0 ? null : Generate(s.TotalNumbers, s.NumbersPerCard, s.CardsIssued);
            await PersistAsync();
            OnChange?.Invoke();
        }

        private async Task PersistAsync()
        {
            try
            {
                await _js.InvokeAsync<bool>("SettingsHelper.save", StorageKey,
                    _set == null ? "" : JsonSerializer.Serialize(_set));
            }
            catch { /* không lưu được thì chỉ mất bộ thẻ khi tải lại trang */ }
        }

        // ---------- Sinh thẻ ----------
        /// <summary>Số dãy số khác nhau tối đa: C(total, k).</summary>
        public static double MaxUniqueCards(int total, int k)
        {
            if (k < 0 || k > total) return 0;
            k = Math.Min(k, total - k);
            double r = 1;
            for (int i = 1; i <= k; i++) r = r * (total - k + i) / i;
            return Math.Round(r);
        }

        /// <summary>Sinh <paramref name="count"/> thẻ KHÁC NHAU, mỗi thẻ <paramref name="k"/> số trong 1..total.</summary>
        public static CardSet Generate(int total, int k, int count)
        {
            var set = new CardSet { Total = total, NumbersPerCard = k };
            count = (int)Math.Min(count, MaxUniqueCards(total, k));

            var seen = new HashSet<string>();
            var pool = new int[total];
            long attempts = 0, maxAttempts = (long)count * 200 + 10000;

            while (set.Cards.Count < count && attempts++ < maxAttempts)
            {
                for (int i = 0; i < total; i++) pool[i] = i + 1;
                // Fisher–Yates một phần: chọn k số đầu
                for (int i = 0; i < k; i++)
                {
                    int j = Random.Shared.Next(i, total);
                    (pool[i], pool[j]) = (pool[j], pool[i]);
                }
                var card = pool.Take(k).OrderBy(n => n).ToArray();
                if (seen.Add(Key(card))) set.Cards.Add(card);
            }
            return set;
        }

        /// <summary>Khóa dãy số — cùng định dạng với khóa trao thưởng trong HistoryAndInput ("01-05-12").</summary>
        public static string Key(IEnumerable<int> numbers) => string.Join("-", numbers.OrderBy(n => n));

        // ---------- Thẻ đã trao thưởng ----------
        public void MarkAwarded(string key)
        {
            if (_awarded.Add(key)) OnChange?.Invoke();
        }

        public void ClearAwarded()
        {
            if (_awarded.Count == 0) return;
            _awarded.Clear();
            OnChange?.Invoke();
        }

        // ---------- Thống kê ----------
        /// <summary>
        /// Đếm số thẻ đã đủ số (có thể nhận thưởng ngay), còn thiếu 1 hoặc 2 số,
        /// dựa trên các số đã quay trong ván. Thẻ đã trao thưởng không tính.
        /// </summary>
        public CardStats GetStats(IEnumerable<int> drawn)
        {
            if (_set == null) return default;

            var d = new HashSet<int>(drawn);
            int awarded = 0, win = 0, m1 = 0, m2 = 0;

            foreach (var card in _set.Cards)
            {
                if (_awarded.Contains(Key(card))) { awarded++; continue; }

                int missing = 0;
                foreach (var n in card)
                    if (!d.Contains(n)) missing++;

                if (missing == 0) win++;
                else if (missing == 1) m1++;
                else if (missing == 2) m2++;
            }
            return new CardStats(_set.Cards.Count, awarded, win, m1, m2);
        }

        // ---------- In thẻ: 8 thẻ / tờ A4 ----------
        public string BuildPrintHtml(AppSettings s)
        {
            if (_set == null) return "";

            int digits = Math.Max(3, _set.Cards.Count.ToString().Length);
            string ball = s.BallColor;
            string ballText = AppSettings.ContrastText(ball);
            string title = WebUtility.HtmlEncode(s.Title);

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>")
              .Append(title).Append(" - Thẻ Bingo</title><style>")
              .Append("@page{size:A4 portrait;margin:0}")
              .Append("*{box-sizing:border-box}")
              .Append("html,body{margin:0;padding:0;background:#fff;font-family:Arial,Helvetica,sans-serif;")
              .Append("-webkit-print-color-adjust:exact;print-color-adjust:exact}")
              .Append(".sheet{width:210mm;height:296mm;padding:8mm;display:grid;")
              .Append("grid-template-columns:1fr 1fr;grid-template-rows:repeat(4,1fr);gap:4mm;page-break-after:always;break-after:page;overflow:hidden}")
              .Append(".sheet:last-child{page-break-after:auto;break-after:auto}")
              .Append(".card{border:0.3mm dashed #888;border-radius:3mm;padding:3mm 4mm;display:flex;flex-direction:column;")
              .Append("justify-content:space-between;overflow:hidden}")
              .Append(".hd{display:flex;justify-content:space-between;align-items:baseline;border-bottom:0.3mm solid #ccc;padding-bottom:1.5mm}")
              .Append(".t{font-size:5.5mm;font-weight:700;letter-spacing:.5mm}")
              .Append(".code{font-size:4.5mm;font-weight:700;color:#444}")
              .Append(".who{font-size:3.4mm;color:#555;text-align:center}")
              .Append(".nums{display:flex;flex-wrap:wrap;justify-content:center;align-content:center;gap:2.5mm;flex:1;padding:2mm 0}")
              .Append(".b{width:14mm;height:14mm;border-radius:50%;display:flex;align-items:center;justify-content:center;")
              .Append("font-weight:800;font-size:7mm;color:").Append(ballText).Append(";background-color:").Append(ball).Append(";")
              .Append("background-image:radial-gradient(circle at 30% 30%,rgba(255,255,255,.45),rgba(255,255,255,0) 60%);")
              .Append("box-shadow:inset 0 -1mm 2mm rgba(0,0,0,.18)}")
              .Append(".ft{font-size:3mm;color:#666;text-align:center}")
              .Append("</style></head><body>");

            int perPerson = Math.Max(1, s.CardsPerPerson);
            int total = _set.Cards.Count;

            for (int start = 0; start < total; start += CardsPerSheet)
            {
                sb.Append("<div class=\"sheet\">");
                for (int i = start; i < start + CardsPerSheet; i++)
                {
                    if (i >= total) { sb.Append("<div></div>"); continue; } // giữ nguyên lưới ở tờ cuối

                    int person = i / perPerson + 1;
                    int nth = i % perPerson + 1;
                    string code = "#" + (i + 1).ToString("D" + digits);
                    string who = perPerson > 1
                        ? $"Người chơi {person.ToString("D" + digits)} · Thẻ {nth}/{perPerson}"
                        : $"Người chơi {person.ToString("D" + digits)}";

                    sb.Append("<div class=\"card\"><div class=\"hd\"><span class=\"t\">").Append(title)
                      .Append("</span><span class=\"code\">").Append(code).Append("</span></div>")
                      .Append("<div class=\"who\">").Append(who).Append("</div><div class=\"nums\">");
                    foreach (var n in _set.Cards[i])
                        sb.Append("<span class=\"b\">").Append(n.ToString("00")).Append("</span>");
                    sb.Append("</div><div class=\"ft\">Trúng thưởng khi cả ")
                      .Append(_set.NumbersPerCard).Append(" số đều được quay</div></div>");
                }
                sb.Append("</div>");
            }

            sb.Append("</body></html>");
            return sb.ToString();
        }
    }
}