// MeetingNudge for Windows — full-screen, impossible-to-ignore meeting reminders from Google Calendar.
// Tray app (WinForms). Data lives in %APPDATA%\MeetingNudge. Written for C# 5 (Windows PowerShell's built-in compiler).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MeetingNudge
{
    // ======================= Storage & settings =======================

    public static class Paths
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetingNudge");
        public static readonly string FeedFile = Path.Combine(Dir, "ical-url.txt");
        public static readonly string SettingsFile = Path.Combine(Dir, "settings.json");
        public static readonly string LogFile = Path.Combine(Dir, "MeetingNudge.log");
        static Paths() { Directory.CreateDirectory(Dir); }
    }

    public static class Log
    {
        public static void Write(string msg)
        {
            try { File.AppendAllText(Paths.LogFile, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + Environment.NewLine); }
            catch (IOException) { }
        }
    }

    public class Reminder
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Time { get; set; }    // local "yyyy-MM-dd HH:mm": full date for once/weekly (its weekday), time of day otherwise
        public string Repeat { get; set; }  // once | daily | weekdays | weekly
        public bool Enabled { get; set; }

        public Reminder()
        {
            Id = Guid.NewGuid().ToString();
            Title = "";
            Time = DateTime.Now.AddMinutes(30).ToString("yyyy-MM-dd HH:mm");
            Repeat = "once";
            Enabled = true;
        }

        public DateTime TimeValue
        {
            get
            {
                DateTime t;
                return DateTime.TryParseExact(Time, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : DateTime.Now;
            }
        }

        DateTime? Candidate(int dayOffset, DateTime now)
        {
            var day = now.Date.AddDays(dayOffset);
            var t = TimeValue;
            switch (Repeat)
            {
                case "daily": break;
                case "weekdays": if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) return null; break;
                case "weekly": if (day.DayOfWeek != t.DayOfWeek) return null; break;
                default: return null;
            }
            return day.AddHours(t.Hour).AddMinutes(t.Minute);
        }

        /// Latest scheduled time at or before now (local).
        public DateTime? LastFire(DateTime now)
        {
            if (Repeat == "once") return TimeValue <= now ? TimeValue : (DateTime?)null;
            for (int d = 0; d <= 7; d++) { var c = Candidate(-d, now); if (c.HasValue && c.Value <= now) return c; }
            return null;
        }

        /// Next scheduled time after now (local).
        public DateTime? NextFire(DateTime now)
        {
            if (Repeat == "once") return TimeValue > now ? TimeValue : (DateTime?)null;
            for (int d = 0; d <= 7; d++) { var c = Candidate(d, now); if (c.HasValue && c.Value > now) return c; }
            return null;
        }

        public static string RepeatLabel(string r)
        {
            switch (r) { case "daily": return "Every day"; case "weekdays": return "Weekdays"; case "weekly": return "Every week"; default: return "Once"; }
        }
    }

    public class AppSettings
    {
        // Missing keys keep these defaults, so adding a setting never wipes someone's reminders.
        public bool MeetingAlertsOn { get; set; }
        public List<int> LeadMinutes { get; set; }
        public int SnoozeMinutes { get; set; }
        public List<Reminder> Reminders { get; set; }
        public bool LoginConfigured { get; set; }

        public AppSettings()
        {
            MeetingAlertsOn = true;
            LeadMinutes = new List<int> { 5 };
            SnoozeMinutes = 1;
            Reminders = new List<Reminder>();
        }

        public static AppSettings Current = Load();

        static AppSettings Load()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    var s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile));
                    if (s != null)
                    {
                        if (s.LeadMinutes == null) s.LeadMinutes = new List<int>();
                        if (s.Reminders == null) s.Reminders = new List<Reminder>();
                        return s;
                    }
                }
            }
            catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            try { File.WriteAllText(Paths.SettingsFile, new JavaScriptSerializer().Serialize(this)); }
            catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
        }
    }

    public static class Feed
    {
        public static string Url()
        {
            try { if (File.Exists(Paths.FeedFile)) { var s = File.ReadAllText(Paths.FeedFile).Trim(); return s.Length > 0 ? s : null; } }
            catch (IOException) { }
            return null;
        }

        public static void Save(string link) { File.WriteAllText(Paths.FeedFile, link); }

        /// Downloads the feed and returns meetings in the next 24h; throws on failure.
        public static List<Meeting> Fetch(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 90000;
            req.ReadWriteTimeout = 90000;
            string text;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                text = reader.ReadToEnd();
            var now = DateTime.UtcNow;
            return Calendar.Meetings(Calendar.Parse(text, Calendar.SelfEmail(url)), now.AddMinutes(-2), now.AddHours(24));
        }
    }

    public static class Links
    {
        // Zoom web links go through a browser page first; zoommtg:// opens the Zoom app straight into the meeting.
        public static void OpenJoin(string url)
        {
            Open(ZoomAppUrl(url) ?? url);
        }

        static string ZoomAppUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                if (!u.Host.ToLowerInvariant().Contains("zoom.")) return null;
                using (var key = Registry.ClassesRoot.OpenSubKey("zoommtg")) { if (key == null) return null; }
                var parts = u.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                int i = Array.FindIndex(parts, p => p == "j" || p == "w");
                if (i < 0 || i + 1 >= parts.Length) return null;
                string q = "zoommtg://" + u.Host + "/join?action=join&confno=" + parts[i + 1];
                foreach (var kv in u.Query.TrimStart('?').Split('&'))
                    if (kv.StartsWith("pwd=")) q += "&" + kv;
                return q;
            }
            catch (Exception) { return null; }
        }

        public static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("open failed: " + ex.Message); }
        }
    }

    public static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Enabled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) { return k != null && k.GetValue("MeetingNudge") != null; } }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("MeetingNudge", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("MeetingNudge", false);
                }
            }
        }
    }

    // ======================= Full-screen alert =======================

    public enum AlertKind { Meeting, Reminder }

    public class Alert
    {
        public AlertKind Kind;
        public string Title;
        public DateTime TargetUtc;
        public string JoinUrl;
    }

    public class Overlay
    {
        readonly List<Form> forms = new List<Form>();
        readonly List<Label> headlines = new List<Label>();
        readonly List<Label> countdowns = new List<Label>();
        readonly System.Windows.Forms.Timer flashTimer = new System.Windows.Forms.Timer { Interval = 600 };
        readonly System.Windows.Forms.Timer tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        bool flashOn;
        Alert alert;
        public Action<Alert> OnSnooze;

        public bool IsShowing { get { return forms.Count > 0; } }

        public Overlay()
        {
            flashTimer.Tick += delegate
            {
                flashOn = !flashOn;
                foreach (var f in forms) f.BackColor = flashOn ? Color.FromArgb(255, 140, 0) : Color.FromArgb(220, 30, 30);
            };
            tickTimer.Tick += delegate { Tick(); };
        }

        static void PlaySound()
        {
            var alarm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Alarm01.wav");
            try { if (File.Exists(alarm)) { new SoundPlayer(alarm).Play(); return; } } catch (Exception) { }
            SystemSounds.Exclamation.Play();
        }

        string Headline()
        {
            if (alert.Kind == AlertKind.Reminder) return "⏰ REMINDER";
            int mins = (int)Math.Ceiling((alert.TargetUtc - DateTime.UtcNow).TotalMinutes);
            return mins > 0 ? "⏰ MEETING IN " + mins + " MIN — JOIN NOW" : "⏰ MEETING STARTED — JOIN NOW";
        }

        void Tick()
        {
            if (alert == null) return;
            int secs = (int)Math.Round((alert.TargetUtc - DateTime.UtcNow).TotalSeconds);
            string text = alert.Kind == AlertKind.Reminder
                ? "Set for " + alert.TargetUtc.ToLocalTime().ToString("HH:mm")
                : secs > 0 ? string.Format("Starts in {0}:{1:00}", secs / 60, secs % 60)
                           : string.Format("STARTED {0}:{1:00} AGO", -secs / 60, -secs % 60);
            string head = Headline();
            foreach (var l in countdowns) l.Text = text;
            foreach (var l in headlines) l.Text = head;
            // Re-ding every 30s so it can't fade into the background.
            if (secs % 30 == 0) PlaySound();
        }

        public void Show(Alert a)
        {
            Close();
            alert = a;
            foreach (var screen in Screen.AllScreens) forms.Add(BuildForm(a, screen));
            foreach (var f in forms) f.Show();
            if (forms.Count > 0) { forms[0].Activate(); forms[0].Focus(); }
            PlaySound();
            flashTimer.Start();
            tickTimer.Start();
            Tick();
        }

        public void Close()
        {
            flashTimer.Stop();
            tickTimer.Stop();
            foreach (var f in forms) { f.Hide(); f.Dispose(); }
            forms.Clear(); headlines.Clear(); countdowns.Clear();
        }

        static Label MakeLabel(string text, float size, FontStyle style, int width)
        {
            return new Label
            {
                Text = text, ForeColor = Color.White, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", size, style), TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false, Width = width, AutoEllipsis = true
            };
        }

        static Button MakeButton(string text, float size, int w, int h)
        {
            return new Button
            {
                Text = text, Font = new Font("Segoe UI", size, FontStyle.Bold), Width = w, Height = h,
                BackColor = Color.White, ForeColor = Color.FromArgb(40, 40, 40), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
            };
        }

        Form BuildForm(Alert a, Screen screen)
        {
            var b = screen.Bounds;
            var f = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = b,
                TopMost = true, ShowInTaskbar = false, BackColor = Color.FromArgb(220, 30, 30), Opacity = 0.94, KeyPreview = true
            };
            int w = b.Width - 80;
            var headline = MakeLabel(Headline(), 40, FontStyle.Bold, w); headline.Height = 110;
            var title = MakeLabel(a.Title, 28, FontStyle.Regular, w); title.Height = 120;
            var countdown = MakeLabel("", 22, FontStyle.Regular, w); countdown.Height = 60;
            headlines.Add(headline); countdowns.Add(countdown);

            string primaryText = a.Kind == AlertKind.Reminder ? "✓  Got it" : a.JoinUrl != null ? "▶  JOIN NOW" : "Open Google Calendar";
            var primary = MakeButton(primaryText, 24, 460, 90);
            var snooze = MakeButton("Snooze " + AppSettings.Current.SnoozeMinutes + " min", 14, 220, 54);
            var dismiss = MakeButton("Dismiss", 14, 220, 54);

            primary.Click += delegate
            {
                if (alert != null && alert.Kind == AlertKind.Meeting)
                {
                    if (alert.JoinUrl != null) Links.OpenJoin(alert.JoinUrl); else Links.Open("https://calendar.google.com");
                }
                Close();
            };
            snooze.Click += delegate { var cur = alert; Close(); if (cur != null && OnSnooze != null) OnSnooze(cur); };
            dismiss.Click += delegate { Close(); };
            f.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
                else if (e.KeyCode == Keys.Enter) { e.Handled = true; primary.PerformClick(); }
            };

            // Stack everything in the middle of the screen.
            int total = headline.Height + title.Height + countdown.Height + 40 + primary.Height + 30 + snooze.Height;
            int y = (b.Height - total) / 2;
            Action<Control, int> place = (c, gap) => { c.Left = (b.Width - c.Width) / 2; c.Top = y; y += c.Height + gap; f.Controls.Add(c); };
            place(headline, 0); place(title, 0); place(countdown, 40); place(primary, 30);
            snooze.Top = dismiss.Top = y;
            snooze.Left = b.Width / 2 - snooze.Width - 10;
            dismiss.Left = b.Width / 2 + 10;
            f.Controls.Add(snooze); f.Controls.Add(dismiss);
            f.Shown += delegate { primary.Focus(); };
            return f;
        }
    }

    // ======================= Watcher =======================

    public enum FeedState { NotSet, Loading, Ok, Failed }

    public class Watcher
    {
        public readonly Overlay Overlay = new Overlay();
        public List<Meeting> Upcoming = new List<Meeting>();
        public DateTime? PausedUntil;
        public FeedState State = FeedState.NotSet;
        public Action OnUpdate;
        readonly HashSet<string> alerted = new HashSet<string>();
        readonly List<KeyValuePair<DateTime, Alert>> snoozed = new List<KeyValuePair<DateTime, Alert>>();
        readonly SynchronizationContext ui;
        bool fetching;

        public Watcher()
        {
            ui = SynchronizationContext.Current;
            Overlay.OnSnooze = a => snoozed.Add(new KeyValuePair<DateTime, Alert>(DateTime.UtcNow.AddMinutes(AppSettings.Current.SnoozeMinutes), a));
        }

        public Meeting NextMeeting { get { var now = DateTime.UtcNow; return Upcoming.FirstOrDefault(m => m.StartUtc > now); } }

        public void Start()
        {
            var check = new System.Windows.Forms.Timer { Interval = 15000 };
            check.Tick += delegate { Check(); };
            check.Start();
            var fetch = new System.Windows.Forms.Timer { Interval = 180000 };
            fetch.Tick += delegate { Refresh(); };
            fetch.Start();
            Refresh();
        }

        void Changed() { if (OnUpdate != null) OnUpdate(); }

        /// Re-fetches the calendar in the background (also right after a new link is saved).
        public void Refresh()
        {
            var url = Feed.Url();
            if (url == null) { Upcoming = new List<Meeting>(); State = FeedState.NotSet; Changed(); return; }
            if (fetching) return;
            fetching = true;
            if (State != FeedState.Ok) { State = FeedState.Loading; Changed(); }
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Meeting> list = null;
                try { list = Feed.Fetch(url); }
                catch (Exception ex) { Log.Write("fetch failed: " + ex.Message); }
                ui.Post(delegate
                {
                    fetching = false;
                    if (list != null)
                    {
                        Upcoming = list;
                        State = FeedState.Ok;
                        var next = list.FirstOrDefault();
                        Log.Write("feed ok: " + list.Count + " meetings in next 24h; next: " + (next == null ? "none" : next.Title + " @ " + next.StartUtc.ToLocalTime()));
                    }
                    else if (State != FeedState.Ok) State = FeedState.Failed;  // keep the last good list when offline
                    Check();
                }, null);
            });
        }

        public void Check()
        {
            try { CheckInner(); } finally { Changed(); }
        }

        void CheckInner()
        {
            if (Overlay.IsShowing) return;
            var now = DateTime.UtcNow;
            if (PausedUntil.HasValue && PausedUntil.Value > now) return;
            PausedUntil = null;

            int si = snoozed.FindIndex(x => x.Key <= now);
            if (si >= 0) { var a = snoozed[si].Value; snoozed.RemoveAt(si); Overlay.Show(a); return; }

            var s = AppSettings.Current;
            var localNow = DateTime.Now;
            foreach (var r in s.Reminders.Where(r => r.Enabled))
            {
                var t = r.LastFire(localNow);
                if (!t.HasValue || (localNow - t.Value).TotalSeconds >= 120) continue;
                string key = "rem|" + r.Id + "|" + t.Value.Ticks;
                if (alerted.Contains(key)) continue;
                alerted.Add(key);
                Log.Write("reminder: " + r.Title);
                Overlay.Show(new Alert { Kind = AlertKind.Reminder, Title = r.Title.Length == 0 ? "Reminder" : r.Title, TargetUtc = t.Value.ToUniversalTime() });
                return;
            }

            if (!s.MeetingAlertsOn) return;
            var leads = s.LeadMinutes.OrderBy(x => x).ToList();
            foreach (var m in Upcoming.Where(m => m.StartUtc > now.AddSeconds(-60)))
            {
                // The most recently crossed alert time; earlier (bigger) ones count as done,
                // so starting the app late doesn't replay every alert back to back.
                var crossed = leads.Where(l => now >= m.StartUtc.AddMinutes(-l)).ToList();
                if (crossed.Count == 0) continue;
                int lead = crossed[0];
                if (alerted.Contains(m.Key + "|" + lead)) continue;
                foreach (var l in leads.Where(l => l >= lead)) alerted.Add(m.Key + "|" + l);
                Log.Write("alert (" + lead + " min): " + m.Title + " join=" + (m.JoinUrl ?? "none"));
                Overlay.Show(new Alert { Kind = AlertKind.Meeting, Title = m.Title, TargetUtc = m.StartUtc, JoinUrl = m.JoinUrl });
                return;
            }
        }

        public void ShowTest()
        {
            Overlay.Show(new Alert { Kind = AlertKind.Meeting, Title = "Test alert — MeetingNudge", TargetUtc = DateTime.UtcNow.AddMinutes(5) });
        }
    }

    // ======================= Settings window =======================

    public class ReminderDialog : Form
    {
        readonly TextBox title = new TextBox { Width = 320 };
        readonly ComboBox repeat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        readonly DateTimePicker date = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd d MMM yyyy", Width = 160 };
        readonly DateTimePicker time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
        readonly CheckBox enabled = new CheckBox { Text = "On", AutoSize = true };
        static readonly string[] Repeats = { "once", "daily", "weekdays", "weekly" };

        public ReminderDialog(Reminder r)
        {
            Text = "Reminder"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Segoe UI", 9.5f); Padding = new Padding(12);
            foreach (var x in Repeats) repeat.Items.Add(Reminder.RepeatLabel(x));
            title.Text = r.Title;
            repeat.SelectedIndex = Math.Max(0, Array.IndexOf(Repeats, r.Repeat));
            date.Value = r.TimeValue; time.Value = r.TimeValue; enabled.Checked = r.Enabled;
            repeat.SelectedIndexChanged += delegate { UpdateDateEnabled(); };
            UpdateDateEnabled();

            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            AcceptButton = ok; CancelButton = cancel;

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            Action<string, Control> row = (label, c) =>
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
                grid.Controls.Add(c);
            };
            row("Remind me to", title);
            row("Repeat", repeat);
            var when = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            when.Controls.Add(date); when.Controls.Add(time);
            row("When", when);
            row("", enabled);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            row("", buttons);
            Controls.Add(grid);
        }

        void UpdateDateEnabled()
        {
            string r = Repeats[Math.Max(0, repeat.SelectedIndex)];
            date.Enabled = r == "once" || r == "weekly";
        }

        public void ApplyTo(Reminder r)
        {
            r.Title = title.Text.Trim();
            r.Repeat = Repeats[Math.Max(0, repeat.SelectedIndex)];
            var d = date.Value.Date;
            r.Time = new DateTime(d.Year, d.Month, d.Day, time.Value.Hour, time.Value.Minute, 0).ToString("yyyy-MM-dd HH:mm");
            r.Enabled = enabled.Checked;
        }
    }

    public class SettingsForm : Form
    {
        readonly Watcher watcher;
        readonly Label feedStatus = new Label { AutoSize = true, MaximumSize = new Size(560, 0) };
        readonly Panel linkEditor = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        readonly TextBox linkBox = new TextBox { Width = 440, UseSystemPasswordChar = true };
        readonly Label linkError = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(560, 0) };
        readonly Button changeLink = new Button { Text = "Change link", AutoSize = true };
        readonly ListBox leads = new ListBox { Width = 200, Height = 80 };
        readonly ListView reminders = new ListView { View = View.Details, FullRowSelect = true, Width = 560, Height = 170, HideSelection = false };

        static GroupBox Section(string title, Control content)
        {
            var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Margin = new Padding(0, 0, 0, 10) };
            content.Dock = DockStyle.Fill;
            g.Controls.Add(content);
            return g;
        }

        static FlowLayoutPanel Row(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            foreach (var c in cs) { c.Anchor = AnchorStyles.Left; p.Controls.Add(c); }
            return p;
        }

        static FlowLayoutPanel Column(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            foreach (var c in cs) p.Controls.Add(c);
            return p;
        }

        static Label Text_(string t) { return new Label { Text = t, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }; }

        public SettingsForm(Watcher w)
        {
            watcher = w;
            Text = "MeetingNudge"; Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(14);
            var s = AppSettings.Current;

            // --- Google Calendar ---
            var connect = new Button { Text = "Connect", AutoSize = true };
            connect.Click += delegate { SaveLink(); };
            linkBox.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveLink(); } };
            var steps = new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(560, 0),
                Text = "1. Open calendar.google.com → ⚙ → Settings\n2. Left sidebar: click your calendar → \"Integrate calendar\"\n" +
                       "3. Copy \"Secret address in iCal format\" (not the public one)\n4. Paste it below. It stays on this PC only — treat it like a password."
            };
            linkEditor.Controls.Add(steps);
            linkEditor.Controls.Add(Row(linkBox, connect));
            linkEditor.Controls.Add(linkError);
            changeLink.Click += delegate { linkEditor.Visible = true; changeLink.Visible = false; linkBox.Focus(); };
            var calendar = Column(Row(feedStatus, changeLink), linkEditor);

            // --- Meeting alerts ---
            var alertsOn = new CheckBox { Text = "Full-screen alert before every meeting", AutoSize = true, Checked = s.MeetingAlertsOn };
            alertsOn.CheckedChanged += delegate { s.MeetingAlertsOn = alertsOn.Checked; s.Save(); };
            var newLead = new NumericUpDown { Minimum = 1, Maximum = 240, Value = 10, Width = 60 };
            var addLead = new Button { Text = "Add alert", AutoSize = true };
            var removeLead = new Button { Text = "Remove selected", AutoSize = true };
            addLead.Click += delegate
            {
                int v = (int)newLead.Value;
                if (!s.LeadMinutes.Contains(v)) { s.LeadMinutes.Add(v); s.Save(); RefreshLeads(); }
            };
            removeLead.Click += delegate
            {
                if (leads.SelectedIndex < 0) return;
                s.LeadMinutes.Remove(s.LeadMinutes.OrderByDescending(x => x).ElementAt(leads.SelectedIndex));
                s.Save(); RefreshLeads();
            };
            var snooze = new NumericUpDown { Minimum = 1, Maximum = 30, Value = Math.Max(1, Math.Min(30, s.SnoozeMinutes)), Width = 60 };
            snooze.ValueChanged += delegate { s.SnoozeMinutes = (int)snooze.Value; s.Save(); };
            var meeting = Column(
                alertsOn,
                Row(leads, Column(removeLead)),
                Row(newLead, Text_("min before"), addLead),
                Row(Text_("Snooze length"), snooze, Text_("min")));

            // --- My reminders ---
            reminders.Columns.Add("On", 40); reminders.Columns.Add("Reminder", 230); reminders.Columns.Add("Repeat", 100); reminders.Columns.Add("Next", 170);
            var addRem = new Button { Text = "Add reminder…", AutoSize = true };
            var editRem = new Button { Text = "Edit…", AutoSize = true };
            var delRem = new Button { Text = "Delete", AutoSize = true };
            addRem.Click += delegate
            {
                var r = new Reminder();
                using (var dlg = new ReminderDialog(r))
                    if (dlg.ShowDialog(this) == DialogResult.OK) { dlg.ApplyTo(r); s.Reminders.Add(r); s.Save(); RefreshReminders(); }
            };
            EventHandler edit = delegate
            {
                if (reminders.SelectedItems.Count == 0) return;
                var r = (Reminder)reminders.SelectedItems[0].Tag;
                using (var dlg = new ReminderDialog(r))
                    if (dlg.ShowDialog(this) == DialogResult.OK) { dlg.ApplyTo(r); s.Save(); RefreshReminders(); }
            };
            editRem.Click += edit;
            reminders.DoubleClick += edit;
            delRem.Click += delegate
            {
                if (reminders.SelectedItems.Count == 0) return;
                s.Reminders.Remove((Reminder)reminders.SelectedItems[0].Tag);
                s.Save(); RefreshReminders();
            };
            var mine = Column(reminders, Row(addRem, editRem, delRem));

            // --- General ---
            var login = new CheckBox { Text = "Open MeetingNudge when I sign in to Windows", AutoSize = true, Checked = Startup.Enabled };
            login.CheckedChanged += delegate { Startup.Enabled = login.Checked; };
            var test = new Button { Text = "Show test alert", AutoSize = true };
            test.Click += delegate { watcher.ShowTest(); };
            var general = Column(login, Row(Text_("Settings save automatically."), test));

            var root = Column(Section("Google Calendar", calendar), Section("Meeting alerts", meeting), Section("My reminders", mine), Section("General", general));
            Controls.Add(root);

            RefreshLeads();
            RefreshReminders();
            UpdateFeed();
            bool hasLink = Feed.Url() != null;
            linkEditor.Visible = !hasLink;
            changeLink.Visible = hasLink;
        }

        void SaveLink()
        {
            var link = linkBox.Text.Trim();
            if (!link.StartsWith("https://calendar.google.com/calendar/ical/") || !link.EndsWith(".ics"))
            {
                linkError.Text = "That doesn't look like the \"Secret address in iCal format\" — it starts with https://calendar.google.com/calendar/ical/ and ends with .ics";
                return;
            }
            try
            {
                Feed.Save(link);
                linkError.Text = ""; linkBox.Text = "";
                linkEditor.Visible = false; changeLink.Visible = true;
                watcher.State = FeedState.Loading;
                watcher.Refresh();
                UpdateFeed();
            }
            catch (Exception ex) { linkError.Text = "Couldn't save: " + ex.Message; }
        }

        public void UpdateFeed()
        {
            switch (watcher.State)
            {
                case FeedState.NotSet: feedStatus.Text = "Not connected"; break;
                case FeedState.Loading: feedStatus.Text = "Loading your calendar… (Google takes up to ~30s)"; break;
                case FeedState.Ok: int n = watcher.Upcoming.Count; feedStatus.Text = "✓ Connected — " + n + " meeting" + (n == 1 ? "" : "s") + " in the next 24h"; break;
                default: feedStatus.Text = "⚠ Couldn't load your calendar: check the link and your internet connection"; break;
            }
        }

        void RefreshLeads()
        {
            leads.Items.Clear();
            foreach (var m in AppSettings.Current.LeadMinutes.OrderByDescending(x => x)) leads.Items.Add(m + " min before");
        }

        void RefreshReminders()
        {
            reminders.Items.Clear();
            var now = DateTime.Now;
            foreach (var r in AppSettings.Current.Reminders)
            {
                var next = r.NextFire(now);
                string nextText = !r.Enabled ? "Off" : !next.HasValue ? "Done"
                    : next.Value.Date == now.Date ? "Today " + next.Value.ToString("HH:mm") : next.Value.ToString("ddd d MMM, HH:mm");
                var item = new ListViewItem(new[] { r.Enabled ? "✓" : "", r.Title.Length == 0 ? "(no text)" : r.Title, Reminder.RepeatLabel(r.Repeat), nextText }) { Tag = r };
                reminders.Items.Add(item);
            }
        }
    }

    // ======================= Tray app =======================

    public class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Watcher watcher;
        SettingsForm settings;
        IntPtr iconHandle = IntPtr.Zero;
        string iconText = null;

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        public TrayApp()
        {
            watcher = new Watcher();
            watcher.OnUpdate = UpdateTray;
            tray.ContextMenuStrip = menu;
            menu.Opening += delegate { BuildMenu(); };
            tray.DoubleClick += delegate { OpenSettings(); };
            tray.Visible = true;
            UpdateTray();
            watcher.Start();

            var s = AppSettings.Current;
            if (!s.LoginConfigured) { Startup.Enabled = true; s.LoginConfigured = true; s.Save(); }
            if (Feed.Url() == null) OpenSettings();  // first run: connect the calendar
        }

        static string Clock(DateTime utc) { return utc.ToLocalTime().ToString("HH:mm"); }

        // The tray icon shows minutes to the next meeting (within the hour), else an alarm dot.
        void UpdateTray()
        {
            var now = DateTime.UtcNow;
            var next = watcher.NextMeeting;
            string tip = "MeetingNudge";
            string text = "";
            if (watcher.PausedUntil.HasValue && watcher.PausedUntil.Value > now) { tip += " — paused until " + Clock(watcher.PausedUntil.Value); text = "II"; }
            else if (next != null)
            {
                int mins = (int)Math.Ceiling((next.StartUtc - now).TotalMinutes);
                tip += " — next: " + next.Title + " " + (mins <= 60 ? "in " + mins + " min" : "at " + Clock(next.StartUtc));
                if (mins <= 60) text = mins.ToString();
            }
            tray.Text = tip.Length > 63 ? tip.Substring(0, 60) + "…" : tip;
            if (text != iconText) SetIcon(text);
            if (settings != null && !settings.IsDisposed) settings.UpdateFeed();
        }

        void SetIcon(string text)
        {
            iconText = text;
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.FillEllipse(new SolidBrush(Color.FromArgb(220, 30, 30)), 1, 1, 30, 30);
                    if (text.Length == 0)
                    {
                        // clock hands
                        var pen = new Pen(Color.White, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLine(pen, 16, 16, 16, 7);
                        g.DrawLine(pen, 16, 16, 23, 19);
                    }
                    else
                    {
                        using (var font = new Font("Segoe UI", text.Length > 1 ? 15 : 19, FontStyle.Bold, GraphicsUnit.Pixel))
                        {
                            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
                        }
                    }
                }
                var old = iconHandle;
                iconHandle = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(iconHandle);
                if (old != IntPtr.Zero) DestroyIcon(old);
            }
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            var next = watcher.NextMeeting;
            if (next != null)
            {
                int mins = (int)Math.Ceiling((next.StartUtc - DateTime.UtcNow).TotalMinutes);
                menu.Items.Add(new ToolStripMenuItem("Next: " + next.Title + " — " + Clock(next.StartUtc) + " (in " + mins + " min)") { Enabled = false });
                if (next.JoinUrl != null)
                {
                    var url = next.JoinUrl;
                    menu.Items.Add("Join next meeting now", null, delegate { Links.OpenJoin(url); });
                }
            }
            else menu.Items.Add(new ToolStripMenuItem("No meetings in the next 24h") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings…", null, delegate { OpenSettings(); });
            menu.Items.Add("Show test alert", null, delegate { watcher.ShowTest(); });
            if (watcher.PausedUntil.HasValue && watcher.PausedUntil.Value > DateTime.UtcNow)
                menu.Items.Add("Resume alerts (paused until " + Clock(watcher.PausedUntil.Value) + ")", null, delegate { watcher.PausedUntil = null; watcher.Check(); });
            else
                menu.Items.Add("Pause alerts for 1 hour", null, delegate { watcher.PausedUntil = DateTime.UtcNow.AddHours(1); UpdateTray(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit MeetingNudge", null, delegate { tray.Visible = false; watcher.Overlay.Close(); ExitThread(); });
        }

        public void OpenSettings()
        {
            if (settings == null || settings.IsDisposed) settings = new SettingsForm(watcher);
            settings.Show();
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }
    }

    // ======================= Entry point =======================

    public static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        public static int Main(string[] args)
        {
            // --selftest <fixture.ics> <fromUtc> <toUtc> <out.txt>: prints parsed meetings (CI checks them against the Mac version).
            if (args.Length == 5 && args[0] == "--selftest") return SelfTest(args);

            bool created;
            using (var mutex = new Mutex(true, @"Local\MeetingNudge-single", out created))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MeetingNudge-show"))
            {
                if (!created) { showSignal.Set(); return 0; }  // already running: ask it to open Settings

                try { SetProcessDPIAware(); } catch (Exception) { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("error: " + e.Exception);
                // Create a WinForms sync context before the watcher captures it.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var app = new TrayApp();
                var ui = SynchronizationContext.Current;
                var listener = new Thread(() =>
                {
                    while (true) { showSignal.WaitOne(); ui.Post(delegate { app.OpenSettings(); }, null); }
                }) { IsBackground = true };
                listener.Start();
                Log.Write("started");
                Application.Run(app);
            }
            return 0;
        }

        static int SelfTest(string[] a)
        {
            try
            {
                var from = DateTime.Parse(a[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                var to = DateTime.Parse(a[3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                var events = Calendar.Parse(File.ReadAllText(a[1], Encoding.UTF8), "me@example.com");
                var lines = Calendar.Meetings(events, from, to)
                    .Select(m => m.StartUtc.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture) + " | " + m.Title + " | " + (m.JoinUrl ?? "NONE"));
                File.WriteAllLines(a[4], lines.ToArray(), new UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(a[4], "ERROR " + ex);
                return 1;
            }
        }
    }
}
