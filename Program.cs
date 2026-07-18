// Claude Usage Widget for Windows
// Single-file WinForms app. Reads the Claude Code OAuth token from
// %USERPROFILE%\.claude\.credentials.json (read-only, never written) and polls
// https://api.anthropic.com/api/oauth/usage to show plan limits on the desktop.
// The token is never logged and never sent to any host other than api.anthropic.com.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Claude Usage Widget")]
[assembly: AssemblyProduct("Claude Usage Widget")]
[assembly: AssemblyDescription("แสดงโควต้า Claude plan บนเดสก์ท็อป")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace ClaudeUsageWidget
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Length > 0 && args[0] == "--selftest")
            {
                SelfTest();
                return;
            }
            if (args.Length > 0 && args[0] == "--toasttest")
            {
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Toast.Pop("SESSION ใช้ไป 81% แล้ว", "รีเซ็ต 17:19 (อีก 3h 43m)", Color.FromArgb(230, 178, 86));
                Toast.Pop("weekly ยังเหลืออีก 89%", "จะรีเซ็ตใน 20h — ใช้ให้คุ้มนะ", Color.FromArgb(92, 190, 140));
                var quit = new System.Windows.Forms.Timer { Interval = 5000 };
                quit.Tick += delegate { Application.Exit(); };
                quit.Start();
                Application.Run();
                return;
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WidgetForm(args.Length > 0 && args[0] == "--mock"));
        }

        static void SelfTest()
        {
            var creds = Credentials.Load();
            Console.WriteLine("credentials: " + (creds != null ? "OK (plan=" + creds.SubscriptionType + ", expires " + creds.ExpiresAt.ToLocalTime() + ")" : "NOT FOUND"));
            if (creds == null) return;
            try
            {
                var usage = UsageClient.Fetch(creds.AccessToken);
                foreach (var l in usage.Limits)
                    Console.WriteLine(string.Format("{0,-14} {1,3}%  severity={2}  resets {3:HH:mm dd MMM}  active={4}",
                        l.Label, l.Percent, l.Severity, l.ResetsAt.ToLocalTime(), l.IsActive));
            }
            catch (Exception ex) { Console.WriteLine("FETCH FAILED: " + ex.Message); }
        }
    }

    class Credentials
    {
        public string AccessToken;
        public string SubscriptionType;
        public string Account = "";
        public DateTimeOffset ExpiresAt;

        public static string Path
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"); }
        }

        public static Credentials Load() { return LoadFrom(Path); }

        public static Credentials LoadFrom(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
                var oauth = root["claudeAiOauth"] as Dictionary<string, object>;
                if (oauth == null) return null;
                var c = new Credentials();
                c.AccessToken = (string)oauth["accessToken"];
                c.SubscriptionType = oauth.ContainsKey("subscriptionType") ? Convert.ToString(oauth["subscriptionType"]) : "";
                c.ExpiresAt = oauth.ContainsKey("expiresAt")
                    ? DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(oauth["expiresAt"]))
                    : DateTimeOffset.MaxValue;
                c.Account = AccountFor(path);
                return c;
            }
            catch { return null; }
        }

        // The logged-in email isn't in .credentials.json — it's in the sibling
        // .claude.json (oauthAccount.emailAddress). Regex-scan it (the file has
        // duplicate keys that break a strict JSON parse). CLAUDE_CONFIG_DIR
        // accounts keep their own .claude.json next to their credentials copy.
        static readonly Regex RxEmail = new Regex("\"emailAddress\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

        static string AccountFor(string credPath)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(credPath);          // ...\.claude
                var home = System.IO.Path.GetDirectoryName(dir);              // ...\<user>
                foreach (var cfg in new[] {
                    System.IO.Path.Combine(dir, ".claude.json"),
                    System.IO.Path.Combine(home, ".claude.json") })
                    if (File.Exists(cfg))
                    {
                        var m = RxEmail.Match(File.ReadAllText(cfg));
                        if (m.Success) return m.Groups[1].Value;
                    }
            }
            catch { }
            return "";
        }
    }

    class LimitInfo
    {
        public string Kind;
        public string Label;
        public int Percent;
        public string Severity;
        public DateTimeOffset ResetsAt;
        public bool IsActive;
    }

    class UsageSnapshot
    {
        public List<LimitInfo> Limits = new List<LimitInfo>();
        public DateTime FetchedAt;
    }

    class RateLimitedException : Exception { }
    class UnauthorizedException : Exception { }

    static class UsageClient
    {
        // Read at most `cap` bytes from a stream, then stop. Guards against an
        // oversized/hostile HTTP body OOM-ing the process before JSON parsing.
        static string ReadCapped(System.IO.Stream s, int cap)
        {
            var buf = new byte[8192];
            var ms = new System.IO.MemoryStream();
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                if (ms.Length + n > cap)
                    throw new WebException("usage response exceeds size cap");
                ms.Write(buf, 0, n);
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        // Only the generic limits[] array is parsed; the rest of the (undocumented)
        // response schema rotates and must not be relied on.
        public static UsageSnapshot Fetch(string token)
        {
            var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
            req.Method = "GET";
            req.Timeout = 20000;
            // Never follow a redirect: HttpWebRequest re-sends custom headers (incl. the
            // Bearer token) to the redirect target, and this endpoint is undocumented —
            // a 3xx to another host would leak the token. Treat any 3xx as an error.
            req.AllowAutoRedirect = false;
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            req.UserAgent = "claude-usage-widget-win/1.0";
            string json;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400)
                        throw new WebException("unexpected redirect from usage endpoint");
                    // Cap the body so a hostile/oversized response can't exhaust memory.
                    // The real payload is a few KB; 1 MB is generous headroom.
                    json = ReadCapped(resp.GetResponseStream(), 1024 * 1024);
                }
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http != null && (int)http.StatusCode == 429) throw new RateLimitedException();
                if (http != null && ((int)http.StatusCode == 401 || (int)http.StatusCode == 403)) throw new UnauthorizedException();
                throw;
            }

            var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var snap = new UsageSnapshot { FetchedAt = DateTime.Now };
            var limits = root.ContainsKey("limits") ? root["limits"] as object[] : null;
            if (limits != null)
            {
                foreach (var o in limits)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    try
                    {
                        var l = new LimitInfo();
                        l.Kind = Convert.ToString(d["kind"]);
                        l.Percent = Convert.ToInt32(d["percent"], CultureInfo.InvariantCulture);
                        l.Severity = d.ContainsKey("severity") ? Convert.ToString(d["severity"]) : "normal";
                        l.ResetsAt = DateTimeOffset.Parse(Convert.ToString(d["resets_at"]), CultureInfo.InvariantCulture);
                        l.IsActive = d.ContainsKey("is_active") && d["is_active"] is bool && (bool)d["is_active"];
                        l.Label = LabelFor(l.Kind, d);
                        snap.Limits.Add(l);
                    }
                    catch { /* skip malformed entries; schema is unstable by design */ }
                }
            }
            return snap;
        }

        static string LabelFor(string kind, Dictionary<string, object> d)
        {
            if (kind == "session") return "SESSION";
            if (kind == "weekly_all") return "WEEKLY";
            var scope = d.ContainsKey("scope") ? d["scope"] as Dictionary<string, object> : null;
            var model = scope != null && scope.ContainsKey("model") ? scope["model"] as Dictionary<string, object> : null;
            var name = model != null && model.ContainsKey("display_name") ? Convert.ToString(model["display_name"]) : null;
            return string.IsNullOrEmpty(name) ? kind.ToUpperInvariant() : name.ToUpperInvariant();
        }
    }

    class LocalStats
    {
        public long InTok, OutTok, CacheTok;
        public int Sessions;
        public string Skills = "";
        public string Projects = "";
        public string Models = "";
        public double Cost;
        public double FableCost;   // today's Fable-only $ (the metered model from Jul 20)
    }

    class ActivityStats
    {
        public Dictionary<DateTime, long> PerDay = new Dictionary<DateTime, long>();
        public long MaxDay;
        public string Top7d = "";
        public string Top1d = "";
        public string Models7d = "";
        public double Cost7;
        public int PctBig = -1;   // % of 7d usage from >150k-context requests
        public int PctSide = -1;  // % of 7d usage from subagent (sidechain) turns
        public string SkillsLine = "";
    }

    // Scans today's Claude Code transcripts (~\.claude\projects\**\*.jsonl) for
    // local-only drill-down stats: tokens, skills invoked, busiest projects.
    // Read-only; nothing leaves the machine.
    static class LocalScanner
    {
        static readonly Regex RxIn = new Regex("\"input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxOut = new Regex("\"output_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxSkill = new Regex("\"skill\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex RxCache = new Regex("\"cache_read_input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxCacheW = new Regex("\"cache_creation_input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        static readonly Regex RxModel = new Regex("\"model\"\\s*:\\s*\"(claude-[^\"]+)\"", RegexOptions.Compiled);

        class ModelUse { public long In, Out, CacheR, CacheW; public long Total { get { return In + Out; } } }

        // API pricing per 1M tokens (input, output); cache read = 0.1× in, cache write = 1.25× in
        static void PriceOf(string model, out double pIn, out double pOut)
        {
            if (model.Contains("fable") || model.Contains("mythos")) { pIn = 10; pOut = 50; }
            else if (model.Contains("opus")) { pIn = 5; pOut = 25; }
            else if (model.Contains("haiku")) { pIn = 1; pOut = 5; }
            else { pIn = 3; pOut = 15; } // sonnet and default
        }

        static string ShortModel(string model)
        {
            if (model.Contains("fable")) return "Fable";
            if (model.Contains("mythos")) return "Mythos";
            if (model.Contains("opus")) return "Opus";
            if (model.Contains("sonnet")) return "Sonnet";
            if (model.Contains("haiku")) return "Haiku";
            return model.Length > 12 ? model.Substring(0, 12) : model;
        }

        static long Num(Regex rx, string line)
        {
            var m = rx.Match(line);
            long v = 0;
            if (m.Success) long.TryParse(m.Groups[1].Value, out v);
            return v;
        }

        // Per-line attribution: each assistant entry in the JSONL carries its own
        // model + usage, so tokens and cost are counted against the real model.
        static void ScanLine(string line, Dictionary<string, ModelUse> models, ref double cost)
        {
            long a, b, c, d;
            ScanLine(line, models, ref cost, out a, out b, out c, out d);
        }

        static void ScanLine(string line, Dictionary<string, ModelUse> models, ref double cost,
            out long tin, out long tout, out long cr, out long cw)
        {
            tin = Num(RxIn, line); tout = Num(RxOut, line);
            cr = Num(RxCache, line); cw = Num(RxCacheW, line);
            if (tin + tout + cr + cw == 0) return;
            var mm = RxModel.Match(line);
            string key = mm.Success ? ShortModel(mm.Groups[1].Value) : "อื่นๆ";
            ModelUse u;
            if (!models.TryGetValue(key, out u)) { u = new ModelUse(); models[key] = u; }
            u.In += tin; u.Out += tout; u.CacheR += cr; u.CacheW += cw;
            double pIn, pOut;
            PriceOf(mm.Success ? mm.Groups[1].Value : "sonnet", out pIn, out pOut);
            cost += (tin * pIn + tout * pOut + cr * pIn * 0.1 + cw * pIn * 1.25) / 1e6;
        }

        static double CostOf(ModelUse u, double pIn, double pOut)
        {
            return (u.In * pIn + u.Out * pOut + u.CacheR * pIn * 0.1 + u.CacheW * pIn * 1.25) / 1e6;
        }

        static string FmtModels(Dictionary<string, ModelUse> models, int take)
        {
            return string.Join(" · ", models.OrderByDescending(kv => kv.Value.Total).Take(take)
                .Select(kv => kv.Key + " " + FmtTok(kv.Value.Total)).ToArray());
        }

        public static LocalStats ScanToday()
        {
            var st = new LocalStats();
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                if (!Directory.Exists(root)) return st;
                var today = DateTime.Today;
                var skills = new Dictionary<string, int>();
                var projects = new Dictionary<string, long>();
                var models = new Dictionary<string, ModelUse>();
                double cost = 0;
                foreach (var f in Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    if (fi.LastWriteTime < today || fi.Length > 30 * 1024 * 1024) continue;
                    st.Sessions++;
                    long tok = 0;
                    foreach (var line in File.ReadLines(f))
                    {
                        long before = models.Values.Sum(u => u.Total);
                        ScanLine(line, models, ref cost);
                        tok += models.Values.Sum(u => u.Total) - before;
                        var sm = RxSkill.Match(line);
                        if (sm.Success)
                        {
                            var k = sm.Groups[1].Value;
                            int c; skills.TryGetValue(k, out c); skills[k] = c + 1;
                        }
                    }
                    var proj = Path.GetFileName(Path.GetDirectoryName(f));
                    long pv; projects.TryGetValue(proj, out pv); projects[proj] = pv + tok;
                }
                st.InTok = models.Values.Sum(u => u.In);
                st.OutTok = models.Values.Sum(u => u.Out);
                st.CacheTok = models.Values.Sum(u => u.CacheR);
                st.Cost = cost;
                ModelUse fu;
                if (models.TryGetValue("Fable", out fu)) st.FableCost = CostOf(fu, 10, 50);
                st.Models = FmtModels(models, 3);
                st.Skills = string.Join(", ", skills.OrderByDescending(kv => kv.Value).Take(3)
                    .Select(kv => kv.Key + "×" + kv.Value).ToArray());
                st.Projects = string.Join(" · ", projects.OrderByDescending(kv => kv.Value).Take(2)
                    .Select(kv => ShortProj(kv.Key) + " " + FmtTok(kv.Value)).ToArray());
            }
            catch { }
            return st;
        }

        // Token activity over the last ~26 weeks, per day and per project (7d / 1d),
        // Codex-heatmap style. Day attribution uses the file's last-write date —
        // close enough for a heat view without parsing per-line timestamps.
        public static ActivityStats ScanActivity()
        {
            var st = new ActivityStats();
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                if (!Directory.Exists(root)) return st;
                var cutoff = DateTime.Today.AddDays(-35);
                var p7 = new Dictionary<string, long>();
                var p1 = new Dictionary<string, long>();
                var models7 = new Dictionary<string, ModelUse>();
                var skills7 = new Dictionary<string, int>();
                double cost7 = 0;
                long workTok = 0, bigTok = 0, sideTok = 0;
                foreach (var f in Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    if (fi.LastWriteTime < cutoff || fi.Length > 30 * 1024 * 1024) continue;
                    var day = fi.LastWriteTime.Date;
                    bool in7d = day >= DateTime.Today.AddDays(-7);
                    long tok = 0;
                    if (in7d)
                    {
                        foreach (var line in File.ReadLines(f))
                        {
                            long tin, tout, cr, cw;
                            ScanLine(line, models7, ref cost7, out tin, out tout, out cr, out cw);
                            long work = tin + tout;
                            tok += work;
                            workTok += work;
                            if (tin + cr + cw > 150000) bigTok += work;      // large-context request
                            if (work > 0 && line.Contains("\"isSidechain\":true")) sideTok += work;
                            var sm = RxSkill.Match(line);
                            if (sm.Success)
                            {
                                int c; skills7.TryGetValue(sm.Groups[1].Value, out c);
                                skills7[sm.Groups[1].Value] = c + 1;
                            }
                        }
                    }
                    else
                    {
                        string text = File.ReadAllText(f);
                        foreach (Match m in RxIn.Matches(text)) { long v; long.TryParse(m.Groups[1].Value, out v); tok += v; }
                        foreach (Match m in RxOut.Matches(text)) { long v; long.TryParse(m.Groups[1].Value, out v); tok += v; }
                    }
                    if (tok == 0) continue;
                    long dv; st.PerDay.TryGetValue(day, out dv); st.PerDay[day] = dv + tok;
                    var proj = ShortProj(Path.GetFileName(Path.GetDirectoryName(f)));
                    long pv;
                    if (in7d) { p7.TryGetValue(proj, out pv); p7[proj] = pv + tok; }
                    if (day == DateTime.Today) { p1.TryGetValue(proj, out pv); p1[proj] = pv + tok; }
                }
                st.MaxDay = st.PerDay.Count > 0 ? st.PerDay.Values.Max() : 0;
                st.Top7d = string.Join(" · ", p7.OrderByDescending(kv => kv.Value).Take(3)
                    .Select(kv => kv.Key + " " + FmtTok(kv.Value)).ToArray());
                st.Top1d = string.Join(" · ", p1.OrderByDescending(kv => kv.Value).Take(3)
                    .Select(kv => kv.Key + " " + FmtTok(kv.Value)).ToArray());
                st.Models7d = FmtModels(models7, 3);
                st.Cost7 = cost7;
                if (workTok > 0)
                {
                    st.PctBig = (int)(bigTok * 100 / workTok);
                    st.PctSide = (int)(sideTok * 100 / workTok);
                }
                int totalSkill = skills7.Values.Sum();
                if (totalSkill > 0)
                    st.SkillsLine = string.Join(" · ", skills7.OrderByDescending(kv => kv.Value).Take(3)
                        .Select(kv => "/" + kv.Key + " " + (kv.Value * 100 / totalSkill) + "%").ToArray());
            }
            catch { }
            return st;
        }

        static string ShortProj(string encoded)
        {
            var i = encoded.LastIndexOf('-');
            var s = i >= 0 && i < encoded.Length - 1 ? encoded.Substring(i + 1) : encoded;
            return s.Length > 14 ? s.Substring(0, 14) : s;
        }

        public static string FmtTok(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("0.#") + "M";
            if (n >= 1000) return (n / 1000.0).ToString("0.#") + "K";
            return n.ToString();
        }
    }

    class Settings
    {
        public int X = -1, Y = -1;
        public bool Pinned = true;
        public string Pets = "";       // no pixel pets by default — opt in from the menu
        public string Accounts = ""; // extra .credentials.json paths, ';' separated
        public bool Mini = false;
        public string ActiveSlot = ""; // path holding the currently-live account's parked copy
        public string Lang = "th";     // th | en
        public int Tone = 1;           // 0 ทางการ · 1 เป็นมิตร · 2 ขี้เล่น · 3 เกรียน
        public string NickName = "";   // how notifications address the user
        public int Theme = 0;          // 0 Aurora(rings) 1 Cozy 2 Kawaii 3 Brutalist
        public bool HidePet = false;   // hide the pet entirely

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }
        public static string HistoryPath { get { return Path.Combine(Dir, "history.csv"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this));
            }
            catch { }
        }
    }

    // Our own toast — Windows balloons can't be themed, so notifications are drawn
    // as small cards matching the widget: dark gradient, colored accent, spark logo.
    class Toast : Form
    {
        static readonly List<Toast> Open = new List<Toast>();
        readonly string _title, _msg;
        readonly Color _accent;

        protected override bool ShowWithoutActivation { get { return true; } }

        public static void Pop(string title, string msg, Color accent)
        {
            var t = new Toast(title, msg, accent);
            var wa = Screen.PrimaryScreen.WorkingArea;
            t.Location = new Point(wa.Right - t.Width - 14, wa.Bottom - (t.Height + 10) * (Open.Count + 1) - 6);
            Open.Add(t);
            t.Show();
        }

        Toast(string title, string msg, Color accent)
        {
            _title = title; _msg = msg; _accent = accent;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            Width = 320; Height = 84;
            using (var p = RoundedRect(new Rectangle(0, 0, Width, Height), 10))
                Region = new Region(p);
            var life = new Timer { Interval = 8000 };
            life.Tick += delegate { life.Stop(); Close(); };
            life.Start();
            Click += delegate { Close(); };
            FormClosed += delegate { Open.Remove(this); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(38, 40, 56), Color.FromArgb(24, 25, 34), 90f))
                g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(Color.FromArgb(70, 74, 100)))
                g.DrawPath(pen, RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 10));
            using (var ac = new SolidBrush(_accent))
                g.FillRectangle(ac, 0, 0, 4, Height);
            Icons.Claude(g, 24, 24, 9f, Icons.Coral);
            TextRenderer.DrawText(g, _title, new Font("Prompt", 9.5f, FontStyle.Bold),
                new Point(40, 14), Color.FromArgb(235, 235, 243));
            TextRenderer.DrawText(g, _msg, new Font("Prompt", 8.5f),
                new Rectangle(40, 36, Width - 52, Height - 42), Color.FromArgb(175, 177, 192),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Claude-style sunburst mark, drawn in code (12 tapered rays, coral).
    static class Icons
    {
        public static readonly Color Coral = Color.FromArgb(217, 119, 87);

        // Claude "spark" — 12 short petals radiating from a solid hub, rounded tips.
        public static void Claude(Graphics g, float cx, float cy, float r, Color c)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float hub = r * 0.34f;      // solid centre
            float inner = r * 0.44f;    // where a petal starts
            float petalW = r * 0.34f;   // petal half-width (fat, not spiky)
            using (var b = new SolidBrush(c))
            using (var pen = new Pen(c, petalW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.FillEllipse(b, cx - hub, cy - hub, hub * 2, hub * 2);
                for (int i = 0; i < 12; i++)
                {
                    double a = i * Math.PI / 6;
                    g.DrawLine(pen,
                        cx + (float)(Math.Cos(a) * inner), cy + (float)(Math.Sin(a) * inner),
                        cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r));
                }
            }
            g.SmoothingMode = old;
        }
    }

    // Transparent always-on-top window so the pet image can perch on the widget's
    // TOP edge, outside the card — a form can't paint beyond its own bounds.
    class PetOverlay : Form
    {
        Image _img;
        int _mood, _frame;

        protected override bool ShowWithoutActivation { get { return true; } }

        public PetOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            // near-black key: anti-aliased edges blend toward dark and read as a
            // soft outline instead of the classic magenta fringe
            BackColor = Color.FromArgb(1, 1, 1);
            TransparencyKey = BackColor;
        }

        public void Set(Image img, int mood, int frame)
        {
            _img = img; _mood = mood; _frame = frame;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_img == null) return;
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var dest = new Rectangle(0, 0, Width, Height);
            if (_mood == 2)
            {
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    var cm = new System.Drawing.Imaging.ColorMatrix(new[]
                    {
                        new float[] { 1.15f, 0, 0, 0, 0 },
                        new float[] { 0, 0.75f, 0, 0, 0 },
                        new float[] { 0, 0, 0.75f, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 },
                        new float[] { 0.12f, 0, 0, 0, 1 },
                    });
                    ia.SetColorMatrix(cm);
                    g.DrawImage(_img, dest, 0, 0, _img.Width, _img.Height, GraphicsUnit.Pixel, ia);
                }
            }
            else g.DrawImage(_img, dest);

            if (_mood >= 1)
                using (var sweat = new SolidBrush(Color.FromArgb(110, 180, 240)))
                {
                    g.FillEllipse(sweat, 2, 6 + (_frame % 4) * 2, 4, 7);
                    if (_mood == 2)
                        g.FillEllipse(sweat, Width - 6, 12 + ((_frame + 2) % 4) * 2, 4, 7);
                }
        }
    }

    // Dark theme for context menus so they match the card instead of stock white.
    class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(30, 32, 44);
        static readonly Color Hover = Color.FromArgb(58, 62, 88);
        static readonly Color Border = Color.FromArgb(70, 74, 100);
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Hover; } }
        public override Color MenuItemPressedGradientEnd { get { return Hover; } }
        public override Color MenuItemBorder { get { return Hover; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return Bg; } }
    }

    class WidgetForm : Form
    {
        // Near-realtime polling: a FileSystemWatcher on ~\.claude\projects marks the
        // moments Claude is actually in use. While active we poll every ActiveSec
        // (min MinGapSec apart); idle we fall back to IdleSec. 429 still backs off.
        const int ActiveSec = 90;
        const int IdleSec = 600;
        const int MinGapSec = 45;
        const int ActiveWindowMin = 3;
        const int BackoffMinutes = 15;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "ClaudeUsageWidget";

        readonly Timer _timer = new Timer();
        readonly NotifyIcon _tray = new NotifyIcon();
        readonly Settings _settings = Settings.Load();
        readonly List<KeyValuePair<DateTime, int>> _sessionSamples = new List<KeyValuePair<DateTime, int>>();
        readonly List<KeyValuePair<DateTime, int>> _weeklySamples = new List<KeyValuePair<DateTime, int>>();
        int _mascotFrame;
        readonly Dictionary<string, string> _notifiedResetsAt = new Dictionary<string, string>();
        readonly Dictionary<string, int> _notifiedLevel = new Dictionary<string, int>();

        UsageSnapshot _usage;
        string _status = "loading…";
        string _plan = "";
        string _account = "";
        bool _fetching;
        FileSystemWatcher _watcher;
        DateTime _lastFetchTry = DateTime.MinValue;
        DateTime _backoffUntil = DateTime.MinValue;
        DateTime _lastActivity = DateTime.MinValue;
        DateTimeOffset _nextReset = DateTimeOffset.MaxValue;
        bool _resetForced;
        bool _fetchQueued;
        string _lastHistoryLine = "";
        bool _detailsOpen;
        bool _graphOpen;
        int _streak;
        readonly HashSet<DateTime> _histDates = new HashSet<DateTime>();
        readonly HashSet<string> _useItNotified = new HashSet<string>();
        Rectangle _btnDetails, _btnMin, _btnMenu, _btnGraph, _sparkRect;
        LocalStats _stats;
        DateTime _statsAt = DateTime.MinValue;
        bool _scanning;
        int _hmGX, _hmGY, _hmCellW, _hmCellH;
        string _hoverText = "";       // drawn in-panel, no flaky ToolTip
        Point _hoverAt;
        ActivityStats _activity;
        DateTime _activityAt = DateTime.MinValue;
        bool _actScanning;
        DateTime _lastSwitchTip = DateTime.MinValue;

        class ExtraAccount
        {
            public string CredPath;
            public string Label = "";
            public string Plan = "";
            public UsageSnapshot Snap;
            public string Err;
        }
        readonly List<ExtraAccount> _extras = new List<ExtraAccount>();
        Point _dragOffset;
        bool _dragging;
        Rectangle _btnClose, _btnRefresh, _btnPin;

        static readonly Color BgColor = Color.FromArgb(30, 30, 40);
        static readonly Color CardLine = Color.FromArgb(55, 55, 70);
        static readonly Color TextDim = Color.FromArgb(150, 150, 165);
        static readonly Color TextMain = Color.FromArgb(230, 230, 238);
        static Font MkFont(string[] names, float size, FontStyle st)
        {
            foreach (var name in names)
            {
                var f = new Font(name, size, st);
                if (f.Name == name) return f;
                f.Dispose();
            }
            return new Font("Segoe UI", size, st);
        }

        // Prompt (Thai Google font) first when installed — falls back cleanly
        static readonly string[] FontChain = { "Prompt", "Segoe UI Variable Display", "Segoe UI" };
        static readonly string[] FontChainText = { "Prompt", "Segoe UI Variable Text", "Segoe UI" };
        static readonly Font FTitle = MkFont(FontChain, 10f, FontStyle.Bold);
        static readonly Font FLabel = MkFont(FontChain, 8f, FontStyle.Bold);
        static readonly Font FSmall = MkFont(FontChainText, 7.5f, FontStyle.Regular);
        static readonly Font FPct = MkFont(FontChain, 10f, FontStyle.Bold);

        readonly bool _mock;
        public WidgetForm() : this(false) { }
        public WidgetForm(bool mock)
        {
            _mock = mock;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = _settings.Pinned;
            BackColor = BgColor;
            DoubleBuffered = true;
            Width = 300;
            Height = 240;

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = (_settings.X >= 0 && _settings.Y >= 0 && wa.Contains(new Point(_settings.X, _settings.Y)))
                ? new Point(_settings.X, _settings.Y)
                : new Point(wa.Right - Width - 20, wa.Top + 20);

            // window / taskbar icon comes from the app.ico embedded at build time
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            RebuildExtras();
            LoadPetImages();
            SetupTray();
            SetupActivityWatcher();
            LoadHistory();

            _timer.Interval = 15000; // cheap check tick; actual fetch cadence decided in PollTick
            _timer.Tick += delegate { PollTick(); };
            _timer.Start();

            var uiTick = new Timer { Interval = 30000 };
            uiTick.Tick += delegate { Invalidate(); };
            uiTick.Start();

            var mascotTick = new Timer { Interval = 160 };
            mascotTick.Tick += delegate
            {
                _mascotFrame++;
                Invalidate(new Rectangle(0, Height - 46, Width, 46)); // pets strip
                SyncPetOverlay();
            };
            mascotTick.Start();
            LocationChanged += delegate { SyncPetOverlay(); };
            VisibleChanged += delegate { SyncPetOverlay(); };

            MouseDown += OnDragStart;
            MouseMove += OnDragMove;
            MouseUp += OnDragEnd;
            MouseClick += OnClickButtons;
            MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (_btnClose.Contains(e.Location) || _btnRefresh.Contains(e.Location)
                    || _btnPin.Contains(e.Location) || _btnMin.Contains(e.Location)) return;
                _settings.Mini = !_settings.Mini;
                _settings.Save();
                ApplyMini();
            };

            // easiest way to add another account: drop its .credentials.json onto the widget
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                foreach (string f in (string[])e.Data.GetData(DataFormats.FileDrop))
                    if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) AddAccount(f);
            };

            if (_settings.Mini) ApplyMini();

            Load += delegate
            {
                if (_mock)
                {
                    _account = "demo@example.com"; _plan = "Max";
                    var snap = new UsageSnapshot { FetchedAt = DateTime.Now };
                    snap.Limits.Add(new LimitInfo { Kind = "session", Label = "SESSION", Percent = 74, ResetsAt = DateTimeOffset.Now.AddHours(2).AddMinutes(40) });
                    snap.Limits.Add(new LimitInfo { Kind = "weekly_all", Label = "WEEKLY", Percent = 28, ResetsAt = DateTimeOffset.Now.AddDays(2).AddHours(14) });
                    snap.Limits.Add(new LimitInfo { Kind = "weekly_scoped", Label = "FABLE", Percent = 25, ResetsAt = DateTimeOffset.Now.AddDays(6).AddHours(14) });
                    ApplySnapshot(snap);
                    _status = "mock";
                }
                else BeginFetch();
            };
            FormClosing += delegate
            {
                _settings.X = Left; _settings.Y = Top; _settings.Pinned = TopMost;
                _settings.Save();
                _tray.Visible = false;
            };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (T.Sharp) { Region = new Region(new Rectangle(0, 0, Width, Height)); return; }
            using (var path = RoundedRect(new Rectangle(0, 0, Width, Height), 12))
                Region = new Region(path);
        }

        void SetupTray()
        {
            _tray.Icon = MakeTrayIcon(-1);
            _tray.Text = "Claude Usage Widget";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { Visible = !Visible; };
            BuildTrayMenu();
        }

        // rebuilt whenever the language changes so every label follows along
        void BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Font = MkFont(FontChainText, 9f, FontStyle.Regular);
            menu.Items.Add(L("👁  แสดง / ซ่อน", "👁  Show / Hide"), null, delegate { Visible = !Visible; });
            menu.Items.Add(L("🔄  รีเฟรชตอนนี้", "🔄  Refresh now"), null, delegate { BeginFetch(); });
            var autostart = new ToolStripMenuItem(L("🚀  เปิดพร้อม Windows", "🚀  Start with Windows")) { Checked = IsAutoStart() };
            autostart.Click += delegate { SetAutoStart(!IsAutoStart()); autostart.Checked = IsAutoStart(); };
            menu.Items.Add(autostart);

            var petsMenu = new ToolStripMenuItem("🐾  Pets");
            foreach (var pet in AllPets)
            {
                var p = pet;
                var item = new ToolStripMenuItem(p.Name) { Checked = SelectedPets().Any(x => x.Id == p.Id), CheckOnClick = true };
                item.CheckedChanged += delegate
                {
                    var ids = SelectedPets().Select(x => x.Id).ToList();
                    if (item.Checked) { if (!ids.Contains(p.Id)) ids.Add(p.Id); }
                    else ids.Remove(p.Id);
                    _settings.Pets = string.Join(",", ids.ToArray());
                    _settings.Save();
                    Invalidate();
                };
                petsMenu.DropDownItems.Add(item);
            }
            petsMenu.DropDownItems.Add(new ToolStripSeparator());
            petsMenu.DropDownItems.Add(L("🖼  เลือกรูป pet (รูปเดียว ปรับอารมณ์อัตโนมัติ)…",
                "🖼  Choose pet image (one image, auto mood)…"), null, delegate
            {
                using (var dlg = new OpenFileDialog { Filter = "รูปภาพ|*.png;*.jpg;*.jpeg;*.gif" })
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        Directory.CreateDirectory(PetImgDir);
                        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif" })
                        {
                            var old = System.IO.Path.Combine(PetImgDir, "pet" + ext);
                            if (File.Exists(old)) File.Delete(old);
                        }
                        File.Copy(dlg.FileName, System.IO.Path.Combine(PetImgDir,
                            "pet" + System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant()), true);
                        LoadPetImages();
                        Invalidate();
                    }
            });
            petsMenu.DropDownItems.Add(L("ลบรูป กลับเป็น pixel pets", "Remove images (back to pixel pets)"), null, delegate
            {
                try { if (Directory.Exists(PetImgDir)) Directory.Delete(PetImgDir, true); } catch { }
                LoadPetImages();
                Invalidate();
            });
            petsMenu.DropDownItems.Add(new ToolStripSeparator());
            var hidePet = new ToolStripMenuItem(L("ซ่อน pet ทั้งหมด", "Hide pet entirely")) { Checked = _settings.HidePet };
            hidePet.Click += delegate
            {
                _settings.HidePet = !_settings.HidePet;
                hidePet.Checked = _settings.HidePet;
                _settings.Save();
                SyncPetOverlay();
                Invalidate();
            };
            petsMenu.DropDownItems.Add(hidePet);
            menu.Items.Add(petsMenu);

            var toneMenu = new ToolStripMenuItem(L("🔔  โทนแจ้งเตือน", "🔔  Notification tone"));
            string[] tones = _settings.Lang == "en"
                ? new[] { "Formal", "Friendly", "Playful", "Savage 🔥" }
                : new[] { "ทางการ", "เป็นมิตร", "ขี้เล่น", "เกรียน 🔥" };
            for (int ti = 0; ti < 4; ti++)
            {
                int tone = ti;
                var item = new ToolStripMenuItem(tones[ti]) { Checked = _settings.Tone == ti };
                item.Click += delegate
                {
                    _settings.Tone = tone;
                    _settings.Save();
                    foreach (var it in toneMenu.DropDownItems.OfType<ToolStripMenuItem>()) it.Checked = it == item;
                    Toast.Pop(L("โทนแจ้งเตือน: ", "Notification tone: ") + tones[tone],
                        tone == 3 ? L("โอเค จัดเต็มนะ อย่าหาว่าไม่เตือน 😏", "okay, no mercy mode 😏")
                                  : L("บันทึกแล้ว", "saved"), Color.CornflowerBlue);
                };
                toneMenu.DropDownItems.Add(item);
            }
            toneMenu.DropDownItems.Add(new ToolStripSeparator());
            toneMenu.DropDownItems.Add(L("✏  ชื่อเรียกผู้ใช้…", "✏  Your nickname…"), null, delegate
            {
                using (var f = new Form
                {
                    Text = L("ให้แจ้งเตือนเรียกคุณว่าอะไร?", "What should notifications call you?"),
                    Width = 320, Height = 128, FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false,
                })
                {
                    var tb = new TextBox { Left = 12, Top = 14, Width = 278, Text = _settings.NickName };
                    var ok = new Button { Text = "OK", Left = 208, Top = 48, Width = 82, DialogResult = DialogResult.OK };
                    f.Controls.Add(tb); f.Controls.Add(ok); f.AcceptButton = ok;
                    if (f.ShowDialog() == DialogResult.OK)
                    {
                        _settings.NickName = tb.Text.Trim();
                        _settings.Save();
                        if (_settings.NickName.Length > 0)
                            Toast.Pop(L("โอเค ", "okay ") + _settings.NickName + " 👋",
                                L("ต่อไปจะเรียกแบบนี้นะ", "notifications will call you this"), Color.CornflowerBlue);
                    }
                }
            });
            menu.Items.Add(toneMenu);

            var langMenu = new ToolStripMenuItem("🌐  ภาษา / Language");
            foreach (var lang in new[] { "th", "en" })
            {
                var lg = lang;
                var item = new ToolStripMenuItem(lang == "th" ? "ไทย" : "English") { Checked = _settings.Lang == lang };
                item.Click += delegate
                {
                    _settings.Lang = lg;
                    _settings.Save();
                    UpdateHeight();
                    Invalidate();
                    BuildTrayMenu(); // relabel the whole menu in the new language
                };
                langMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(langMenu);

            var themeMenu = new ToolStripMenuItem("🎨  " + L("ธีม / Theme", "Theme"));
            string[] themeNames = { "Aurora (วงแหวน)", "Cozy Night", "Kawaii", "Brutalist" };
            for (int ti = 0; ti < 4; ti++)
            {
                int theme = ti;
                var item = new ToolStripMenuItem(themeNames[ti]) { Checked = _settings.Theme == ti };
                item.Click += delegate
                {
                    _settings.Theme = theme;
                    _settings.Save();
                    foreach (var it in themeMenu.DropDownItems.OfType<ToolStripMenuItem>()) it.Checked = it == item;
                    OnResize(EventArgs.Empty); // re-shape region (sharp vs rounded)
                    UpdateHeight();
                    Invalidate();
                };
                themeMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(themeMenu);

            var accMenu = new ToolStripMenuItem("👥  Accounts");
            accMenu.DropDownItems.Add(L("สแกนหาบัญชีอัตโนมัติ (WSL)", "Auto-scan accounts (WSL)"), null, delegate
            {
                int found = 0;
                foreach (var distro in new[] { "Ubuntu", "Ubuntu-22.04", "Ubuntu-24.04", "Debian", "kali-linux" })
                {
                    try
                    {
                        var home = @"\\wsl.localhost\" + distro + @"\home";
                        if (!Directory.Exists(home)) continue;
                        foreach (var u in Directory.GetDirectories(home))
                        {
                            var f = System.IO.Path.Combine(u, ".claude", ".credentials.json");
                            if (File.Exists(f)) { AddAccount(f); found++; }
                        }
                    }
                    catch { }
                }
                Toast.Pop("Claude Usage", found > 0 ? "เจอ " + found + " บัญชีใน WSL เพิ่มให้แล้ว" : "ไม่เจอบัญชีใน WSL", Color.CornflowerBlue);
            });
            accMenu.DropDownItems.Add(L("เพิ่มบัญชี (เลือกไฟล์ .credentials.json)…", "Add account (pick .credentials.json)…"), null, delegate
            {
                using (var dlg = new OpenFileDialog { Filter = "credentials (*.json)|*.json", Title = "เลือกไฟล์ .credentials.json ของอีกบัญชี — หรือลากไฟล์มาวางบน widget ก็ได้" })
                    if (dlg.ShowDialog() == DialogResult.OK) AddAccount(dlg.FileName);
            });
            accMenu.DropDownItems.Add(L("เพิ่มบัญชีใหม่ (login ผ่านเบราว์เซอร์)…", "New account (browser login)…"), null, delegate
            {
                var dir = System.IO.Path.Combine(Settings.Dir, "accounts", "acc-" + DateTime.Now.ToString("yyMMddHHmmss"));
                Directory.CreateDirectory(dir);
                AddAccount(System.IO.Path.Combine(dir, ".credentials.json"));
                // quoted set "VAR=..." — a space in the profile path must not split the command
                // WorkingDirectory = the app-owned dir (not the widget's CWD, e.g. Downloads):
                // cmd resolves the bare `claude` command from the current directory first, so a
                // planted claude.exe/.bat in the launch folder would otherwise run. dir is created
                // by us this instant and contains no attacker binary.
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/k title Claude Login && set \"CLAUDE_CONFIG_DIR=" + dir + "\"" +
                    " && echo กำลังเปิด Claude Code ใน config แยก - พิมพ์ /login แล้วเลือกบัญชีที่ต้องการ && claude")
                { WorkingDirectory = dir });
            });
            // one clickable switch entry per tracked account, rebuilt on open
            accMenu.DropDownOpening += delegate
            {
                for (int i = accMenu.DropDownItems.Count - 1; i >= 0; i--)
                    if (accMenu.DropDownItems[i].Tag as string == "switch") accMenu.DropDownItems.RemoveAt(i);
                foreach (var accItem in _extras)
                {
                    var acc = accItem;
                    if (acc.Snap == null && !File.Exists(acc.CredPath)) continue;
                    string usage = acc.Snap != null && acc.Snap.Limits.Count > 0
                        ? " (" + L("หนักสุด ", "peak ") + acc.Snap.Limits.Max(x => x.Percent) + "%)" : "";
                    var mi = new ToolStripMenuItem(L("สลับใช้: ", "Switch to: ") + acc.Label + usage) { Tag = "switch", ForeColor = TextMain };
                    mi.Click += delegate { SwitchTo(acc); };
                    accMenu.DropDownItems.Add(mi);
                }
            };
            accMenu.DropDownItems.Add(L("ล้างบัญชีเสริมทั้งหมด", "Clear extra accounts"), null, delegate
            {
                _settings.Accounts = "";
                _settings.Save();
                RebuildExtras();
                UpdateHeight();
                Invalidate();
            });
            menu.Items.Add(accMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L("✕  ออกจากแอป", "✕  Exit"), null, delegate { Close(); });

            var renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
            menu.Renderer = renderer;
            menu.ForeColor = TextMain;
            foreach (ToolStripItem it in menu.Items)
            {
                it.ForeColor = TextMain;
                var dd = it as ToolStripMenuItem;
                if (dd != null && dd.HasDropDownItems)
                {
                    dd.DropDown.Renderer = renderer;
                    dd.DropDown.ForeColor = TextMain;
                    foreach (ToolStripItem sub in dd.DropDownItems) sub.ForeColor = TextMain;
                }
            }
            _tray.ContextMenuStrip = menu;
        }

        // ---- fetching ----

        void SetupActivityWatcher()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
                if (!Directory.Exists(dir)) return;
                _watcher = new FileSystemWatcher(dir, "*.jsonl");
                _watcher.IncludeSubdirectories = true;
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                // Marshal onto the UI thread: _lastActivity/_fetchQueued are read by the
                // timer and OnPaint there. A raw 8-byte DateTime write from the watcher
                // thread isn't atomic on 32-bit, so update it where everything else reads it.
                FileSystemEventHandler onActivity = delegate
                {
                    try { BeginInvoke((MethodInvoker)delegate { _lastActivity = DateTime.Now; _fetchQueued = true; }); }
                    catch { /* form closing / handle gone — safe to drop */ }
                };
                _watcher.Changed += onActivity;
                _watcher.Created += onActivity;
                _watcher.EnableRaisingEvents = true;
            }
            catch { /* watcher is an optimization; polling still works without it */ }
        }

        bool IsActive { get { return (DateTime.Now - _lastActivity).TotalMinutes < ActiveWindowMin; } }

        // Swap ~\.claude\.credentials.json to another account, so every NEW Claude
        // Code session (and this widget) uses it. The live file is first backed up
        // (timestamped) and parked into the slot of the account it belongs to, so
        // each account always keeps its freshest refresh token.
        void SwitchTo(ExtraAccount acc)
        {
            try
            {
                // Validate the target is a real credentials file BEFORE overwriting the live
                // token — refuse to clobber ~\.claude\.credentials.json with junk (the source
                // can be any drag-dropped/picked .json). The live file is backed up below, but
                // don't even start the swap on an invalid source.
                if (Credentials.LoadFrom(acc.CredPath) == null)
                {
                    Toast.Pop("Claude Usage", L("ไฟล์บัญชีนี้ไม่ใช่ credentials ที่ใช้ได้ — ยกเลิกการสลับ", "That account file isn't a valid credentials file — switch cancelled"), Color.FromArgb(226, 102, 102));
                    return;
                }
                var live = Credentials.Path;
                var mainSlot = System.IO.Path.Combine(Settings.Dir, "accounts", "main", ".credentials.json");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mainSlot));
                var park = string.IsNullOrEmpty(_settings.ActiveSlot) ? mainSlot : _settings.ActiveSlot;
                if (File.Exists(live))
                {
                    File.Copy(live, System.IO.Path.Combine(Settings.Dir,
                        "cred-backup-" + DateTime.Now.ToString("yyMMdd-HHmmss") + ".json"), true);
                    File.Copy(live, park, true);
                    // credential backups are sensitive — keep only the 5 newest
                    foreach (var old in Directory.GetFiles(Settings.Dir, "cred-backup-*.json")
                        .OrderByDescending(p => p).Skip(5))
                        try { File.Delete(old); } catch { }
                }
                File.Copy(acc.CredPath, live, true);
                _settings.ActiveSlot = acc.CredPath;
                _settings.Save();
                if (park == mainSlot) AddAccount(mainSlot); // keep the old main visible & switchable
                Toast.Pop("สลับบัญชีแล้ว → " + acc.Label,
                    "Claude Code เซสชันใหม่จะใช้บัญชีนี้ทันที (เซสชันที่เปิดค้างอยู่ต้องปิด-เปิดใหม่)",
                    Color.FromArgb(92, 190, 140));
                BeginFetch();
            }
            catch (Exception ex)
            {
                Toast.Pop("Claude Usage", "สลับบัญชีไม่สำเร็จ: " + ex.Message, Color.FromArgb(226, 102, 102));
            }
        }

        string L(string th, string en) { return _settings.Lang == "en" ? en : th; }

        void ApplyMini()
        {
            if (_settings.Mini) { Width = 216; Height = 40; }
            else { Width = 300; UpdateHeight(); }
            Invalidate();
        }

        void AddAccount(string path)
        {
            // ';' is the account-list separator — a path containing one would corrupt
            // the stored list (and split into bogus paths on reload). Reject it.
            if (string.IsNullOrEmpty(path) || path.Contains(";"))
            {
                Toast.Pop("Claude Usage", L("path ของไฟล์มีอักขระ ';' ไม่รองรับ", "File path contains ';' — not supported"), Color.FromArgb(226, 102, 102));
                return;
            }
            if ((";" + _settings.Accounts + ";").Contains(";" + path + ";")) return;
            _settings.Accounts = string.IsNullOrEmpty(_settings.Accounts) ? path : _settings.Accounts + ";" + path;
            _settings.Save();
            RebuildExtras();
            BeginFetch();
        }

        void RebuildExtras()
        {
            _extras.Clear();
            foreach (var p in (_settings.Accounts ?? "").Split(';'))
            {
                var path = p.Trim();
                if (path.Length == 0) continue;
                string label = "";
                try
                {
                    // ...\<user>\.claude\.credentials.json → show <user>
                    var claudeDir = System.IO.Path.GetDirectoryName(path);
                    label = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(claudeDir));
                }
                catch { }
                _extras.Add(new ExtraAccount { CredPath = path, Label = string.IsNullOrEmpty(label) ? path : label });
            }
        }

        void StartScan()
        {
            if (_scanning) return;
            if (_stats != null && (DateTime.Now - _statsAt).TotalMinutes < 5) return;
            _scanning = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var st = LocalScanner.ScanToday();
                BeginInvoke((MethodInvoker)delegate
                {
                    _stats = st; _statsAt = DateTime.Now; _scanning = false;
                    UpdateHeight(); // Fable banner may now need its 32px
                    Invalidate();
                });
            });
        }

        void PollTick()
        {
            var now = DateTime.Now;

            // a limit just reset — refetch immediately (once) even if we're in 429
            // backoff, otherwise the bar sticks at 100% past the reset time
            if (!_resetForced && DateTimeOffset.Now >= _nextReset && _nextReset != DateTimeOffset.MaxValue)
            {
                _resetForced = true;
                _backoffUntil = DateTime.MinValue;
                BeginFetch();
                return;
            }
            if (now < _backoffUntil) return;
            double gap = (now - _lastFetchTry).TotalSeconds;
            int interval = IsActive ? ActiveSec : IdleSec;
            if ((_fetchQueued && gap >= MinGapSec) || gap >= interval)
            {
                _fetchQueued = false;
                BeginFetch();
            }
        }

        void BeginFetch()
        {
            if (_fetching) return;
            _fetching = true;
            _lastFetchTry = DateTime.Now;
            _status = "refreshing…";
            Invalidate();

            var creds = Credentials.Load();
            if (creds == null)
            {
                _status = "ไม่พบ token — ติดตั้ง Claude Code ก่อน";
                _fetching = false;
                Invalidate();
                return;
            }
            _plan = PlanBadge(creds.SubscriptionType);
            _account = creds.Account;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                UsageSnapshot snap = null;
                string status;
                try
                {
                    snap = UsageClient.Fetch(creds.AccessToken);
                    status = (IsActive ? "live · updated " : "updated ") + snap.FetchedAt.ToString("HH:mm:ss");
                }
                catch (RateLimitedException)
                {
                    status = "rate limited — backoff " + BackoffMinutes + "m";
                    _backoffUntil = DateTime.Now.AddMinutes(BackoffMinutes);
                }
                catch (UnauthorizedException)
                {
                    status = "token หมดอายุ — เปิด Claude Code สักครั้ง";
                }
                catch (Exception)
                {
                    status = "network error — retrying";
                }

                foreach (var acc in _extras.ToArray())
                {
                    try
                    {
                        var c2 = Credentials.LoadFrom(acc.CredPath);
                        if (c2 == null) { acc.Err = "ไม่พบไฟล์ credentials"; acc.Snap = null; continue; }
                        acc.Plan = PlanBadge(c2.SubscriptionType);
                        if (!string.IsNullOrEmpty(c2.Account)) acc.Label = c2.Account; // prefer real email
                        acc.Snap = UsageClient.Fetch(c2.AccessToken);
                        acc.Err = null;
                    }
                    catch (UnauthorizedException) { acc.Err = "token หมดอายุ"; acc.Snap = null; }
                    catch (RateLimitedException) { acc.Err = "rate limited"; }
                    catch { acc.Err = "network error"; }
                }

                BeginInvoke((MethodInvoker)delegate
                {
                    _fetching = false;
                    _status = status;
                    if (snap != null) ApplySnapshot(snap);
                    StartScan(); // keep today's Fable spend fresh for the headline banner
                    UpdateHeight();
                    Invalidate();
                });
            });
        }

        void ApplySnapshot(UsageSnapshot snap)
        {
            _usage = snap;
            UpdateHeight();

            var session = snap.Limits.FirstOrDefault(l => l.Kind == "session");
            if (session != null)
            {
                _sessionSamples.Add(new KeyValuePair<DateTime, int>(DateTime.Now, session.Percent));
                var cutoff = DateTime.Now.AddHours(-24);
                _sessionSamples.RemoveAll(s => s.Key < cutoff);
            }
            var weekly = snap.Limits.FirstOrDefault(l => l.Kind == "weekly_all");
            if (weekly != null)
            {
                _weeklySamples.Add(new KeyValuePair<DateTime, int>(DateTime.Now, weekly.Percent));
                var cutoff = DateTime.Now.AddDays(-8);
                _weeklySamples.RemoveAll(s => s.Key < cutoff);
            }
            if (snap.Limits.Count > 0)
            {
                AppendHistory(snap);
                if (!_histDates.Contains(DateTime.Today)) { _histDates.Add(DateTime.Today); RecomputeStreak(); }
            }

            // track the soonest upcoming reset so PollTick can refresh right on time
            var active = snap.Limits.Where(l => l.ResetsAt > DateTimeOffset.Now).ToList();
            _nextReset = active.Count > 0 ? active.Min(l => l.ResetsAt) : DateTimeOffset.MaxValue;
            _resetForced = false;

            int worst = snap.Limits.Count > 0 ? snap.Limits.Max(l => l.Percent) : -1;
            _tray.Icon = MakeTrayIcon(worst);
            // tray tooltip: account + every limit + next reset, all at a glance (max 63 chars)
            var sess = snap.Limits.FirstOrDefault(l => l.Kind == "session");
            string tt = (_account.Length > 0 ? _account.Split('@')[0] + " · " : "")
                + string.Join(" ", snap.Limits.Select(l => (string.IsNullOrEmpty(l.Label) ? "?" : l.Label.Substring(0, 1)) + l.Percent.ToString() + "%").ToArray())
                + (sess != null ? " · reset " + sess.ResetsAt.ToLocalTime().ToString("HH:mm") : "");
            _tray.Text = tt.Length > 63 ? tt.Substring(0, 63) : tt;

            foreach (var l in snap.Limits) MaybeNotify(l);

            // near-full and an emptier account is available → suggest switching
            if (worst >= 90 && (DateTime.Now - _lastSwitchTip).TotalMinutes > 60)
            {
                var alt = _extras.FirstOrDefault(a => a.Snap != null && a.Snap.Limits.Count > 0
                    && a.Snap.Limits.Max(x => x.Percent) < 50);
                if (alt != null)
                {
                    _lastSwitchTip = DateTime.Now;
                    Toast.Pop("บัญชีหลักใกล้เต็ม (" + worst + "%)",
                        "บัญชี " + alt.Label + " ยังว่าง — กด ⚙ → Accounts → สลับใช้", Color.FromArgb(230, 178, 86));
                }
            }
        }

        void MaybeNotify(LimitInfo l)
        {
            string resetsKey = l.ResetsAt.ToString("o");
            string prevReset;
            if (!_notifiedResetsAt.TryGetValue(l.Kind, out prevReset) || prevReset != resetsKey)
            {
                _notifiedResetsAt[l.Kind] = resetsKey;
                _notifiedLevel[l.Kind] = 0;
            }
            // Warn only while the limit is still approachable (75% and 90%).
            // At 100% there is nothing actionable — never toast.
            int level = l.Percent >= 100 ? 0 : l.Percent >= 90 ? 90 : l.Percent >= 75 ? 75 : 0;
            if (level > 0 && _notifiedLevel[l.Kind] < level)
            {
                _notifiedLevel[l.Kind] = level;
                string when = l.ResetsAt.ToLocalTime().ToString("HH:mm") + " (" + L("อีก ", "in ") + Countdown(l.ResetsAt) + ")";
                string title, body;
                switch (_settings.Tone)
                {
                    case 0: // ทางการ
                        title = l.Label + " " + l.Percent + "%";
                        body = L("จะรีเซ็ต ", "resets at ") + when;
                        break;
                    case 2: // ขี้เล่น
                        title = l.Label + " " + l.Percent + L("% แล้วจ้า~", "% already~");
                        body = L("รีเซ็ต ", "resets ") + when + L(" — ใช้ของดีให้คุ้มนะ ☕", " — make it count ☕");
                        break;
                    case 3: // เกรียน
                        title = L("เผาโควต้าเก่งมาก 👏 ", "quota speedrun 👏 ") + l.Label + " " + l.Percent + "%";
                        body = L("รีเซ็ต ", "resets ") + when + L(" — หรือจะลองพักดื่มน้ำสักแก้วดูไหม", " — maybe touch some grass?");
                        break;
                    default: // เป็นมิตร
                        title = l.Label + L(" ใกล้เต็มแล้วนะ (", " getting close (") + l.Percent + "%)";
                        body = L("รีเซ็ต ", "resets ") + when + L(" — วางแผนงานที่เหลือดีๆ นะ", " — plan the rest wisely");
                        break;
                }
                if (_settings.NickName.Length > 0)
                    title = (_settings.Lang == "en" ? "" : "คุณ") + _settings.NickName + " — " + title;
                Toast.Pop(title, body, level >= 90 ? Color.FromArgb(226, 102, 102) : Color.FromArgb(230, 178, 86));
            }

            // reverse alert: lots of weekly quota left and the reset is near — use it
            if (l.Kind == "weekly_all" && l.Percent < 50)
            {
                var hrs = (l.ResetsAt - DateTimeOffset.Now).TotalHours;
                if (hrs > 0 && hrs <= 24 && !_useItNotified.Contains(resetsKey))
                {
                    _useItNotified.Add(resetsKey);
                    string body2 = _settings.Tone >= 2
                        ? L("จะรีเซ็ตใน " + Countdown(l.ResetsAt) + " — โควต้าไม่ทบนะจ๊ะ รีบเผา 🔥",
                            "resets in " + Countdown(l.ResetsAt) + " — quota doesn't roll over, burn it 🔥")
                        : L("จะรีเซ็ตใน " + Countdown(l.ResetsAt) + " — ใช้ให้คุ้มนะ",
                            "resets in " + Countdown(l.ResetsAt) + " — make the most of it");
                    Toast.Pop(L("weekly ยังเหลืออีก ", "weekly still has ") + (100 - l.Percent) + "%", body2,
                        Color.FromArgb(92, 190, 140));
                }
            }
        }

        void AppendHistory(UsageSnapshot snap)
        {
            try
            {
                var values = new StringBuilder();
                foreach (var l in snap.Limits) values.Append(",").Append(l.Kind).Append("=").Append(l.Percent);
                if (values.ToString() == _lastHistoryLine) return; // log only on change
                _lastHistoryLine = values.ToString();
                Directory.CreateDirectory(Path.GetDirectoryName(Settings.HistoryPath));
                File.AppendAllLines(Settings.HistoryPath, new[] { DateTime.Now.ToString("o") + values });
            }
            catch { }
        }

        // ---- history / daily ----

        void LoadHistory()
        {
            try
            {
                if (!File.Exists(Settings.HistoryPath)) return;
                var cutoffW = DateTime.Now.AddDays(-8);
                var cutoffS = DateTime.Now.AddHours(-24);
                foreach (var line in File.ReadAllLines(Settings.HistoryPath))
                {
                    var parts = line.Split(',');
                    DateTime t;
                    if (parts.Length < 2 || !DateTime.TryParse(parts[0], null, DateTimeStyles.RoundtripKind, out t)) continue;
                    _histDates.Add(t.Date);
                    if (t < cutoffW) continue;
                    foreach (var kv in parts.Skip(1))
                    {
                        var eq = kv.Split('=');
                        int v;
                        if (eq.Length != 2 || !int.TryParse(eq[1], out v)) continue;
                        if (eq[0] == "weekly_all") _weeklySamples.Add(new KeyValuePair<DateTime, int>(t, v));
                        if (eq[0] == "session" && t >= cutoffS) _sessionSamples.Add(new KeyValuePair<DateTime, int>(t, v));
                    }
                }
            }
            catch { }
            RecomputeStreak();
        }

        void RecomputeStreak()
        {
            _streak = 0;
            var d = DateTime.Today;
            if (!_histDates.Contains(d)) d = d.AddDays(-1);
            while (_histDates.Contains(d)) { _streak++; d = d.AddDays(-1); }
        }

        // How many percentage points of the weekly quota were consumed since local
        // midnight. Baseline is yesterday's last sample, unless the weekly limit
        // reset today (current < baseline), in which case today's minimum is used.
        int? DailyUsed()
        {
            if (_weeklySamples.Count == 0) return null;
            var today = DateTime.Today;
            var cur = _weeklySamples[_weeklySamples.Count - 1];
            var before = _weeklySamples.Where(s => s.Key < today).ToList();
            var todays = _weeklySamples.Where(s => s.Key >= today).ToList();
            if (todays.Count == 0) return null;
            int baseline = before.Count > 0 && cur.Value >= before[before.Count - 1].Value
                ? before[before.Count - 1].Value
                : todays.Min(s => s.Value);
            return Math.Max(0, cur.Value - baseline);
        }

        // ---- burn rate ----

        string BurnEstimate()
        {
            if (_sessionSamples.Count < 2) return null;
            var now = DateTime.Now;
            var recent = _sessionSamples.Where(s => s.Key > now.AddMinutes(-60)).ToList();
            if (recent.Count < 2) return null;
            var first = recent.First(); var last = recent.Last();
            if (last.Value < first.Value) return null; // reset happened inside the window
            double minutes = (last.Key - first.Key).TotalMinutes;
            if (minutes < 5) return null;
            double slope = (last.Value - first.Value) / minutes; // %/min
            if (slope <= 0.05) return null;
            double minsLeft = (100 - last.Value) / slope;
            var eta = now.AddMinutes(minsLeft);
            var session = _usage != null ? _usage.Limits.FirstOrDefault(l => l.Kind == "session") : null;
            if (session != null && eta > session.ResetsAt.ToLocalTime().DateTime) return null; // resets before it fills
            return L("อัตรานี้ session เต็มราว ", "at this rate session fills ~") + eta.ToString("HH:mm");
        }

        // ---- theming ----
        // Four looks share the same data but render limits very differently:
        //  0 Aurora   = ring gauges on deep blue
        //  1 Cozy     = tall glowing pill bars on indigo, rounded card, star specks
        //  2 Kawaii   = candy pastel, dotted empty track, big rounded caps, sparkles
        //  3 Brutalist= pure black, square thick borders, hatched track, blocky %
        enum Style { Rings, Glow, Candy, Block }
        struct Theme
        {
            public Color Bg1, Bg2, Card, Line, Text, Dim, Accent1, Accent2;
            public bool Sharp;
            public Style Kind;
            public string Title;
        }

        static readonly Theme[] Themes =
        {
            // 0 Aurora — current dark blue, ring gauges
            new Theme { Bg1 = Color.FromArgb(34, 36, 50), Bg2 = Color.FromArgb(21, 22, 30),
                Card = Color.FromArgb(30, 32, 44), Line = Color.FromArgb(55, 55, 70),
                Text = Color.FromArgb(230, 230, 238), Dim = Color.FromArgb(150, 150, 165),
                Accent1 = Color.FromArgb(100, 149, 237), Accent2 = Color.FromArgb(150, 120, 210),
                Sharp = false, Kind = Style.Rings, Title = "Aurora" },
            // 1 Cozy Night — deep violet dusk, tall glowing pill bars
            new Theme { Bg1 = Color.FromArgb(42, 32, 74), Bg2 = Color.FromArgb(22, 18, 44),
                Card = Color.FromArgb(48, 38, 84), Line = Color.FromArgb(78, 64, 120),
                Text = Color.FromArgb(242, 238, 252), Dim = Color.FromArgb(170, 160, 200),
                Accent1 = Color.FromArgb(255, 190, 120), Accent2 = Color.FromArgb(150, 130, 235),
                Sharp = false, Kind = Style.Glow, Title = "Cozy" },
            // 2 Kawaii — soft pink-lilac candy, dotted track, sparkles
            new Theme { Bg1 = Color.FromArgb(48, 34, 58), Bg2 = Color.FromArgb(30, 22, 40),
                Card = Color.FromArgb(58, 42, 70), Line = Color.FromArgb(120, 88, 132),
                Text = Color.FromArgb(252, 240, 250), Dim = Color.FromArgb(210, 180, 210),
                Accent1 = Color.FromArgb(255, 170, 210), Accent2 = Color.FromArgb(170, 210, 255),
                Sharp = false, Kind = Style.Candy, Title = "Kawaii" },
            // 3 Brutalist — pure black, thick borders, punchy blocks
            new Theme { Bg1 = Color.FromArgb(8, 8, 10), Bg2 = Color.FromArgb(8, 8, 10),
                Card = Color.FromArgb(18, 18, 22), Line = Color.FromArgb(210, 210, 215),
                Text = Color.FromArgb(245, 245, 245), Dim = Color.FromArgb(140, 140, 148),
                Accent1 = Color.FromArgb(245, 214, 62), Accent2 = Color.FromArgb(122, 92, 236),
                Sharp = true, Kind = Style.Block, Title = "Brutalist" },
        };

        Theme T { get { return Themes[Math.Max(0, Math.Min(3, _settings.Theme))]; } }

        // brutalist punch colours by band (yellow/cyan/coral), else pace colours
        Color LimitColor(LimitInfo l)
        {
            if (_settings.Theme == 3)
                return l.Percent >= 60 ? Color.FromArgb(245, 214, 62)
                     : l.Percent >= 30 ? Color.FromArgb(64, 224, 208)
                     : Color.FromArgb(240, 110, 100);
            return PaceColor(l);
        }

        // ---- painting ----

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var th = T;

            if (th.Sharp)
                using (var bg = new SolidBrush(th.Bg1)) g.FillRectangle(bg, ClientRectangle);
            else
                using (var bg = new LinearGradientBrush(ClientRectangle, th.Bg1, th.Bg2, 90f))
                    g.FillRectangle(bg, ClientRectangle);
            using (var pen = new Pen(th.Line, th.Sharp ? 2f : 1f))
                if (th.Sharp) g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
                else g.DrawPath(pen, RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 12));

            if (_settings.Mini)
            {
                _btnClose = _btnRefresh = _btnPin = _btnDetails = _btnMenu = _sparkRect = Rectangle.Empty;
                _btnMin = new Rectangle(Width - 22, 13, 14, 14);
                DrawGlyph(g, _btnMin, "⛶", TextDim);
                Icons.Claude(g, 14, 20, 7f, Icons.Coral);
                int bx = 26;
                if (_usage != null)
                    foreach (var l in _usage.Limits)
                    {
                        if (l.Kind != "session" && l.Kind != "weekly_all") continue;
                        var c = PaceColor(l);
                        TextRenderer.DrawText(g, l.Kind == "session" ? "5h" : "7d", FSmall, new Point(bx, 6), TextDim);
                        var pct = l.Percent + "%";
                        TextRenderer.DrawText(g, pct, FLabel, new Point(bx + 18, 5), c);
                        var bar = new Rectangle(bx + 2, 24, 58, 5);
                        using (var back = new SolidBrush(Color.FromArgb(44, 46, 60)))
                        using (var p = RoundedRect(bar, 2)) g.FillPath(back, p);
                        int bw = (int)(bar.Width * Math.Min(100, l.Percent) / 100.0);
                        if (bw > 2)
                            using (var fill = new SolidBrush(c))
                            using (var p = RoundedRect(new Rectangle(bar.X, bar.Y, bw, bar.Height), 2))
                                g.FillPath(fill, p);
                        var ef = ElapsedFrac(l);
                        if (ef != null)
                            using (var tick = new SolidBrush(Color.FromArgb(200, 235, 235, 245)))
                                g.FillRectangle(tick, bar.X + (int)(bar.Width * ef.Value), bar.Y - 1, 1, bar.Height + 2);
                        bx += 74;
                    }
                else TextRenderer.DrawText(g, "loading…", FSmall, new Point(bx, 13), TextDim);
                if (IsActive)
                    using (var dot = new SolidBrush(Color.FromArgb(80, 200, 120)))
                        g.FillEllipse(dot, Width - 34, 17, 6, 6);
                return; // ⛶ or double-click to expand
            }

            int y = 12;
            // header: Claude sunburst + title + plan badge
            Icons.Claude(g, 20, y + 8, 8f, Icons.Coral);
            TextRenderer.DrawText(g, "Claude Usage", FTitle, new Point(28, y), th.Text);
            if (!string.IsNullOrEmpty(_plan))
            {
                var sz = TextRenderer.MeasureText(_plan, FSmall);
                var badge = new Rectangle(28 + TextRenderer.MeasureText("Claude Usage", FTitle).Width + 6, y + 1, sz.Width + 10, 16);
                if (th.Sharp)
                {
                    using (var b = new SolidBrush(th.Accent2)) g.FillRectangle(b, badge);
                    TextRenderer.DrawText(g, _plan, FSmall, badge, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    using (var b = new LinearGradientBrush(badge, Color.FromArgb(80, 100, 190), Color.FromArgb(120, 80, 200), 0f))
                    using (var p = RoundedRect(badge, 8)) g.FillPath(b, p);
                    TextRenderer.DrawText(g, _plan, FSmall, badge, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            // live account (so switching between accounts is always visible)
            if (!string.IsNullOrEmpty(_account))
            {
                bool extra = _extras.Count > 0;
                TextRenderer.DrawText(g, "👤 " + _account + (extra ? L("  · หลายบัญชี", "  · multi") : ""),
                    FSmall, new Point(28, 30), extra ? Color.FromArgb(230, 178, 86) : TextDim);
            }

            using (var accent = new LinearGradientBrush(new Rectangle(14, 46, Width - 28, 2),
                Color.FromArgb(170, 100, 149, 237), Color.FromArgb(0, 100, 149, 237), 0f))
                g.FillRectangle(accent, 14, 46, Width - 28, 2);

            // quota-meteorology headline: when does the next window reset, and at what time?
            if (_usage != null && _usage.Limits.Count > 0)
            {
                var hl = _usage.Limits.FirstOrDefault(x => x.Kind == "session") ?? _usage.Limits[0];
                var pc = PaceColor(hl);
                string icon = pc.R < 150 ? "☀" : pc.R > 200 && pc.G < 140 ? "⛈" : "⛅";
                string at = hl.ResetsAt.ToLocalTime().ToString("HH:mm");
                string head = icon + "  " + hl.Label + L(" reset ", " resets ") + at
                    + " (" + L("อีก ", "in ") + Countdown(hl.ResetsAt) + ")";
                TextRenderer.DrawText(g, head, FLabel, new Rectangle(14, 52, Width - 28, 18), pc,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            _btnClose = new Rectangle(Width - 26, y, 16, 16);
            _btnMin = new Rectangle(Width - 46, y, 16, 16);
            _btnMenu = new Rectangle(Width - 66, y, 16, 16);
            _btnRefresh = new Rectangle(Width - 86, y, 16, 16);
            _btnPin = new Rectangle(Width - 106, y, 16, 16);
            DrawGlyph(g, _btnClose, "✕", TextDim);
            DrawGlyph(g, _btnMin, "—", TextDim);
            DrawGlyph(g, _btnMenu, "⚙", TextDim);
            DrawGlyph(g, _btnRefresh, "⟳", _fetching ? Color.CornflowerBlue : TextDim);
            DrawGlyph(g, _btnPin, "📌", TopMost ? Color.Gold : TextDim);

            y = 76; // rows start below the account line + reset headline
            if (_usage != null)
            {
                // main account rendered per theme
                if (_settings.Theme == 0) { DrawRings(g, _usage.Limits, y); y += 96; }
                else { DrawThemedBars(g, _usage.Limits, y); y += Math.Min(3, _usage.Limits.Count) * 52; }

                // headline metric: today's Fable spend — Fable is metered ($10/$50)
                // for Pro from 20 Jul 2026, so this is the number that matters most
                if (_stats != null && _stats.FableCost > 0)
                {
                    var fb = new Rectangle(12, y, Width - 24, 26);
                    using (var bg = new LinearGradientBrush(fb, Color.FromArgb(60, 226, 140, 80), Color.FromArgb(30, 226, 140, 80), 0f))
                    using (var p = RoundedRect(fb, 7)) g.FillPath(bg, p);
                    using (var pen = new Pen(Color.FromArgb(120, 226, 140, 80)))
                    using (var p = RoundedRect(fb, 7)) g.DrawPath(pen, p);
                    Icons.Claude(g, 26, y + 13, 7f, Icons.Coral);
                    TextRenderer.DrawText(g, L("วันนี้ใช้ Fable ไป", "Fable used today"), FLabel,
                        new Point(38, y + 6), Color.FromArgb(240, 200, 160));
                    string amt = "$" + _stats.FableCost.ToString("0.00");
                    var asz = TextRenderer.MeasureText(amt, FPct);
                    TextRenderer.DrawText(g, amt, FPct, new Point(Width - 24 - asz.Width, y + 4),
                        Color.FromArgb(255, 190, 120));
                    y += 32;
                }

                foreach (var acc in _extras)
                {
                    using (var pen = new Pen(CardLine)) g.DrawLine(pen, 14, y + 2, Width - 14, y + 2);
                    TextRenderer.DrawText(g, "บัญชี: " + acc.Label + (acc.Plan.Length > 0 ? " · " + acc.Plan : ""),
                        FSmall, new Point(14, y + 6), Color.CornflowerBlue);
                    y += 22;
                    if (acc.Snap != null)
                        foreach (var l in acc.Snap.Limits) { DrawLimitRow(g, l, y); y += 46; }
                    else
                    {
                        TextRenderer.DrawText(g, acc.Err ?? "รอข้อมูล…", FSmall, new Point(14, y), TextDim);
                        y += 16;
                    }
                }

                // sparkline of session % over last 24h (click to open the 7-day graph)
                var spark = new Rectangle(14, y + 4, 90, 16);
                _sparkRect = new Rectangle(spark.X, spark.Y - 2, spark.Width + 26, spark.Height + 4);
                DrawSparkline(g, spark);
                string burn = _usage.Limits.Any(l => l.Kind == "session") ? BurnEstimate() : null;
                if (burn != null)
                    TextRenderer.DrawText(g, burn, FSmall, new Point(spark.Right + 28, y + 5), Color.FromArgb(220, 180, 90));
                y += 24;

                int? daily = DailyUsed();
                if (daily.HasValue)
                    TextRenderer.DrawText(g, L("วันนี้ใช้ weekly ไป +", "today used +") + daily.Value + "%",
                        FSmall, new Point(14, y + 2), TextDim);

                string toggle = L("รายละเอียด ", "Details ") + (_detailsOpen ? "▴" : "▾");
                var tsz = TextRenderer.MeasureText(toggle, FSmall);
                _btnDetails = new Rectangle(Width - 14 - tsz.Width, y, tsz.Width, 16);
                TextRenderer.DrawText(g, toggle, FSmall, _btnDetails, Color.CornflowerBlue,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                string gtoggle = L("กิจกรรม ", "Activity ") + (_graphOpen ? "▴" : "▾");
                var gsz = TextRenderer.MeasureText(gtoggle, FSmall);
                _btnGraph = new Rectangle(_btnDetails.X - 12 - gsz.Width, y, gsz.Width, 16);
                TextRenderer.DrawText(g, gtoggle, FSmall, _btnGraph, Color.CornflowerBlue,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                y += 18;

                if (_graphOpen)
                {
                    DrawActivityPanel(g, new Rectangle(14, y + 2, Width - 28, 297));
                    y += 305;
                }

                if (_detailsOpen)
                {
                    using (var pen = new Pen(CardLine)) g.DrawLine(pen, 14, y + 2, Width - 14, y + 2);
                    y += 7;
                    if (_scanning || _stats == null)
                        TextRenderer.DrawText(g, "กำลังสแกน transcripts ในเครื่อง…", FSmall, new Point(14, y), TextDim);
                    else
                    {
                        TextRenderer.DrawText(g, L("วันนี้: in ", "today: in ") + LocalScanner.FmtTok(_stats.InTok)
                            + " · out " + LocalScanner.FmtTok(_stats.OutTok)
                            + " · cache " + LocalScanner.FmtTok(_stats.CacheTok), FSmall, new Point(14, y), TextMain);
                        TextRenderer.DrawText(g, _stats.Sessions + " sessions · "
                            + L("มูลค่าวันนี้ ≈ $", "today's value ≈ $") + _stats.Cost.ToString("0.00")
                            + L(" (ราคา API ตามโมเดลจริง)", " (API pricing, per model)"),
                            FSmall, new Point(14, y + 16), Color.FromArgb(120, 200, 150));
                        TextRenderer.DrawText(g, L("โมเดล: ", "models: ") + (_stats.Models.Length > 0 ? _stats.Models : "—"),
                            FSmall, new Point(14, y + 32), TextMain);
                        TextRenderer.DrawText(g, "skills: " + (_stats.Skills.Length > 0 ? _stats.Skills : "—"),
                            FSmall, new Point(14, y + 48), TextDim);
                        TextRenderer.DrawText(g, "projects: " + (_stats.Projects.Length > 0 ? _stats.Projects : "—"),
                            FSmall, new Point(14, y + 64), TextDim);
                    }
                    y += 84;
                }
            }

            int statusX = 14;
            if (IsActive)
            {
                using (var dot = new SolidBrush(Color.FromArgb(80, 200, 120)))
                    g.FillEllipse(dot, 14, Height - 17, 6, 6);
                statusX = 24;
            }
            TextRenderer.DrawText(g, _status, FSmall, new Point(statusX, Height - 22), TextDim);
            DrawPets(g);

            // in-panel hover label — reliable where a ToolTip flickers on a borderless form
            if (_hoverText.Length > 0)
            {
                var sz = TextRenderer.MeasureText(_hoverText, FSmall);
                int tx = Math.Min(_hoverAt.X + 12, Width - sz.Width - 12);
                int tyy = Math.Max(4, _hoverAt.Y - sz.Height - 8);
                var box = new Rectangle(tx - 5, tyy - 3, sz.Width + 10, sz.Height + 6);
                using (var b = new SolidBrush(Color.FromArgb(245, 18, 19, 26)))
                using (var p = RoundedRect(box, 5)) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(90, 94, 120)))
                using (var p = RoundedRect(box, 5)) g.DrawPath(pen, p);
                TextRenderer.DrawText(g, _hoverText, FSmall, new Point(tx, tyy), Color.FromArgb(235, 235, 243));
            }
        }

        // Pixel pets, drawn from character maps — our own art, no external assets.
        // Any combination can be enabled from the tray menu. Every pet's mood
        // follows the worst usage percent: relaxed < 60, sweating >= 60,
        // panicking (shaking, two drops) >= 85.
        class Pet
        {
            public string Id, Name;
            public string[] Map;
            public Dictionary<char, Color> Pal;
            public int WidthPx { get { return Map.Max(r => r.Length) * 2; } }
        }

        static readonly Pet[] AllPets = new[]
        {
            new Pet { Id = "capy", Name = "Capybara", Map = new[]
            {
                "........g...............",
                ".......yyy..............",
                ".......yyy..............",
                "...obboyyy.obbo.........",
                "..obbbbbbbbbbbbooooo....",
                ".obbbbbbbbbbbbbbbboo....",
                ".olebbbbbbbbbbbbbbbbo...",
                "ollllbbbbbbbbbbbbbbbbo..",
                "onlllbbbbbbbbbbbbbbbbo..",
                "ollllbbbbbbbbbbbbbbbbo..",
                ".obbbbbbbbbbbbbbbbbbbo..",
                ".obbddbbbbbbbbddbbbdbo..",
                ".obbo..obbbbbo..obbo....",
                ".oo.....oo.......oo.....",
            }, Pal = new Dictionary<char, Color> {
                { 'o', Color.FromArgb(58, 40, 26) }, { 'b', Color.FromArgb(176, 127, 82) },
                { 'l', Color.FromArgb(203, 158, 109) }, { 'd', Color.FromArgb(146, 102, 62) },
                { 'e', Color.FromArgb(28, 22, 16) }, { 'n', Color.FromArgb(64, 44, 30) },
                { 'y', Color.FromArgb(235, 155, 40) }, { 'g', Color.FromArgb(96, 158, 72) },
            } },
            new Pet { Id = "cat", Name = "แมวส้ม", Map = new[]
            {
                "..oo.....oo.........",
                "..obo...obo.........",
                ".obbbbbbbbbo........",
                ".obebbbbebbo........",
                "olbbbnbbbbbo........",
                ".obbbbbbbbbboooo....",
                "..obbbbbbbbbbbbbo.ob",
                "..obbbbbbbbbbbbbboob",
                "..obbbbbbbbbbbbbbob.",
                "..obbbbbbbbbbbbbo...",
                "..obbo..obbo..obo...",
                "..oo.....oo....oo...",
            }, Pal = new Dictionary<char, Color> {
                { 'o', Color.FromArgb(70, 45, 25) }, { 'b', Color.FromArgb(226, 150, 70) },
                { 'l', Color.FromArgb(245, 220, 180) }, { 'd', Color.FromArgb(190, 115, 45) },
                { 'e', Color.FromArgb(35, 28, 20) }, { 'n', Color.FromArgb(215, 110, 120) },
            } },
            new Pet { Id = "duck", Name = "เป็ด", Map = new[]
            {
                "....oooo..........",
                "...obbbbo.........",
                "...obebbo.........",
                ".yyobbbbo.........",
                "..yobbbbo.........",
                "...obbbbbooooo....",
                "...obbbbbbbbbbo...",
                "..obbbbdddbbbbbo..",
                "..obbbbbbbbbbbo...",
                "...oobbbbbbboo....",
                ".....yy...yy......",
            }, Pal = new Dictionary<char, Color> {
                { 'o', Color.FromArgb(120, 95, 35) }, { 'b', Color.FromArgb(242, 208, 85) },
                { 'd', Color.FromArgb(212, 172, 58) }, { 'e', Color.FromArgb(30, 26, 20) },
                { 'y', Color.FromArgb(235, 140, 50) },
            } },
        };

        List<Pet> SelectedPets()
        {
            var ids = (_settings.Pets ?? "").Split(',');
            return AllPets.Where(p => ids.Contains(p.Id)).ToList();
        }

        Image _petImg; // single user image; the widget applies the mood itself
        PetOverlay _petOver;

        // keep the perched pet glued to the widget's top edge
        void SyncPetOverlay()
        {
            bool want = _petImg != null && Visible && !_settings.HidePet;
            if (!want)
            {
                if (_petOver != null && _petOver.Visible) _petOver.Hide();
                return;
            }
            if (_petOver == null) _petOver = new PetOverlay { Owner = this };
            int worst = _usage != null && _usage.Limits.Count > 0 ? _usage.Limits.Max(l => l.Percent) : 0;
            int mood = worst >= 85 ? 2 : worst >= 60 ? 1 : 0;
            // stands to the LEFT of the widget, outside the card, bigger; shows in pill too
            int h = _settings.Mini ? 48 : 78;
            int w = Math.Min(_settings.Mini ? 110 : 160, _petImg.Width * h / Math.Max(1, _petImg.Height));
            int[] bob = { 0, -2, -3, -2 };
            int by = bob[(_mascotFrame / (mood == 2 ? 1 : mood == 1 ? 2 : 3)) % 4];
            int bx = mood == 2 ? (_mascotFrame % 2 == 0 ? 2 : -2) : 0;
            _petOver.Set(_petImg, mood, _mascotFrame);
            // sit just off the widget's left edge, vertically centred on the header
            int px = Left - w + 8;
            int wa = Screen.PrimaryScreen.WorkingArea.Left;
            if (px < wa) px = Left + Width - 8; // no room on the left → hop to the right side
            _petOver.Bounds = new Rectangle(px + bx, Top + (_settings.Mini ? 0 : 6) + by, w, h);
            _petOver.TopMost = TopMost;
            if (!_petOver.Visible) _petOver.Show();
        }
        static string PetImgDir { get { return System.IO.Path.Combine(Settings.Dir, "pet"); } }

        void LoadPetImages()
        {
            if (_petImg != null) { _petImg.Dispose(); _petImg = null; }
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif" })
            {
                var p = System.IO.Path.Combine(PetImgDir, "pet" + ext);
                if (File.Exists(p))
                {
                    // clone into a memory bitmap — Image.FromStream needs its stream
                    // alive for the image's lifetime, and Bitmap(path) locks the file
                    try { using (var tmp = new Bitmap(p)) _petImg = new Bitmap(tmp); } catch { }
                    break;
                }
            }
        }

        void DrawPets(Graphics g)
        {
            if (_settings.HidePet) return;
            int worst = _usage != null && _usage.Limits.Count > 0 ? _usage.Limits.Max(l => l.Percent) : 0;

            // a custom image perches on the TOP edge via the PetOverlay window instead
            if (_petImg != null) return;

            int x = Width - 10;
            int i = 0;
            foreach (var p in Enumerable.Reverse(SelectedPets()))
            {
                x -= p.WidthPx + 6;
                DrawPet(g, p, x, Height - 6 - p.Map.Length * 2, _mascotFrame + i * 4, worst);
                i++;
            }

            if (_streak >= 2 && i > 0)
            {
                // pixel flame + consecutive-day streak, pets' little reward
                int fx = x - 26, fy = Height - 20;
                using (var oj = new SolidBrush(Color.FromArgb(235, 140, 50)))
                using (var yl = new SolidBrush(Color.FromArgb(250, 210, 90)))
                {
                    g.FillRectangle(oj, fx + 2, fy - 6, 2, 2);
                    g.FillRectangle(oj, fx, fy - 4, 6, 4);
                    g.FillRectangle(yl, fx + 2, fy - 2, 2, 2);
                }
                TextRenderer.DrawText(g, "×" + _streak, FSmall, new Point(fx + 8, fy - 8), Color.FromArgb(235, 140, 50));
            }
        }

        static void DrawPet(Graphics g, Pet p, int x, int y, int frame, int worst)
        {
            int speed = worst >= 85 ? 1 : worst >= 60 ? 2 : 3;
            int[] bob = { 0, -1, -2, -1 };
            y += bob[(frame / speed) % 4];
            if (worst >= 85) x += (frame % 2 == 0) ? 1 : -1; // panic shake
            bool blink = frame % 26 == 0;
            const int s = 2;

            for (int r = 0; r < p.Map.Length; r++)
                for (int c = 0; c < p.Map[r].Length; c++)
                {
                    char ch = p.Map[r][c];
                    if (ch == '.') continue;
                    if (ch == 'e' && blink) ch = 'b';
                    Color col;
                    if (!p.Pal.TryGetValue(ch, out col)) continue;
                    using (var br = new SolidBrush(col))
                        g.FillRectangle(br, x + c * s, y + r * s, s, s);
                }

            if (worst >= 60)
                using (var bSweat = new SolidBrush(Color.FromArgb(110, 180, 240)))
                {
                    int fall = (frame % 4) * s;
                    int dropX = x + p.WidthPx - 5 * s;
                    g.FillRectangle(bSweat, dropX, y + 2 * s + fall, s, s * 2);
                    if (worst >= 85)
                        g.FillRectangle(bSweat, dropX + 3 * s, y + 5 * s + fall, s, s * 2);
                }
        }

        // Fraction of the limit window that has already elapsed (0..1), from the
        // known window lengths: session = 5h, weekly = 7d.
        static double? ElapsedFrac(LimitInfo l)
        {
            double windowH = l.Kind == "session" ? 5 : 7 * 24;
            var remain = (l.ResetsAt - DateTimeOffset.Now).TotalHours;
            if (remain < 0 || remain > windowH) return null;
            return 1 - remain / windowH;
        }

        // Pace-aware color: compares usage against elapsed time, so burning quota
        // faster than the clock turns yellow/red even at low percentages.
        static Color PaceColor(LimitInfo l)
        {
            if (l.Percent >= 90) return Color.FromArgb(226, 102, 102);
            var ef = ElapsedFrac(l);
            if (ef == null || ef.Value < 0.03) return BarColor(l.Percent);
            double ratio = l.Percent / (ef.Value * 100);
            if (ratio <= 1.0) return Color.FromArgb(92, 190, 140);   // ahead of the clock
            if (ratio <= 1.6) return Color.FromArgb(230, 178, 86);   // faster than the clock
            return Color.FromArgb(226, 102, 102);                    // much faster — economize
        }

        // Three ring gauges across the card — our signature metric view.
        void DrawRings(Graphics g, List<LimitInfo> limits, int top)
        {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int n = Math.Min(3, limits.Count);
            int colW = (Width - 28) / Math.Max(1, n);
            for (int i = 0; i < n; i++)
            {
                var l = limits[i];
                var c = PaceColor(l);
                int cx = 14 + colW * i + colW / 2;
                int cy = top + 34;
                int rad = 27, th = 7;
                var box = new Rectangle(cx - rad, cy - rad, rad * 2, rad * 2);
                using (var pen = new Pen(Color.FromArgb(44, 46, 60), th)) g.DrawArc(pen, box, 0, 360);
                float sweep = Math.Min(100, l.Percent) / 100f * 360f;
                using (var pen = new Pen(c, th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    if (sweep > 0) g.DrawArc(pen, box, -90, sweep);
                TextRenderer.DrawText(g, l.Percent + "%", FPct, box, c,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, l.Label, FLabel, new Rectangle(cx - colW / 2, cy + rad + 2, colW, 14),
                    TextDim, TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, L("อีก ", "in ") + Countdown(l.ResetsAt), FSmall,
                    new Rectangle(cx - colW / 2, cy + rad + 16, colW, 12), TextDim, TextFormatFlags.HorizontalCenter);
            }
            g.SmoothingMode = sm;
        }

        static void Sparkle(Graphics g, int x, int y, int r, Color c)
        {
            using (var b = new SolidBrush(c))
                g.FillPolygon(b, new[]
                {
                    new Point(x, y - r), new Point(x + r / 3, y - r / 3), new Point(x + r, y),
                    new Point(x + r / 3, y + r / 3), new Point(x, y + r),
                    new Point(x - r / 3, y + r / 3), new Point(x - r, y), new Point(x - r / 3, y - r / 3),
                });
        }

        // One limit "card" per theme style — genuinely different silhouettes.
        void DrawThemedBars(Graphics g, List<LimitInfo> limits, int top)
        {
            var th = T;
            int n = Math.Min(3, limits.Count);
            for (int i = 0; i < n; i++)
            {
                var l = limits[i];
                var c = LimitColor(l);
                int y = top + i * 52;
                var card = new Rectangle(12, y, Width - 24, 46);

                // card background
                if (th.Kind == Style.Block)
                {
                    using (var b = new SolidBrush(th.Card)) g.FillRectangle(b, card);
                    using (var pen = new Pen(th.Line, 2f)) g.DrawRectangle(pen, card);
                }
                else
                {
                    int rad = th.Kind == Style.Candy ? 14 : 10;
                    using (var b = new SolidBrush(th.Card))
                    using (var p = RoundedRect(card, rad)) g.FillPath(b, p);
                    if (th.Kind == Style.Candy) // pastel outline + corner sparkle
                    {
                        using (var pen = new Pen(Color.FromArgb(90, c), 1.5f))
                        using (var p = RoundedRect(card, rad)) g.DrawPath(pen, p);
                        Sparkle(g, card.Right - 12, y + 10, 4, Color.FromArgb(200, th.Accent1));
                    }
                }

                TextRenderer.DrawText(g, l.Label, FLabel, new Point(26, y + 6), th.Text);
                string reset = L("reset ใน ", "resets in ") + Countdown(l.ResetsAt);
                var rsz = TextRenderer.MeasureText(reset, FSmall);
                TextRenderer.DrawText(g, reset, FSmall, new Point(Width - 26 - 46 - rsz.Width, y + 7), th.Dim);
                TextRenderer.DrawText(g, l.Percent + "%", FPct, new Point(Width - 26 - 44, y + 3), c);

                int pct = (int)Math.Min(100, l.Percent);
                var ef = ElapsedFrac(l);

                if (th.Kind == Style.Block)
                {
                    var bar = new Rectangle(26, y + 26, Width - 52 - 8, 11);
                    using (var hp = new HatchBrush(HatchStyle.LightUpwardDiagonal, Color.FromArgb(52, 52, 58), Color.FromArgb(26, 26, 32)))
                        g.FillRectangle(hp, bar);
                    int bw = bar.Width * pct / 100;
                    if (bw > 1) using (var fill = new SolidBrush(c)) g.FillRectangle(fill, bar.X, bar.Y, bw, bar.Height);
                    using (var pen = new Pen(th.Line, 1.5f)) g.DrawRectangle(pen, bar);
                    if (ef != null) using (var t2 = new SolidBrush(Color.White))
                        g.FillRectangle(t2, bar.X + (int)(bar.Width * ef.Value), bar.Y - 2, 2, bar.Height + 4);
                }
                else if (th.Kind == Style.Candy)
                {
                    var bar = new Rectangle(26, y + 27, Width - 52 - 8, 8);
                    // dotted empty track
                    using (var dot = new SolidBrush(Color.FromArgb(70, 62, 82)))
                        for (int dx = bar.X; dx < bar.Right - 3; dx += 8) g.FillEllipse(dot, dx, bar.Y + 1, 5, 5);
                    int bw = bar.Width * pct / 100;
                    if (bw > 6)
                        using (var fill = new LinearGradientBrush(new Rectangle(bar.X, bar.Y, bw, bar.Height), Lighten(c), c, 0f))
                        using (var p = RoundedRect(new Rectangle(bar.X, bar.Y, bw, bar.Height), 4)) g.FillPath(fill, p);
                    if (ef != null) using (var t2 = new SolidBrush(Color.FromArgb(230, th.Text)))
                        g.FillEllipse(t2, bar.X + (int)(bar.Width * ef.Value) - 2, bar.Y - 1, 4, bar.Height + 2);
                }
                else // Glow (Cozy): thick pill + soft outer glow
                {
                    var bar = new Rectangle(26, y + 26, Width - 52 - 8, 10);
                    using (var back = new SolidBrush(Color.FromArgb(60, 52, 92)))
                    using (var p = RoundedRect(bar, 5)) g.FillPath(back, p);
                    int bw = bar.Width * pct / 100;
                    if (bw > 5)
                    {
                        var fr = new Rectangle(bar.X, bar.Y, bw, bar.Height);
                        using (var glow = new SolidBrush(Color.FromArgb(70, c)))
                        using (var gp = RoundedRect(new Rectangle(fr.X - 1, fr.Y - 2, fr.Width + 2, fr.Height + 4), 7)) g.FillPath(glow, gp);
                        using (var fill = new LinearGradientBrush(fr, Lighten(c), Darken(c), 90f))
                        using (var p = RoundedRect(fr, 5)) g.FillPath(fill, p);
                    }
                    if (ef != null) using (var t2 = new SolidBrush(Color.FromArgb(230, 235, 235, 245)))
                        g.FillRectangle(t2, bar.X + (int)(bar.Width * ef.Value), bar.Y - 2, 1, bar.Height + 4);
                }
            }
        }

        void DrawLimitRow(Graphics g, LimitInfo l, int y)
        {
            TextRenderer.DrawText(g, l.Label, FLabel, new Point(14, y), TextDim);
            string reset = L("reset ใน ", "resets in ") + Countdown(l.ResetsAt);
            var rsz = TextRenderer.MeasureText(reset, FSmall);
            TextRenderer.DrawText(g, reset, FSmall, new Point(Width - 14 - rsz.Width, y + 1), TextDim);

            var barRect = new Rectangle(14, y + 16, Width - 28 - 50, 11);
            using (var back = new SolidBrush(Color.FromArgb(44, 46, 60)))
            using (var p = RoundedRect(barRect, 5)) g.FillPath(back, p);
            int w = (int)(barRect.Width * Math.Min(100, l.Percent) / 100.0);
            var c = PaceColor(l);
            if (w > 4)
            {
                var fillRect = new Rectangle(barRect.X, barRect.Y, w, barRect.Height);
                using (var fill = new LinearGradientBrush(fillRect, Darken(c), Lighten(c), 0f))
                using (var p = RoundedRect(fillRect, 5))
                    g.FillPath(fill, p);
            }
            var ef = ElapsedFrac(l);
            if (ef != null)
            {
                // white tick = where the clock is; fill past the tick means overspending
                int tx = barRect.X + (int)(barRect.Width * ef.Value);
                using (var tick = new SolidBrush(Color.FromArgb(210, 235, 235, 245)))
                    g.FillRectangle(tick, tx, barRect.Y - 2, 2, barRect.Height + 4);
            }
            TextRenderer.DrawText(g, l.Percent + "%", FPct,
                new Point(barRect.Right + 6, y + 11), c);
        }

        void StartActivityScan()
        {
            if (_actScanning) return;
            if (_activity != null && (DateTime.Now - _activityAt).TotalMinutes < 10) return;
            _actScanning = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var st = LocalScanner.ScanActivity();
                BeginInvoke((MethodInvoker)delegate
                {
                    _activity = st; _activityAt = DateTime.Now; _actScanning = false;
                    Invalidate();
                });
            });
        }

        static Color HeatColor(long v, long max)
        {
            if (v == 0) return Color.FromArgb(40, 42, 56);
            double f = max > 0 ? (double)v / max : 0;
            return f > 0.75 ? Color.FromArgb(150, 190, 255)
                 : f > 0.45 ? Color.FromArgb(100, 150, 237)
                 : f > 0.15 ? Color.FromArgb(75, 115, 195)
                 : Color.FromArgb(55, 85, 145);
        }

        // Codex-style token-activity heatmap over the last ~30 days with day/date
        // axes and a density legend, plus models and heaviest projects.
        void DrawActivityPanel(Graphics g, Rectangle r)
        {
            using (var b = new SolidBrush(Color.FromArgb(28, 30, 42)))
            using (var p = RoundedRect(r, 6)) g.FillPath(b, p);
            TextRenderer.DrawText(g, L("TOKEN ACTIVITY · 30 วัน", "TOKEN ACTIVITY · 30 days"),
                FSmall, new Point(r.X + 8, r.Y + 5), TextDim);

            // density legend
            int lx = r.Right - 96;
            TextRenderer.DrawText(g, L("น้อย", "less"), FSmall, new Point(lx - 26, r.Y + 5), TextDim);
            long[] samples = { 0, 1, 5, 8, 10 };
            for (int i = 0; i < 5; i++)
                using (var b = new SolidBrush(HeatColor(samples[i], 10)))
                    g.FillRectangle(b, lx + i * 12, r.Y + 6, 9, 9);
            TextRenderer.DrawText(g, L("มาก", "more"), FSmall, new Point(lx + 62, r.Y + 5), TextDim);

            if (_actScanning || _activity == null)
            {
                TextRenderer.DrawText(g, L("กำลังสแกน…", "scanning…"), FSmall, new Point(r.X + 8, r.Y + 24), TextDim);
                return;
            }

            const int cellH = 15, gap = 3, weeks = 5;
            int gx = r.X + 30, gy = r.Y + 24;
            int cellW = (r.Width - 38 - (weeks - 1) * gap) / weeks; // stretch across the panel
            var thisMonday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));

            // day-of-week axis (rows)
            string[] dayNames = _settings.Lang == "en"
                ? new[] { "M", "", "W", "", "F", "", "Su" }
                : new[] { "จ", "", "พ", "", "ศ", "", "อา" };
            for (int d = 0; d < 7; d++)
                if (dayNames[d].Length > 0)
                    TextRenderer.DrawText(g, dayNames[d], FSmall,
                        new Point(r.X + 8, gy + d * (cellH + gap) + 1), TextDim);

            for (int w = 0; w < weeks; w++)
            {
                var colMonday = thisMonday.AddDays(-(weeks - 1 - w) * 7);
                for (int d = 0; d < 7; d++)
                {
                    var day = colMonday.AddDays(d);
                    if (day > DateTime.Today) continue;
                    long v; _activity.PerDay.TryGetValue(day, out v);
                    using (var b = new SolidBrush(HeatColor(v, _activity.MaxDay)))
                        g.FillRectangle(b, gx + w * (cellW + gap), gy + d * (cellH + gap), cellW, cellH);
                    if (day == DateTime.Today)
                        using (var pen = new Pen(Color.FromArgb(226, 140, 80)))
                            g.DrawRectangle(pen, gx + w * (cellW + gap), gy + d * (cellH + gap), cellW - 1, cellH - 1);
                }
                // date axis (columns): the Monday each week starts on
                TextRenderer.DrawText(g, colMonday.ToString("d/M"), FSmall,
                    new Point(gx + w * (cellW + gap), gy + 7 * (cellH + gap) + 2), TextDim);
            }

            // axis border lines
            using (var pen = new Pen(Color.FromArgb(58, 61, 82)))
            {
                g.DrawLine(pen, gx - 4, gy - 2, gx - 4, gy + 7 * (cellH + gap) - 2);
                g.DrawLine(pen, gx - 4, gy + 7 * (cellH + gap), gx + weeks * (cellW + gap), gy + 7 * (cellH + gap));
            }

            _hmGX = gx; _hmGY = gy; _hmCellW = cellW; _hmCellH = cellH; // for hover tooltip

            int ty = gy + 7 * (cellH + gap) + 18;
            TextRenderer.DrawText(g, L("โมเดล 7 วัน:  ", "Models 7d:  ")
                + (_activity.Models7d.Length > 0 ? _activity.Models7d : "—"),
                FSmall, new Point(r.X + 8, ty), TextMain);
            TextRenderer.DrawText(g, L("มูลค่า 7 วัน ≈ $", "7d value ≈ $") + _activity.Cost7.ToString("0.00")
                + L(" (ราคา API ตามโมเดลจริง)", " (API pricing, per model)"),
                FSmall, new Point(r.X + 8, ty + 15), Color.FromArgb(120, 200, 150));
            TextRenderer.DrawText(g, L("7 วัน:  ", "7d:  ") + (_activity.Top7d.Length > 0 ? _activity.Top7d : "—"),
                FSmall, new Point(r.X + 8, ty + 30), TextMain);
            TextRenderer.DrawText(g, L("วันนี้:  ", "today:  ") + (_activity.Top1d.Length > 0 ? _activity.Top1d : "—"),
                FSmall, new Point(r.X + 8, ty + 45), TextDim);

            // what's contributing to usage (last 7 days, this machine only)
            int iy = ty + 63;
            using (var pen = new Pen(CardLine)) g.DrawLine(pen, r.X + 8, iy - 4, r.Right - 8, iy - 4);
            TextRenderer.DrawText(g, L("ปัจจัยการใช้งาน · 7 วัน (เครื่องนี้)", "WHAT'S DRIVING USAGE · 7d (this machine)"),
                FSmall, new Point(r.X + 8, iy), TextDim);
            if (_activity.PctBig >= 0)
                TextRenderer.DrawText(g, _activity.PctBig + L("% มาจาก context ใหญ่ (>150k)", "% from large context (>150k)"),
                    FSmall, new Point(r.X + 8, iy + 15), Color.FromArgb(150, 190, 255));
            if (_activity.PctSide >= 0)
                TextRenderer.DrawText(g, _activity.PctSide + L("% มาจาก subagents", "% from subagents"),
                    FSmall, new Point(r.X + 8, iy + 30), Color.FromArgb(150, 190, 255));
            TextRenderer.DrawText(g, "skills: " + (_activity.SkillsLine.Length > 0 ? _activity.SkillsLine : "—"),
                FSmall, new Point(r.X + 8, iy + 45), TextDim);
        }

        void DrawSparkline(Graphics g, Rectangle r)
        {
            if (_sessionSamples.Count < 2)
            {
                TextRenderer.DrawText(g, L("รอเก็บข้อมูล 24h…", "collecting 24h data…"),
                    FSmall, new Point(r.X, r.Y + 2), Color.FromArgb(90, 90, 105));
                return;
            }
            var start = DateTime.Now.AddHours(-24);
            double span = 24 * 60.0;
            var pts = _sessionSamples
                .Select(s => new PointF(
                    r.X + (float)(Math.Max(0, (s.Key - start).TotalMinutes) / span * r.Width),
                    r.Bottom - (float)(s.Value / 100.0 * r.Height)))
                .ToArray();
            var area = pts.Concat(new[] { new PointF(pts[pts.Length - 1].X, r.Bottom), new PointF(pts[0].X, r.Bottom) }).ToArray();
            using (var fill = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2),
                Color.FromArgb(80, 100, 149, 237), Color.FromArgb(0, 100, 149, 237), 90f))
                g.FillPolygon(fill, area);
            using (var pen = new Pen(Color.CornflowerBlue, 1.5f))
                g.DrawLines(pen, pts);
            TextRenderer.DrawText(g, "24h", FSmall, new Point(r.Right + 4, r.Y + 2), Color.FromArgb(90, 90, 105));
        }

        static void DrawGlyph(Graphics g, Rectangle r, string glyph, Color c)
        {
            TextRenderer.DrawText(g, glyph, FLabel, r, c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void UpdateHeight()
        {
            if (_settings.Mini) { Width = 216; Height = 40; return; }
            int extra = 0;
            foreach (var a in _extras)
                extra += 22 + (a.Snap != null ? a.Snap.Limits.Count * 46 : 16);
            int n = _usage != null ? Math.Min(3, _usage.Limits.Count) : 3;
            int mainH = _settings.Theme == 0 ? 96 : n * 52; // rings vs themed bars
            int fable = (_stats != null && _stats.FableCost > 0) ? 32 : 0;
            Height = 76 + mainH + fable + extra + 76 + (_graphOpen ? 305 : 0) + (_detailsOpen ? 91 : 0);
        }

        static Color Lighten(Color c)
        {
            return Color.FromArgb(Math.Min(255, c.R + 55), Math.Min(255, c.G + 55), Math.Min(255, c.B + 55));
        }

        static Color Darken(Color c)
        {
            return Color.FromArgb((int)(c.R * 0.8), (int)(c.G * 0.8), (int)(c.B * 0.8));
        }

        static Color BarColor(int pct)
        {
            if (pct >= 85) return Color.FromArgb(226, 102, 102);
            if (pct >= 60) return Color.FromArgb(230, 178, 86);
            return Color.FromArgb(92, 146, 235);
        }

        string Countdown(DateTimeOffset resetsAt)
        {
            var d = resetsAt - DateTimeOffset.Now;
            if (d.TotalSeconds <= 0) return L("ตอนนี้", "now");
            if (d.TotalHours >= 24) return string.Format("{0}d {1}h", (int)d.TotalDays, d.Hours);
            return string.Format("{0}h {1:00}m", (int)d.TotalHours, d.Minutes);
        }

        static string PlanBadge(string subscriptionType)
        {
            if (string.IsNullOrEmpty(subscriptionType)) return "";
            var s = subscriptionType.ToLowerInvariant();
            if (s.Contains("max")) return "Max";
            if (s.Contains("pro")) return "Pro";
            if (s.Contains("team")) return "Team";
            if (s.Contains("enterprise")) return "Enterprise";
            return subscriptionType;
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Icon MakeTrayIcon(int worstPct)
        {
            using (var bmp = new Bitmap(16, 16))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var c = worstPct < 0 ? Color.Gray : BarColor(worstPct);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);
                if (worstPct >= 0)
                {
                    string t = worstPct >= 100 ? "!" : worstPct >= 10 ? (worstPct / 10).ToString() : "0";
                    TextRenderer.DrawText(g, t, new Font("Segoe UI", 7f, FontStyle.Bold),
                        new Rectangle(0, 0, 16, 16), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        // ---- window interaction ----

        void OnDragStart(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_btnClose.Contains(e.Location) || _btnRefresh.Contains(e.Location) || _btnPin.Contains(e.Location)) return;
            _dragging = true;
            _dragOffset = e.Location;
        }

        void OnDragMove(object s, MouseEventArgs e)
        {
            if (_dragging) { Location = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y); return; }

            string prev = _hoverText;
            _hoverText = "";

            // heatmap hover: label the cell under the cursor (drawn in OnPaint)
            if (_graphOpen && _activity != null && _hmCellW > 0)
            {
                int w = (e.X - _hmGX) / (_hmCellW + 3);
                int d = (e.Y - _hmGY) / (_hmCellH + 3);
                if (w >= 0 && w < 5 && d >= 0 && d < 7 && e.X >= _hmGX && e.Y >= _hmGY)
                {
                    var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
                    var day = monday.AddDays(-(4 - w) * 7 + d);
                    if (day <= DateTime.Today)
                    {
                        long v; _activity.PerDay.TryGetValue(day, out v);
                        _hoverText = day.ToString("ddd d MMM") + " · " + LocalScanner.FmtTok(v) + " tok";
                        _hoverAt = new Point(e.X, e.Y);
                    }
                }
            }
            // hover the reset headline → exact reset date/time
            else if (_usage != null && _usage.Limits.Count > 0
                && new Rectangle(14, 50, Width - 28, 18).Contains(e.Location))
            {
                var hl = _usage.Limits.FirstOrDefault(x => x.Kind == "session") ?? _usage.Limits[0];
                _hoverText = hl.ResetsAt.ToLocalTime().ToString("dddd HH:mm · d MMM");
                _hoverAt = new Point(e.X, e.Y);
            }
            if (_hoverText != prev) Invalidate();
        }

        void OnDragEnd(object s, MouseEventArgs e) { _dragging = false; }

        void OnClickButtons(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (_btnClose.Contains(e.Location)) { Close(); return; }           // exit app
            if (_btnMin.Contains(e.Location))                                  // shrink to pill bar
            {
                _settings.Mini = !_settings.Mini;
                _settings.Save();
                ApplyMini();
                return;
            }
            if (_btnRefresh.Contains(e.Location)) { BeginFetch(); return; }
            if (_btnMenu.Contains(e.Location)) { _tray.ContextMenuStrip.Show(this, e.Location); return; }
            if (_btnPin.Contains(e.Location)) { TopMost = !TopMost; Invalidate(); return; }
            if (_btnDetails.Contains(e.Location))
            {
                _detailsOpen = !_detailsOpen;
                UpdateHeight();
                if (_detailsOpen) StartScan();
                Invalidate();
                return;
            }
            if (_sparkRect.Contains(e.Location) || _btnGraph.Contains(e.Location))
            {
                _graphOpen = !_graphOpen;
                UpdateHeight();
                if (_graphOpen) StartActivityScan();
                Invalidate();
            }
        }

        // ---- autostart ----

        static bool IsAutoStart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunValue) != null;
        }

        static void SetAutoStart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValue, false);
            }
        }
    }
}
