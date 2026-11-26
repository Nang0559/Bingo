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
    /// Mỗi thẻ là 1 dãy số khác nhau; thẻ trúng khi TẤT CẢ số trên thẻ đã được quay.
    /// Bộ thẻ sinh theo SEED (cùng seed + thông số = cùng bộ thẻ). Sau khi in/khóa (Issued)
    /// bộ thẻ không bao giờ bị tạo lại; tăng số người chỉ NỐI THÊM thẻ phía sau.
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
        private Dictionary<string, int> _index = new();
        private int _pendingSeed = NewSeed();

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
        public bool Issued => _set != null && _set.Issued;
        public string? IssuedAt => _set?.IssuedAt;
        public int Seed => _set?.Seed ?? 0;
        public int PendingSeed => _pendingSeed;
        public int SetTotal => _set?.Total ?? 0;
        public int SetNumbersPerCard => _set?.NumbersPerCard ?? 0;
        public int SetPerPerson => _set?.PerPerson ?? 1;

        public class CardSet
        {
            public int Total { get; set; }
            public int NumbersPerCard { get; set; }
            public List<int[]> Cards { get; set; } = new();

            /// <summary>Seed sinh thẻ (0 = bộ thẻ cũ tạo trước khi có seed).</summary>
            public int Seed { get; set; }

            /// <summary>Số thẻ phát cho mỗi người tại thời điểm tạo bộ thẻ.</summary>
            public int PerPerson { get; set; } = 1;

            /// <summary>true = đã in/khóa: không bao giờ tạo lại hay xóa thẻ, chỉ được nối thêm.</summary>
            public bool Issued { get; set; }

            public string? IssuedAt { get; set; }
        }

        /// <summary>Một thẻ để hiển thị (xem trước / tra cứu).</summary>
        public record CardView(int Index, string Code, string Who, int[] Numbers);

        private static int NewSeed() => Random.Shared.Next(1, int.MaxValue);

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

            if (_set != null && _set.Cards.Count == 0) _set = null;
            if (_set != null)
            {
                if (_set.PerPerson < 1) _set.PerPerson = 1;
                // Bộ thẻ tạo bởi phiên bản cũ (chưa có seed): coi như ĐÃ PHÁT HÀNH để không bị tạo lại.
                if (_set.Seed == 0) _set.Issued = true;
                else _pendingSeed = _set.Seed;
            }
            RebuildIndex();
            await SyncAsync();
        }

        /// <summary>Gọi khi mở Cài đặt: bỏ seed "xáo thử" chưa lưu, quay về seed của bộ thẻ hiện tại.</summary>
        public void ResetPending()
        {
            _pendingSeed = _set is { Seed: > 0 } ? _set.Seed : _pendingSeed;
        }

        /// <summary>Chọn một seed khác (chỉ có tác dụng khi bộ thẻ CHƯA phát hành, sau khi bấm Lưu).</summary>
        public void ReshufflePending()
        {
            if (Issued) return;
            _pendingSeed = NewSeed();
        }

        private void RebuildIndex()
        {
            _index = new Dictionary<string, int>();
            if (_set == null) return;
            for (int i = 0; i < _set.Cards.Count; i++) _index[Key(_set.Cards[i])] = i;
        }

        /// <summary>
        /// Đồng bộ bộ thẻ với thiết lập.
        /// - Đã phát hành: KHÔNG tạo lại / xóa thẻ; nếu tăng số người (cùng thông số) thì nối thêm thẻ.
        /// - Chưa phát hành: tạo lại theo seed khi thông số đổi.
        /// </summary>
        public async Task SyncAsync()
        {
            var s = _settings.Current;
            int want = (int)Math.Min(s.CardsIssued, MaxUniqueCards(s.TotalNumbers, s.NumbersPerCard));

            if (_set != null && _set.Issued)
            {
                bool sameParams = _set.Total == s.TotalNumbers
                                  && _set.NumbersPerCard == s.NumbersPerCard
                                  && _set.PerPerson == s.CardsPerPerson;
                if (sameParams && want > _set.Cards.Count) await ExtendAsync(want);
                else OnChange?.Invoke();
                return;
            }

            if (want <= 0)
            {
                if (_set != null)
                {
                    _set = null;
                    _awarded.Clear();
                    RebuildIndex();
                    await PersistAsync();
                }
                OnChange?.Invoke();
                return;
            }

            bool same = _set != null
                        && _set.Total == s.TotalNumbers
                        && _set.NumbersPerCard == s.NumbersPerCard
                        && _set.Cards.Count == want
                        && _set.Seed == _pendingSeed;
            if (same)
            {
                if (_set!.PerPerson != s.CardsPerPerson)
                {
                    _set.PerPerson = s.CardsPerPerson; // cùng bộ thẻ, chỉ đổi cách chia cho từng người
                    await PersistAsync();
                }
                OnChange?.Invoke();
                return;
            }

            await RegenerateAsync();
        }

        /// <summary>Tạo lại toàn bộ thẻ theo thiết lập (chỉ dùng khi chưa phát hành).</summary>
        public async Task RegenerateAsync()
        {
            var s = _settings.Current;
            _awarded.Clear();
            if (s.CardsIssued <= 0) _set = null;
            else
            {
                _set = Generate(s.TotalNumbers, s.NumbersPerCard, s.CardsIssued, _pendingSeed);
                _set.PerPerson = s.CardsPerPerson;
                if (_set.Cards.Count == 0) _set = null;
            }
            RebuildIndex();
            await PersistAsync();
            OnChange?.Invoke();
        }

        /// <summary>Nối thêm thẻ vào bộ đã phát hành; các thẻ cũ giữ nguyên 100%.</summary>
        private async Task ExtendAsync(int want)
        {
            if (_set == null) return;
            var bigger = Generate(_set.Total, _set.NumbersPerCard, want, _set.Seed, _set.Cards);
            _set.Cards = bigger.Cards;
            RebuildIndex();
            await PersistAsync();
            OnChange?.Invoke();
        }

        /// <summary>Xóa bộ thẻ hiện tại (kể cả đã phát hành) để tạo bộ mới. Chỉ gọi sau khi người dùng xác nhận.</summary>
        public async Task DiscardAsync()
        {
            _set = null;
            _awarded.Clear();
            RebuildIndex();
            _pendingSeed = NewSeed();
            await PersistAsync();
            OnChange?.Invoke();
        }

        /// <summary>Khóa bộ thẻ (cũng được gọi tự động khi in).</summary>
        public async Task LockAsync()
        {
            if (_set == null || _set.Issued) return;
            _set.Issued = true;
            _set.IssuedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
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
        /// <summary>Bộ sinh số ngẫu nhiên có seed, ổn định giữa các phiên bản .NET (SplitMix64).</summary>
        private sealed class SeededRandom
        {
            private ulong _state;
            public SeededRandom(long seed) => _state = (ulong)seed;

            private ulong NextU64()
            {
                ulong z = (_state += 0x9E3779B97F4A7C15UL);
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            /// <summary>Số nguyên trong [min, max).</summary>
            public int Next(int min, int max) => min + (int)(NextU64() % (ulong)(max - min));
        }

        /// <summary>Số dãy số khác nhau tối đa: C(total, k).</summary>
        public static double MaxUniqueCards(int total, int k)
        {
            if (k < 0 || k > total) return 0;
            k = Math.Min(k, total - k);
            double r = 1;
            for (int i = 1; i <= k; i++) r = r * (total - k + i) / i;
            return Math.Round(r);
        }

        /// <summary>
        /// Sinh <paramref name="count"/> thẻ KHÁC NHAU, mỗi thẻ <paramref name="k"/> số trong 1..total.
        /// Không có <paramref name="existing"/>: kết quả chỉ phụ thuộc seed (thẻ i luôn giống nhau dù count lớn hay nhỏ).
        /// Có <paramref name="existing"/>: giữ nguyên các thẻ đó và nối thêm thẻ mới (không trùng).
        /// </summary>
        public static CardSet Generate(int total, int k, int count, int seed, List<int[]>? existing = null)
        {
            var set = new CardSet { Total = total, NumbersPerCard = k, Seed = seed };
            count = (int)Math.Min(count, MaxUniqueCards(total, k));

            var seen = new HashSet<string>();
            if (existing != null)
                foreach (var c in existing) { set.Cards.Add(c); seen.Add(Key(c)); }

            var rng = new SeededRandom(existing == null || existing.Count == 0 ? seed : (long)seed * 1000003L + existing.Count);
            var pool = new int[total];
            long attempts = 0, maxAttempts = (long)count * 200 + 10000;

            while (set.Cards.Count < count && attempts++ < maxAttempts)
            {
                for (int i = 0; i < total; i++) pool[i] = i + 1;
                // Fisher–Yates một phần: chọn k số đầu
                for (int i = 0; i < k; i++)
                {
                    int j = rng.Next(i, total);
                    (pool[i], pool[j]) = (pool[j], pool[i]);
                }
                var card = pool.Take(k).OrderBy(n => n).ToArray();
                if (seen.Add(Key(card))) set.Cards.Add(card);
            }
            return set;
        }

        /// <summary>Khóa dãy số — cùng định dạng với khóa trao thưởng trong HistoryAndInput ("01-05-12").</summary>
        public static string Key(IEnumerable<int> numbers) => string.Join("-", numbers.OrderBy(n => n));

        // ---------- Tra cứu thẻ đã phát hành ----------
        private int Digits => Math.Max(3, Count.ToString().Length);
        private int PerPerson => _set == null ? 1 : Math.Max(1, _set.PerPerson);

        private string CodeOf(int i) => "#" + (i + 1).ToString("D" + Digits);

        private string WhoOf(int i)
        {
            int per = PerPerson;
            int person = i / per + 1;
            int nth = i % per + 1;
            return per > 1
                ? $"Người chơi {person.ToString("D" + Digits)} · Thẻ {nth}/{per}"
                : $"Người chơi {person.ToString("D" + Digits)}";
        }

        /// <summary>Dãy số này có phải một thẻ đã phát hành không?</summary>
        public bool TryFind(string key, out CardView? card)
        {
            card = null;
            if (_set == null || !_index.TryGetValue(key, out int i)) return false;
            card = new CardView(i, CodeOf(i), WhoOf(i), _set.Cards[i]);
            return true;
        }

        /// <summary>Lấy thẻ theo mã (1-based), ví dụ "#42" → thẻ số 42.</summary>
        public bool TryGetByCode(int code, out CardView? card)
        {
            card = null;
            if (_set == null || code < 1 || code > _set.Cards.Count) return false;
            int i = code - 1;
            card = new CardView(i, CodeOf(i), WhoOf(i), _set.Cards[i]);
            return true;
        }

        /// <summary>
        /// Xem trước các thẻ đầu tiên theo thiết lập đang chỉnh (draft).
        /// Đã phát hành → hiện đúng thẻ đã phát hành; chưa → hiện đúng thẻ SẼ được tạo khi bấm Lưu.
        /// </summary>
        public List<CardView> Preview(AppSettings d, int take)
        {
            var result = new List<CardView>();
            int want = (int)Math.Min(d.CardsIssued, MaxUniqueCards(d.TotalNumbers, d.NumbersPerCard));
            if (want <= 0 || take <= 0) return result;

            List<int[]> cards;
            int per;
            if (_set != null && _set.Issued)
            {
                cards = _set.Cards.Take(take).ToList();
                per = PerPerson;
            }
            else
            {
                cards = Generate(d.TotalNumbers, d.NumbersPerCard, Math.Min(take, want), _pendingSeed).Cards;
                per = Math.Max(1, d.CardsPerPerson);
            }

            int digits = Math.Max(3, want.ToString().Length);
            for (int i = 0; i < cards.Count; i++)
            {
                int person = i / per + 1, nth = i % per + 1;
                string who = per > 1
                    ? $"Người chơi {person.ToString("D" + digits)} · Thẻ {nth}/{per}"
                    : $"Người chơi {person.ToString("D" + digits)}";
                result.Add(new CardView(i, "#" + (i + 1).ToString("D" + digits), who, cards[i]));
            }
            return result;
        }

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
        public CardStats GetStats(IEnumerable<int> drawn)
        {
            if (_set == null) return default;

            var d = new HashSet<int>(drawn);
            int awarded = 0, win = 0, m1 = 0, m2 = 0;

            foreach (var card in _set.Cards)
            {
                if (_awarded.Count > 0 && _awarded.Contains(Key(card))) { awarded++; continue; }

                int missing = 0;
                foreach (var n in card)
                    if (!d.Contains(n)) missing++;

                if (missing == 0) win++;
                else if (missing == 1) m1++;
                else if (missing == 2) m2++;
            }
            return new CardStats(_set.Cards.Count, awarded, win, m1, m2);
        }

        // ---------- Sao lưu / khôi phục bộ thẻ ----------
        public string? ExportJson() => _set == null ? null : JsonSerializer.Serialize(_set);

        /// <summary>Khôi phục bộ thẻ từ file sao lưu. Trả về thông báo lỗi, hoặc null nếu thành công.</summary>
        public async Task<string?> ImportAsync(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "File thẻ trống.";
            CardSet? set;
            try { set = JsonSerializer.Deserialize<CardSet>(json); }
            catch { return "File thẻ không hợp lệ."; }

            if (set == null || set.Cards.Count == 0) return "File thẻ không có thẻ nào.";
            if (set.Total < 20 || set.Total > 99 || set.NumbersPerCard < 1 || set.NumbersPerCard > Math.Min(15, set.Total))
                return "Thông số trong file thẻ không hợp lệ.";

            var seen = new HashSet<string>();
            for (int i = 0; i < set.Cards.Count; i++)
            {
                var c = set.Cards[i];
                if (c == null || c.Length != set.NumbersPerCard || c.Distinct().Count() != c.Length
                    || c.Any(n => n < 1 || n > set.Total))
                    return $"Thẻ thứ {i + 1} trong file không hợp lệ.";
                set.Cards[i] = c.OrderBy(n => n).ToArray();
                if (!seen.Add(Key(set.Cards[i]))) return $"Thẻ thứ {i + 1} bị trùng với thẻ khác.";
            }

            if (set.PerPerson < 1 || set.Cards.Count % set.PerPerson != 0) set.PerPerson = 1;
            set.Issued = true;
            set.IssuedAt ??= DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            _set = set;
            _awarded.Clear();
            RebuildIndex();
            if (set.Seed > 0) _pendingSeed = set.Seed;
            await PersistAsync();

            // Đồng bộ thiết lập cho khớp bộ thẻ vừa khôi phục
            var s = SettingsService.Clone(_settings.Current);
            s.TotalNumbers = set.Total;
            s.NumbersPerCard = set.NumbersPerCard;
            s.CardsPerPerson = set.PerPerson;
            s.Participants = set.Cards.Count / set.PerPerson;
            s.MinWinCount = Math.Min(s.MinWinCount, set.NumbersPerCard);
            s.MaxWinCount = Math.Max(s.MaxWinCount, set.NumbersPerCard);
            await _settings.SaveAsync(s);
            OnChange?.Invoke();
            return null;
        }

        // ---------- In thẻ: 8 thẻ / tờ A4 ----------
        /// <summary>In thẻ (có thể chỉ in 1 khoảng thẻ để in bù). In xong tự khóa bộ thẻ.</summary>
        public async Task<bool> PrintAsync(AppSettings s, int from = 1, int to = int.MaxValue)
        {
            var html = BuildPrintHtml(s, from, to);
            if (string.IsNullOrEmpty(html)) return false;
            await _js.InvokeVoidAsync("SettingsHelper.printHtml", html);
            await LockAsync();
            return true;
        }

        public string BuildPrintHtml(AppSettings s, int from = 1, int to = int.MaxValue)
        {
            if (_set == null) return "";

            from = Math.Max(1, from);
            to = Math.Min(to, _set.Cards.Count);
            if (from > to) return "";

            string ball = s.BallColor;
            string ballText = AppSettings.ContrastText(ball);
            string title = WebUtility.HtmlEncode(s.Title);
            // Nhiều số / thẻ → thu nhỏ bóng để vẫn vừa 8 thẻ trên 1 tờ A4
            bool big = _set.NumbersPerCard <= 10;
            string ballSize = big ? "14mm" : "11mm";
            string ballFont = big ? "7mm" : "5.5mm";

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
              .Append(".b{width:").Append(ballSize).Append(";height:").Append(ballSize).Append(";border-radius:50%;display:flex;align-items:center;justify-content:center;")
              .Append("font-weight:800;font-size:").Append(ballFont).Append(";color:").Append(ballText).Append(";background-color:").Append(ball).Append(";")
              .Append("background-image:radial-gradient(circle at 30% 30%,rgba(255,255,255,.45),rgba(255,255,255,0) 60%);")
              .Append("box-shadow:inset 0 -1mm 2mm rgba(0,0,0,.18)}")
              .Append(".ft{font-size:3mm;color:#666;text-align:center}")
              .Append("</style></head><body>");

            for (int start = from - 1; start < to; start += CardsPerSheet)
            {
                sb.Append("<div class=\"sheet\">");
                for (int i = start; i < start + CardsPerSheet; i++)
                {
                    if (i >= to) { sb.Append("<div></div>"); continue; } // giữ nguyên lưới ở tờ cuối

                    sb.Append("<div class=\"card\"><div class=\"hd\"><span class=\"t\">").Append(title)
                      .Append("</span><span class=\"code\">").Append(CodeOf(i)).Append("</span></div>")
                      .Append("<div class=\"who\">").Append(WhoOf(i)).Append("</div><div class=\"nums\">");
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
