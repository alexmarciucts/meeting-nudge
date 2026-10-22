// Minimal iCalendar (RFC 5545) reader for Google Calendar's secret iCal feed — Windows port of ICS.swift.
// Written for C# 5 so Windows PowerShell 5.1 (Add-Type) can compile it with no SDK installed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MeetingNudge
{
    public class IcsDate
    {
        public DateTime Utc;
        public DateTime Wall;  // clock time in Tz
        public bool AllDay;
        public TimeZoneInfo Tz;
    }

    public class RawEvent
    {
        public string Uid = "", Summary = "", Description = "", Location = "", Url = "", Conference = "", Status = "";
        public IcsDate Start;
        public Dictionary<string, string> RRule;
        public List<DateTime> ExDates = new List<DateTime>();
        public DateTime? RecurrenceId;
        public int OtherAttendees;
        public bool SelfDeclined;
    }

    public class Meeting
    {
        public string Key, Title, JoinUrl;
        public DateTime StartUtc;
    }

    public static class Calendar
    {
        // ---------------- Time zones ----------------

        // Google writes IANA zone names; .NET Framework on Windows only knows Windows zone IDs.
        static readonly Dictionary<string, string> IanaToWindows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Etc/UTC","UTC"},{"Etc/GMT","UTC"},{"UTC","UTC"},{"GMT","UTC"},
            {"Europe/London","GMT Standard Time"},{"Europe/Dublin","GMT Standard Time"},{"Europe/Lisbon","GMT Standard Time"},
            {"Atlantic/Canary","GMT Standard Time"},{"Atlantic/Reykjavik","Greenwich Standard Time"},
            {"Europe/Berlin","W. Europe Standard Time"},{"Europe/Amsterdam","W. Europe Standard Time"},{"Europe/Rome","W. Europe Standard Time"},
            {"Europe/Vienna","W. Europe Standard Time"},{"Europe/Stockholm","W. Europe Standard Time"},{"Europe/Oslo","W. Europe Standard Time"},
            {"Europe/Zurich","W. Europe Standard Time"},{"Europe/Luxembourg","W. Europe Standard Time"},{"Europe/Malta","W. Europe Standard Time"},
            {"Europe/Monaco","W. Europe Standard Time"},{"Europe/Paris","Romance Standard Time"},{"Europe/Brussels","Romance Standard Time"},
            {"Europe/Madrid","Romance Standard Time"},{"Europe/Copenhagen","Romance Standard Time"},{"Africa/Ceuta","Romance Standard Time"},
            {"Europe/Warsaw","Central European Standard Time"},{"Europe/Zagreb","Central European Standard Time"},
            {"Europe/Sarajevo","Central European Standard Time"},{"Europe/Skopje","Central European Standard Time"},
            {"Europe/Prague","Central Europe Standard Time"},{"Europe/Budapest","Central Europe Standard Time"},
            {"Europe/Belgrade","Central Europe Standard Time"},{"Europe/Bratislava","Central Europe Standard Time"},
            {"Europe/Ljubljana","Central Europe Standard Time"},{"Europe/Bucharest","GTB Standard Time"},{"Europe/Athens","GTB Standard Time"},
            {"Asia/Nicosia","GTB Standard Time"},{"Europe/Chisinau","E. Europe Standard Time"},{"Europe/Kiev","FLE Standard Time"},
            {"Europe/Kyiv","FLE Standard Time"},{"Europe/Helsinki","FLE Standard Time"},{"Europe/Riga","FLE Standard Time"},
            {"Europe/Sofia","FLE Standard Time"},{"Europe/Tallinn","FLE Standard Time"},{"Europe/Vilnius","FLE Standard Time"},
            {"Europe/Istanbul","Turkey Standard Time"},{"Europe/Moscow","Russian Standard Time"},{"Europe/Minsk","Belarus Standard Time"},
            {"Asia/Amman","Jordan Standard Time"},{"Asia/Jerusalem","Israel Standard Time"},{"Asia/Beirut","Middle East Standard Time"},
            {"Asia/Dubai","Arabian Standard Time"},{"Asia/Riyadh","Arab Standard Time"},{"Asia/Tehran","Iran Standard Time"},
            {"Asia/Karachi","Pakistan Standard Time"},{"Asia/Kolkata","India Standard Time"},{"Asia/Calcutta","India Standard Time"},
            {"Asia/Bangkok","SE Asia Standard Time"},{"Asia/Ho_Chi_Minh","SE Asia Standard Time"},{"Asia/Jakarta","SE Asia Standard Time"},
            {"Asia/Singapore","Singapore Standard Time"},{"Asia/Kuala_Lumpur","Singapore Standard Time"},{"Asia/Manila","Singapore Standard Time"},
            {"Asia/Shanghai","China Standard Time"},{"Asia/Hong_Kong","China Standard Time"},{"Asia/Taipei","Taipei Standard Time"},
            {"Asia/Tokyo","Tokyo Standard Time"},{"Asia/Seoul","Korea Standard Time"},
            {"Africa/Johannesburg","South Africa Standard Time"},{"Africa/Cairo","Egypt Standard Time"},
            {"Africa/Lagos","W. Central Africa Standard Time"},{"Africa/Nairobi","E. Africa Standard Time"},{"Africa/Casablanca","Morocco Standard Time"},
            {"America/New_York","Eastern Standard Time"},{"America/Detroit","Eastern Standard Time"},{"America/Toronto","Eastern Standard Time"},
            {"America/Chicago","Central Standard Time"},{"America/Winnipeg","Central Standard Time"},{"America/Denver","Mountain Standard Time"},
            {"America/Edmonton","Mountain Standard Time"},{"America/Phoenix","US Mountain Standard Time"},
            {"America/Los_Angeles","Pacific Standard Time"},{"America/Vancouver","Pacific Standard Time"},
            {"America/Anchorage","Alaskan Standard Time"},{"Pacific/Honolulu","Hawaiian Standard Time"},{"America/Halifax","Atlantic Standard Time"},
            {"America/Sao_Paulo","E. South America Standard Time"},{"America/Mexico_City","Central Standard Time (Mexico)"},
            {"America/Bogota","SA Pacific Standard Time"},{"America/Lima","SA Pacific Standard Time"},
            {"America/Argentina/Buenos_Aires","Argentina Standard Time"},{"America/Buenos_Aires","Argentina Standard Time"},
            {"America/Santiago","Pacific SA Standard Time"},
            {"Australia/Sydney","AUS Eastern Standard Time"},{"Australia/Melbourne","AUS Eastern Standard Time"},
            {"Australia/Brisbane","E. Australia Standard Time"},{"Australia/Perth","W. Australia Standard Time"},
            {"Australia/Adelaide","Cen. Australia Standard Time"},{"Pacific/Auckland","New Zealand Standard Time"},
        };

        static readonly Dictionary<string, TimeZoneInfo> tzCache = new Dictionary<string, TimeZoneInfo>();

        public static TimeZoneInfo FindZone(string tzid)
        {
            if (string.IsNullOrEmpty(tzid)) return TimeZoneInfo.Local;
            TimeZoneInfo tz;
            lock (tzCache)
            {
                if (tzCache.TryGetValue(tzid, out tz)) return tz;
                tz = TimeZoneInfo.Local;
                string winId;
                if (!IanaToWindows.TryGetValue(tzid, out winId)) winId = tzid;
                try { tz = TimeZoneInfo.FindSystemTimeZoneById(winId); } catch (Exception) { }
                tzCache[tzid] = tz;
            }
            return tz;
        }

        public static DateTime ToUtc(DateTime wall, TimeZoneInfo tz)
        {
            wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(wall)) wall = wall.AddHours(1);  // clock-change gap
            return TimeZoneInfo.ConvertTimeToUtc(wall, tz);
        }

        public static IcsDate ParseDate(string value, Dictionary<string, string> p)
        {
            string v = value.Trim();
            TimeZoneInfo tz;
            string tzid;
            if (v.EndsWith("Z")) tz = TimeZoneInfo.Utc;
            else if (p != null && p.TryGetValue("TZID", out tzid)) tz = FindZone(tzid);
            else tz = TimeZoneInfo.Local;
            string d = v.TrimEnd('Z');
            if (d.Length < 8) return null;
            int y, mo, day, h = 0, mi = 0, s = 0;
            if (!int.TryParse(d.Substring(0, 4), out y) || !int.TryParse(d.Substring(4, 2), out mo) || !int.TryParse(d.Substring(6, 2), out day)) return null;
            string valueType;
            bool allDay = (p != null && p.TryGetValue("VALUE", out valueType) && valueType == "DATE") || d.Length == 8;
            if (!allDay && d.Length >= 15)
            {
                int.TryParse(d.Substring(9, 2), out h); int.TryParse(d.Substring(11, 2), out mi); int.TryParse(d.Substring(13, 2), out s);
            }
            try
            {
                var wall = new DateTime(y, mo, day, h, mi, s, DateTimeKind.Unspecified);
                return new IcsDate { Wall = wall, Utc = ToUtc(wall, tz), AllDay = allDay, Tz = tz };
            }
            catch (ArgumentException) { return null; }
        }

        // ---------------- Parsing ----------------

        static bool SplitProp(string line, out string name, out Dictionary<string, string> prms, out string value)
        {
            name = null; prms = null; value = null;
            bool inQuote = false;
            int colon = -1;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') inQuote = !inQuote;
                else if (c == ':' && !inQuote) { colon = i; break; }
            }
            if (colon < 0) return false;
            var parts = line.Substring(0, colon).Split(';');
            prms = new Dictionary<string, string>();
            for (int i = 1; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq > 0) prms[parts[i].Substring(0, eq).ToUpperInvariant()] = parts[i].Substring(eq + 1).Trim('"');
            }
            name = parts[0].ToUpperInvariant();
            value = line.Substring(colon + 1);
            return true;
        }

        public static List<RawEvent> Parse(string text, string selfEmail)
        {
            var events = new List<RawEvent>();
            RawEvent cur = null;
            int nested = 0;  // sub-components (VALARM) inside a VEVENT

            Action<string> handle = line =>
            {
                if (line.StartsWith("BEGIN:"))
                {
                    if (line == "BEGIN:VEVENT") { cur = new RawEvent(); nested = 0; }
                    else if (cur != null) nested++;
                    return;
                }
                if (line.StartsWith("END:"))
                {
                    if (line == "END:VEVENT") { if (cur != null) events.Add(cur); cur = null; }
                    else if (cur != null) nested--;
                    return;
                }
                if (cur == null || nested != 0) return;
                string name, value; Dictionary<string, string> p;
                if (!SplitProp(line, out name, out p, out value)) return;
                switch (name)
                {
                    case "UID": cur.Uid = value; break;
                    case "SUMMARY": cur.Summary = Unescape(value); break;
                    case "DESCRIPTION": cur.Description = value; break;
                    case "LOCATION": cur.Location = value; break;
                    case "URL": cur.Url = value; break;
                    case "X-GOOGLE-CONFERENCE": cur.Conference = value; break;
                    case "STATUS": cur.Status = value.ToUpperInvariant(); break;
                    case "DTSTART": cur.Start = ParseDate(value, p); break;
                    case "RECURRENCE-ID":
                        var rid = ParseDate(value, p);
                        if (rid != null) cur.RecurrenceId = rid.Utc;
                        break;
                    case "EXDATE":
                        foreach (var v in value.Split(','))
                        {
                            var ex = ParseDate(v, p);
                            if (ex != null) cur.ExDates.Add(ex.Utc);
                        }
                        break;
                    case "RRULE":
                        var rule = new Dictionary<string, string>();
                        foreach (var part in value.Split(';'))
                        {
                            int eq = part.IndexOf('=');
                            if (eq > 0) rule[part.Substring(0, eq).ToUpperInvariant()] = part.Substring(eq + 1);
                        }
                        cur.RRule = rule;
                        break;
                    case "ATTENDEE":
                        string cutype, partstat;
                        if (!p.TryGetValue("CUTYPE", out cutype)) cutype = "INDIVIDUAL";
                        cutype = cutype.ToUpperInvariant();
                        if (selfEmail.Length > 0 && value.ToLowerInvariant().Contains(selfEmail))
                            cur.SelfDeclined = p.TryGetValue("PARTSTAT", out partstat) && partstat.ToUpperInvariant() == "DECLINED";
                        else if (cutype == "INDIVIDUAL" || cutype == "GROUP")
                            cur.OtherAttendees++;
                        break;
                }
            };

            // Unfold continuation lines (leading space/tab) while walking the text once.
            var pending = new StringBuilder();
            foreach (var rawLine in text.Split('\n'))
            {
                string raw = rawLine.EndsWith("\r") ? rawLine.Substring(0, rawLine.Length - 1) : rawLine;
                if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
                {
                    pending.Append(raw, 1, raw.Length - 1);
                }
                else
                {
                    if (pending.Length > 0) handle(pending.ToString());
                    pending.Clear();
                    pending.Append(raw);
                }
            }
            if (pending.Length > 0) handle(pending.ToString());
            return events;
        }

        public static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 'n' || n == 'N' ? '\n' : n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ---------------- Recurrence expansion ----------------

        static int? WeekdayNum(string s)
        {
            switch (s.ToUpperInvariant())
            {
                case "SU": return 0; case "MO": return 1; case "TU": return 2; case "WE": return 3;
                case "TH": return 4; case "FR": return 5; case "SA": return 6;
            }
            return null;
        }

        static List<int> IntList(Dictionary<string, string> rule, string key)
        {
            string v;
            var res = new List<int>();
            if (rule.TryGetValue(key, out v))
                foreach (var x in v.Split(',')) { int n; if (int.TryParse(x, out n)) res.Add(n); }
            return res;
        }

        static List<DateTime> DaysInMonth(DateTime monthStart, List<string> byDay, List<int> byMonthDay, List<int> bySetPos)
        {
            int len = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
            var res = new List<DateTime>();
            foreach (int md in byMonthDay)
            {
                int d = md > 0 ? md : len + md + 1;
                if (d >= 1 && d <= len) res.Add(monthStart.AddDays(d - 1));
            }
            foreach (var spec in byDay)
            {
                if (spec.Length < 2) continue;
                var wd = WeekdayNum(spec.Substring(spec.Length - 2));
                if (wd == null) continue;
                var matches = new List<DateTime>();
                for (int i = 0; i < len; i++) { var d = monthStart.AddDays(i); if ((int)d.DayOfWeek == wd.Value) matches.Add(d); }
                int ord;
                if (int.TryParse(spec.Substring(0, spec.Length - 2), out ord))
                {
                    int idx = ord > 0 ? ord - 1 : matches.Count + ord;
                    if (idx >= 0 && idx < matches.Count) res.Add(matches[idx]);
                }
                else res.AddRange(matches);
            }
            res = res.Distinct().OrderBy(d => d).ToList();
            if (bySetPos.Count > 0)
            {
                var picked = new List<DateTime>();
                foreach (int pos in bySetPos)
                {
                    int idx = pos > 0 ? pos - 1 : res.Count + pos;
                    if (idx >= 0 && idx < res.Count) picked.Add(res[idx]);
                }
                res = picked.OrderBy(d => d).ToList();
            }
            return res;
        }

        /// Start times (UTC) of e that fall within [from, to].
        public static List<DateTime> Occurrences(RawEvent e, DateTime from, DateTime to)
        {
            var outList = new List<DateTime>();
            var s = e.Start;
            if (s == null) return outList;
            if (e.RRule == null) { if (s.Utc >= from && s.Utc <= to) outList.Add(s.Utc); return outList; }
            if (s.Utc > to) return outList;

            var rule = e.RRule;
            string freq; if (!rule.TryGetValue("FREQ", out freq)) freq = ""; freq = freq.ToUpperInvariant();
            string tmp;
            int interval = 1;
            if (rule.TryGetValue("INTERVAL", out tmp)) { int.TryParse(tmp, out interval); if (interval < 1) interval = 1; }
            DateTime? until = null;
            if (rule.TryGetValue("UNTIL", out tmp))
            {
                var u = ParseDate(tmp, new Dictionary<string, string> { { "TZID", "" } });
                if (u != null)
                {
                    // Date-only or floating UNTIL is in the event's own zone.
                    DateTime uu = tmp.Trim().EndsWith("Z") ? u.Utc : ToUtc(u.Wall, s.Tz);
                    until = u.AllDay ? ToUtc(u.Wall.AddDays(1), s.Tz) : uu;
                }
            }
            if (until.HasValue && until.Value < from) return outList;
            int? count = null;
            if (rule.TryGetValue("COUNT", out tmp)) { int c; if (int.TryParse(tmp, out c)) count = c; }
            var byDay = rule.TryGetValue("BYDAY", out tmp) ? tmp.Split(',').ToList() : new List<string>();
            var byMonthDay = IntList(rule, "BYMONTHDAY");
            var byMonth = IntList(rule, "BYMONTH");
            var bySetPos = IntList(rule, "BYSETPOS");
            int wkst = (rule.TryGetValue("WKST", out tmp) ? WeekdayNum(tmp) : null) ?? 1;

            var time = s.Wall.TimeOfDay;
            var startDay = s.Wall.Date;
            var startMonth = new DateTime(startDay.Year, startDay.Month, 1);
            var startYear = new DateTime(startDay.Year, 1, 1);
            int startWeekday = (int)startDay.DayOfWeek;
            var weekStart0 = startDay.AddDays(-((startWeekday - wkst + 7) % 7));

            // Without COUNT we can jump straight to the periods near the window.
            int period = 0;
            if (count == null)
            {
                var fromWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(from, DateTimeKind.Utc), s.Tz);
                int elapsed;
                switch (freq)
                {
                    case "DAILY": elapsed = (int)(fromWall.Date - startDay).TotalDays; break;
                    case "WEEKLY": elapsed = (int)(fromWall.Date - weekStart0).TotalDays / 7; break;
                    case "MONTHLY": elapsed = (fromWall.Year - startDay.Year) * 12 + fromWall.Month - startDay.Month; break;
                    default: elapsed = fromWall.Year - startDay.Year; break;
                }
                period = Math.Max(0, elapsed / interval - 1);
            }

            int n = 0;
            for (; period < 100000; period++)
            {
                List<DateTime> days;
                try
                {
                    switch (freq)
                    {
                        case "DAILY":
                            days = new List<DateTime> { startDay.AddDays((double)period * interval) };
                            break;
                        case "WEEKLY":
                            var weekStart = weekStart0.AddDays((double)period * interval * 7);
                            var targets = byDay.Count == 0 ? new List<int> { startWeekday }
                                : byDay.Where(x => x.Length >= 2).Select(x => WeekdayNum(x.Substring(x.Length - 2))).Where(x => x.HasValue).Select(x => x.Value).ToList();
                            days = targets.Select(t => weekStart.AddDays((t - wkst + 7) % 7)).OrderBy(d => d).ToList();
                            break;
                        case "MONTHLY":
                            var month = startMonth.AddMonths(period * interval);
                            var md = byDay.Count == 0 && byMonthDay.Count == 0 ? new List<int> { startDay.Day } : byMonthDay;
                            days = DaysInMonth(month, byDay, md, bySetPos);
                            break;
                        case "YEARLY":
                            var year = startYear.AddYears(period * interval);
                            var months = byMonth.Count == 0 ? new List<int> { startDay.Month } : byMonth.OrderBy(x => x).ToList();
                            days = new List<DateTime>();
                            foreach (int m in months)
                            {
                                var md2 = byDay.Count == 0 && byMonthDay.Count == 0 ? new List<int> { startDay.Day } : byMonthDay;
                                days.AddRange(DaysInMonth(year.AddMonths(m - 1), byDay, md2, bySetPos));
                            }
                            break;
                        default:
                            return outList;
                    }
                }
                catch (ArgumentOutOfRangeException) { return outList; }

                foreach (var day in days)
                {
                    var occ = ToUtc(day + time, s.Tz);
                    if (occ < s.Utc) continue;
                    if (until.HasValue && occ > until.Value) return outList;
                    if (count.HasValue && n >= count.Value) return outList;
                    n++;
                    if (occ > to) return outList;
                    if (occ >= from && !e.ExDates.Any(x => Math.Abs((x - occ).TotalSeconds) < 1)) outList.Add(occ);
                }
            }
            return outList;
        }

        // ---------------- Join links ----------------

        static readonly Regex[] JoinPatterns = new[]
        {
            @"https://[A-Za-z0-9.-]*zoom(gov)?\.(us|com)/(j|w|my|s)/[^\s<>""'\\]+",
            @"zoommtg://[^\s<>""'\\]+",
            @"https://meet\.google\.com/[a-z]{3}-[a-z]{4}-[a-z]{3}",
            @"https://teams\.(microsoft|live)\.com/l/meetup-join/[^\s<>""'\\]+",
            @"https://[A-Za-z0-9.-]*(webex\.com|whereby\.com|around\.co)/[^\s<>""'\\]+",
        }.Select(p => new Regex(p, RegexOptions.IgnoreCase)).ToArray();

        public static string FindJoinUrl(string conference, params string[] texts)
        {
            if (conference.StartsWith("http")) return conference;
            var cleaned = texts.Select(t => Unescape(t).Replace("&amp;", "&")).ToList();
            foreach (var regex in JoinPatterns)
                foreach (var text in cleaned)
                {
                    var m = regex.Match(text);
                    if (m.Success) return m.Value.TrimEnd('.', ',', ';', ')', ']');
                }
            return null;
        }

        // ---------------- Meetings ----------------

        /// Meetings starting within [from, to] (UTC): real meetings only (a join link or other people),
        /// skipping all-day events, cancellations, and ones you declined.
        public static List<Meeting> Meetings(List<RawEvent> events, DateTime from, DateTime to)
        {
            var overridden = new HashSet<string>();
            foreach (var e in events)
                if (e.RecurrenceId.HasValue) overridden.Add(e.Uid + "|" + e.RecurrenceId.Value.Ticks);
            var res = new List<Meeting>();
            foreach (var e in events)
            {
                if (e.Start == null || e.Start.AllDay || e.Status == "CANCELLED" || e.SelfDeclined) continue;
                foreach (var occ in Occurrences(e, from, to))
                {
                    if (!e.RecurrenceId.HasValue && overridden.Contains(e.Uid + "|" + occ.Ticks)) continue;
                    var join = FindJoinUrl(e.Conference, e.Url, e.Location, e.Description);
                    if (join == null && e.OtherAttendees == 0) continue;
                    res.Add(new Meeting
                    {
                        Key = e.Uid + "|" + occ.Ticks,
                        Title = e.Summary.Length == 0 ? "Meeting" : e.Summary,
                        StartUtc = occ,
                        JoinUrl = join
                    });
                }
            }
            return res.OrderBy(m => m.StartUtc).ToList();
        }

        /// The secret feed URL embeds the calendar's email: .../calendar/ical/<email>/private-.../basic.ics
        public static string SelfEmail(string feedUrl)
        {
            try
            {
                var parts = new Uri(feedUrl).AbsolutePath.Split('/');
                int i = Array.IndexOf(parts, "ical");
                if (i >= 0 && i + 1 < parts.Length) return Uri.UnescapeDataString(parts[i + 1]).ToLowerInvariant();
            }
            catch (UriFormatException) { }
            return "";
        }
    }
}
