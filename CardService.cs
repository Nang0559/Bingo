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
    /// Mỗi thẻ là bảng 5x5 chuẩn: cột B-I-N-G-O lấy số theo từng dải (75 số: B 1-15, I 16-30, N 31-45, G 46-60, O 61-75),
    /// ô giữa là FREE. Thẻ trúng khi đủ MỘT hàng, MỘT cột hoặc MỘT đường chéo (ô FREE tính là đã có).
    /// Bộ thẻ sinh theo SEED (cùng seed + thông số = cùng bộ thẻ). Sau khi in/khóa (Issued)
    /// bộ thẻ không bao giờ bị tạo lại; tăng số người chỉ NỐI THÊM thẻ phía sau.
    /// </summary>
    public class CardService
    {
        public const int MaxParticipants = 5000;
        public const int CardsPerSheet = 8; // 2 cột x 4 hàng trên 1 tờ A4

        public const int Size = 5;          // bảng 5x5
        public const int MinTotalForCards = 25; // mỗi cột cần >= 5 số
        public const string Letters = "BINGO";
        public const int FreeCell = 12;     // ô giữa (hàng 3, cột N)

        // v2 = thẻ 5x5. (Bộ thẻ v1 là thẻ "dãy số" kiểu cũ → không dùng lại.)
        private const string StorageKey = "bingo.cards.v2";

        private readonly IJSRuntime _js;
        private readonly SettingsService _settings;

        private CardSet? _set;
        private readonly HashSet<string> _awarded = new();
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
        public int SetPerPerson => _set?.PerPerson ?? 1;

        public class CardSet
        {
            public int Total { get; set; }

            /// <summary>Mỗi thẻ = 25 số theo thứ tự hàng (trái→phải, trên→dưới); 0 = ô FREE ở giữa.</summary>
            public List<int[]> Cards { get; set; } = new();

            /// <summary>Seed sinh thẻ (0 = bộ thẻ cũ tạo trước khi có seed).</summary>
            public int Seed { get; set; }

            /// <summary>Số thẻ phát cho mỗi người tại thời điểm tạo bộ thẻ.</summary>
            public int PerPerson { get; set; } = 1;

            /// <summary>true = đã in/khóa: không bao giờ tạo lại hay xóa thẻ, chỉ được nối thêm.</summary>
            public bool Issued { get; set; }

            public string? IssuedAt { get; set; }

            /// <summary>2 = thẻ 5x5.</summary>
            public int Format { get; set; } = 2;
        }

        /// <summary>Một đường thắng (hàng/cột/chéo) và các số (không tính FREE) trên đường đó.</summary>
        public record WinLine(string Name, int[] Numbers);

        /// <summary>Một thẻ để hiển thị (xem trước / tra cứu). Numbers = 25 ô theo hàng, 0 = FREE.</summary>
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

            if (_set != null && (_set.Cards.Count == 0 || _set.Format != 2 || _set.Cards.Any(c => c == null || c.Length != Size * Size)))
                _set = null;
            if (_set != null)
            {
                if (_set.PerPerson < 1) _set.PerPerson = 1;
                // Bộ thẻ tạo bởi phiên bản cũ (chưa có seed): coi như ĐÃ PHÁT HÀNH để không bị tạo lại.
                if (_set.Seed == 0) _set.Issued = true;
                else _pendingSeed = _set.Seed;
            }
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

        /// <summary>
        /// Đồng bộ bộ thẻ với thiết lập.
        /// - Đã phát hành: KHÔNG tạo lại / xóa thẻ; nếu tăng số người (cùng thông số) thì nối thêm thẻ.
        /// - Chưa phát hành: tạo lại theo seed khi thông số đổi.
        /// </summary>
        public async Task SyncAsync()
        {
            var s = _settings.Current;
            int want = WantedCount(s);

            if (_set != null && _set.Issued)
            {
                bool sameParams = _set.Total == s.TotalNumbers
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
                            await PersistAsync();
                }
                OnChange?.Invoke();
                return;
            }

            bool same = _set != null
                        && _set.Total == s.TotalNumbers
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
            int want = WantedCount(s);
            if (want <= 0) _set = null;
            else
            {
                _set = Generate(s.TotalNumbers, want, _pendingSeed);
                _set.PerPerson = s.CardsPerPerson;
                if (_set.Cards.Count == 0) _set = null;
            }
            await PersistAsync();
            OnChange?.Invoke();
        }

        /// <summary>Nối thêm thẻ vào bộ đã phát hành; các thẻ cũ giữ nguyên 100%.</summary>
        private async Task ExtendAsync(int want)
        {
            if (_set == null) return;
            var bigger = Generate(_set.Total, want, _set.Seed, _set.Cards);
            _set.Cards = bigger.Cards;
            await PersistAsync();
            OnChange?.Invoke();
        }

        /// <summary>Xóa bộ thẻ hiện tại (kể cả đã phát hành) để tạo bộ mới. Chỉ gọi sau khi người dùng xác nhận.</summary>
        public async Task DiscardAsync()
        {
            _set = null;
            _awarded.Clear();
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

        /// <summary>Số thẻ cần phát hành theo thiết lập (0 nếu chưa khai báo hoặc tổng số bóng quá nhỏ cho thẻ 5x5).</summary>
        public static int WantedCount(AppSettings s)
        {
            if (s.CardsIssued <= 0 || s.TotalNumbers < MinTotalForCards) return 0;
            return (int)Math.Min(s.CardsIssued, MaxUniqueCards(s.TotalNumbers));
        }

        /// <summary>Dải số của từng cột B-I-N-G-O: chia đều 1..total thành 5 dải liên tiếp (75 → 15 số / cột).</summary>
        public static (int Lo, int Hi)[] ColumnRanges(int total)
        {
            var r = new (int, int)[Size];
            int baseSize = total / Size, extra = total % Size, lo = 1;
            for (int c = 0; c < Size; c++)
            {
                int n = baseSize + (c < extra ? 1 : 0);
                r[c] = (lo, lo + n - 1);
                lo += n;
            }
            return r;
        }

        /// <summary>Mô tả dải số, ví dụ "B 1–15 · I 16–30 · N 31–45 · G 46–60 · O 61–75".</summary>
        public static string RangeText(int total)
        {
            if (total < MinTotalForCards) return "";
            var r = ColumnRanges(total);
            return string.Join(" · ", Enumerable.Range(0, Size).Select(c => $"{Letters[c]} {r[c].Lo}–{r[c].Hi}"));
        }

        /// <summary>Số thẻ khác nhau tối đa (mỗi cột: chỉnh hợp P(n, 5), cột N: P(n, 4)).</summary>
        public static double MaxUniqueCards(int total)
        {
            if (total < MinTotalForCards) return 0;
            var ranges = ColumnRanges(total);
            double r = 1;
            for (int c = 0; c < Size; c++)
            {
                int n = ranges[c].Hi - ranges[c].Lo + 1;
                int k = c == 2 ? Size - 1 : Size;
                for (int i = 0; i < k; i++) r *= (n - i);
            }
            return r;
        }

        /// <summary>
        /// Sinh <paramref name="count"/> thẻ 5x5 KHÁC NHAU.
        /// Không có <paramref name="existing"/>: kết quả chỉ phụ thuộc seed (thẻ i luôn giống nhau dù count lớn hay nhỏ).
        /// Có <paramref name="existing"/>: giữ nguyên các thẻ đó và nối thêm thẻ mới (không trùng).
        /// </summary>
        public static CardSet Generate(int total, int count, int seed, List<int[]>? existing = null)
        {
            var set = new CardSet { Total = total, Seed = seed };
            if (total < MinTotalForCards) return set;
            count = (int)Math.Min(count, MaxUniqueCards(total));

            var seen = new HashSet<string>();
            if (existing != null)
                foreach (var c in existing) { set.Cards.Add(c); seen.Add(Key(c)); }

            var rng = new SeededRandom(existing == null || existing.Count == 0 ? seed : (long)seed * 1000003L + existing.Count);
            var ranges = ColumnRanges(total);
            var pool = new int[total];
            long attempts = 0, maxAttempts = (long)count * 50 + 10000;

            while (set.Cards.Count < count && attempts++ < maxAttempts)
            {
                var grid = new int[Size * Size];
                for (int col = 0; col < Size; col++)
                {
                    int n = ranges[col].Hi - ranges[col].Lo + 1;
                    for (int i = 0; i < n; i++) pool[i] = ranges[col].Lo + i;

                    int k = col == 2 ? Size - 1 : Size; // cột N chỉ 4 số (ô giữa là FREE)
                    for (int i = 0; i < k; i++)          // Fisher–Yates một phần
                    {
                        int j = rng.Next(i, n);
                        (pool[i], pool[j]) = (pool[j], pool[i]);
                    }

                    int idx = 0;
                    for (int row = 0; row < Size; row++)
                    {
                        if (row * Size + col == FreeCell) { grid[FreeCell] = 0; continue; }
                        grid[row * Size + col] = pool[idx++];
                    }
                }
                if (seen.Add(Key(grid))) set.Cards.Add(grid);
            }
            return set;
        }

        /// <summary>Khóa nhận dạng 1 thẻ (toàn bộ 25 ô) — chỉ dùng để chống trùng khi sinh thẻ.</summary>
        public static string Key(IEnumerable<int> grid) => string.Join("-", grid);

        /// <summary>Khóa trao thưởng của 1 thẻ (theo mã thẻ, vì nhiều thẻ có thể cùng chung 1 hàng số).</summary>
        public static string CardKey(int index) => $"card-{index + 1}";

        // 12 đường thắng: 5 hàng, 5 cột, 2 đường chéo (chỉ số ô trong lưới 25)
        private static readonly int[][] Lines = BuildLines();
        private static readonly string[] LineNames = BuildLineNames();

        private static int[][] BuildLines()
        {
            var l = new List<int[]>();
            for (int r = 0; r < Size; r++) l.Add(Enumerable.Range(0, Size).Select(c => r * Size + c).ToArray());
            for (int c = 0; c < Size; c++) l.Add(Enumerable.Range(0, Size).Select(r => r * Size + c).ToArray());
            l.Add(Enumerable.Range(0, Size).Select(i => i * Size + i).ToArray());
            l.Add(Enumerable.Range(0, Size).Select(i => i * Size + (Size - 1 - i)).ToArray());
            return l.ToArray();
        }

        private static string[] BuildLineNames()
        {
            var n = new List<string>();
            for (int r = 0; r < Size; r++) n.Add($"Hàng {r + 1}");
            for (int c = 0; c < Size; c++) n.Add($"Cột {Letters[c]}");
            n.Add("Chéo \\");
            n.Add("Chéo /");
            return n.ToArray();
        }

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

        /// <summary>Lấy thẻ theo mã (1-based), ví dụ "#42" → thẻ số 42.</summary>
        public bool TryGetByCode(int code, out CardView? card)
        {
            card = null;
            if (_set == null || code < 1 || code > _set.Cards.Count) return false;
            int i = code - 1;
            card = new CardView(i, CodeOf(i), WhoOf(i), _set.Cards[i]);
            return true;
        }

        private static bool[] ToMarks(IEnumerable<int> drawn)
        {
            var m = new bool[128];
            foreach (var n in drawn) if (n > 0 && n < m.Length) m[n] = true;
            return m;
        }

        /// <summary>Các hàng / cột / đường chéo của thẻ đã đủ số (ô FREE tính là đã có).</summary>
        public List<WinLine> GetWinningLines(int index, IEnumerable<int> drawn)
        {
            var result = new List<WinLine>();
            if (_set == null || index < 0 || index >= _set.Cards.Count) return result;

            var marks = ToMarks(drawn);
            var card = _set.Cards[index];
            for (int l = 0; l < Lines.Length; l++)
            {
                if (Lines[l].All(cell => cell == FreeCell || marks[card[cell]]))
                    result.Add(new WinLine(LineNames[l], Lines[l].Where(c => c != FreeCell).Select(c => card[c]).ToArray()));
            }
            return result;
        }

        /// <summary>Số ô còn thiếu ít nhất để Bingo của thẻ (0 = đã Bingo), kèm các số còn thiếu của đường gần nhất.</summary>
        public (int Missing, int[] Numbers) BestLine(int index, IEnumerable<int> drawn)
        {
            if (_set == null || index < 0 || index >= _set.Cards.Count) return (Size, Array.Empty<int>());
            var marks = ToMarks(drawn);
            var card = _set.Cards[index];
            int best = int.MaxValue; int[] bestNums = Array.Empty<int>();
            foreach (var line in Lines)
            {
                var miss = line.Where(c => c != FreeCell && !marks[card[c]]).Select(c => card[c]).ToArray();
                if (miss.Length < best) { best = miss.Length; bestNums = miss; }
            }
            return (best, bestNums);
        }

        /// <summary>Tìm các thẻ có một hàng/cột/chéo gồm đúng các số này và đã đủ số (để tra thẻ khi chỉ biết dãy số).</summary>
        public List<(CardView Card, WinLine Line)> FindByLine(IEnumerable<int> numbers, IEnumerable<int> drawn)
        {
            var result = new List<(CardView, WinLine)>();
            if (_set == null) return result;

            var want = numbers.OrderBy(n => n).ToArray();
            for (int i = 0; i < _set.Cards.Count; i++)
            {
                foreach (var line in GetWinningLines(i, drawn))
                {
                    if (line.Numbers.Length == want.Length && line.Numbers.OrderBy(n => n).SequenceEqual(want))
                    {
                        result.Add((new CardView(i, CodeOf(i), WhoOf(i), _set.Cards[i]), line));
                        break;
                    }
                }
            }
            return result;
        }

        public bool IsAwarded(int index) => _awarded.Contains(CardKey(index));

        /// <summary>
        /// Xem trước các thẻ đầu tiên theo thiết lập đang chỉnh (draft).
        /// Đã phát hành → hiện đúng thẻ đã phát hành; chưa → hiện đúng thẻ SẼ được tạo khi bấm Lưu.
        /// </summary>
        public List<CardView> Preview(AppSettings d, int take)
        {
            var result = new List<CardView>();
            int want = WantedCount(d);
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
                cards = Generate(d.TotalNumbers, Math.Min(take, want), _pendingSeed).Cards;
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
        /// <summary>
        /// Winning = thẻ đã đủ ít nhất 1 hàng/cột/chéo (Bingo, chưa nhận thưởng).
        /// Missing1 / Missing2 = thẻ chưa Bingo nhưng đường gần nhất chỉ còn thiếu 1 / 2 số.
        /// Thẻ đã trao thưởng chỉ đếm vào Awarded.
        /// </summary>
        public CardStats GetStats(IEnumerable<int> drawn)
        {
            if (_set == null) return default;

            var marks = ToMarks(drawn);
            int awarded = 0, win = 0, m1 = 0, m2 = 0;

            for (int i = 0; i < _set.Cards.Count; i++)
            {
                if (_awarded.Count > 0 && _awarded.Contains(CardKey(i))) { awarded++; continue; }

                var card = _set.Cards[i];
                int best = Size;
                foreach (var line in Lines)
                {
                    int miss = 0;
                    foreach (var cell in line)
                        if (cell != FreeCell && !marks[card[cell]] && ++miss >= best) break;
                    if (miss < best) best = miss;
                    if (best == 0) break;
                }

                if (best == 0) win++;
                else if (best == 1) m1++;
                else if (best == 2) m2++;
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
            if (set.Format != 2) return "File thẻ thuộc định dạng cũ (không phải thẻ Bingo 5×5).";
            if (set.Total < MinTotalForCards || set.Total > 99) return "Thông số trong file thẻ không hợp lệ.";

            var ranges = ColumnRanges(set.Total);
            var seen = new HashSet<string>();
            for (int i = 0; i < set.Cards.Count; i++)
            {
                var c = set.Cards[i];
                bool ok = c != null && c.Length == Size * Size && c[FreeCell] == 0;
                if (ok)
                {
                    for (int cell = 0; cell < c!.Length && ok; cell++)
                    {
                        if (cell == FreeCell) continue;
                        var (lo, hi) = ranges[cell % Size];
                        ok = c[cell] >= lo && c[cell] <= hi;
                    }
                    ok = ok && c.Where(n => n != 0).Distinct().Count() == Size * Size - 1;
                }
                if (!ok) return $"Thẻ thứ {i + 1} trong file không hợp lệ.";
                if (!seen.Add(Key(c!))) return $"Thẻ thứ {i + 1} bị trùng với thẻ khác.";
            }

            if (set.PerPerson < 1 || set.Cards.Count % set.PerPerson != 0) set.PerPerson = 1;
            set.Issued = true;
            set.IssuedAt ??= DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            _set = set;
            _awarded.Clear();
            if (set.Seed > 0) _pendingSeed = set.Seed;
            await PersistAsync();

            // Đồng bộ thiết lập cho khớp bộ thẻ vừa khôi phục
            var s = SettingsService.Clone(_settings.Current);
            s.TotalNumbers = set.Total;
            s.CardsPerPerson = set.PerPerson;
            s.Participants = set.Cards.Count / set.PerPerson;
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

            string ball = AppSettings.NormalizeColor(s.BallColor);
            string ballText = AppSettings.ContrastText(ball);
            // Số trên thẻ: dùng màu bóng; nếu màu quá sáng (khó đọc trên nền trắng) thì dùng chữ đen
            string numColor = ballText == "#ffffff" ? ball : "#222222";
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
              .Append(".card{border:0.3mm dashed #888;border-radius:2.5mm;padding:2mm 3mm;display:flex;flex-direction:column;gap:1mm;overflow:hidden;min-height:0}")
              .Append(".hd{display:flex;justify-content:space-between;align-items:baseline;line-height:1}")
              .Append(".t{font-size:4.2mm;font-weight:800;letter-spacing:.4mm;color:").Append(numColor).Append("}")
              .Append(".code{font-size:3mm;font-weight:700;color:#444;text-align:right}")
              .Append(".bd{flex:1;min-height:0;display:grid;grid-template-columns:repeat(5,1fr);grid-template-rows:1.15fr repeat(5,1fr);")
              .Append("border:0.4mm solid ").Append(ball).Append(";border-radius:1.2mm;overflow:hidden}")
              .Append(".bd div{display:flex;align-items:center;justify-content:center;border-right:0.25mm solid ").Append(ball)
              .Append(";border-bottom:0.25mm solid ").Append(ball).Append(";min-width:0;min-height:0}")
              .Append(".bd div:nth-child(5n){border-right:0}")
              .Append(".bd div:nth-last-child(-n+5){border-bottom:0}")
              .Append(".bd .l{background:").Append(ball).Append(";color:").Append(ballText).Append(";font-weight:900;font-size:5.2mm;border-color:rgba(255,255,255,.35)}")
              .Append(".bd .n{font-weight:800;font-size:5mm;color:").Append(numColor).Append("}")
              .Append(".bd .f{background:").Append(ball).Append(";color:").Append(ballText).Append(";font-weight:900;font-size:3.4mm;letter-spacing:.2mm}")
              .Append(".ft{font-size:2.4mm;color:#777;text-align:center;line-height:1}")
              .Append("</style></head><body>");

            for (int start = from - 1; start < to; start += CardsPerSheet)
            {
                sb.Append("<div class=\"sheet\">");
                for (int i = start; i < start + CardsPerSheet; i++)
                {
                    if (i >= to) { sb.Append("<div></div>"); continue; } // giữ nguyên lưới ở tờ cuối

                    sb.Append("<div class=\"card\"><div class=\"hd\"><span class=\"t\">").Append(title)
                      .Append("</span><span class=\"code\"><b>").Append(CodeOf(i)).Append("</b> · ").Append(WebUtility.HtmlEncode(WhoOf(i)))
                      .Append("</span></div><div class=\"bd\">");
                    foreach (var ch in Letters) sb.Append("<div class=\"l\">").Append(ch).Append("</div>");
                    var grid = _set.Cards[i];
                    for (int cell = 0; cell < grid.Length; cell++)
                    {
                        if (cell == FreeCell) sb.Append("<div class=\"f\">FREE</div>");
                        else sb.Append("<div class=\"n\">").Append(grid[cell].ToString("00")).Append("</div>");
                    }
                    sb.Append("</div><div class=\"ft\">Trúng khi đủ 1 hàng, 1 cột hoặc 1 đường chéo (ô FREE tính là đã có)</div></div>");
                }
                sb.Append("</div>");
            }

            sb.Append("</body></html>");
            return sb.ToString();
        }
    }
}
