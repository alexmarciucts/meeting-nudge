// Minimal iCalendar (RFC 5545) reader for Google Calendar's secret iCal feed:
// parses VEVENTs, expands recurring meetings for a time window, applies
// moved/cancelled occurrences, and pulls out the join link.

import Foundation

struct ICSDate {
    let date: Date
    let allDay: Bool
    let tz: TimeZone
}

struct RawEvent {
    var uid = ""
    var summary = ""
    var description = ""
    var location = ""
    var url = ""
    var conference = ""
    var status = ""
    var start: ICSDate?
    var rrule: [String: String]?
    var exdates: [Date] = []
    var recurrenceId: Date?
    var otherAttendees = 0
    var selfDeclined = false
}

// MARK: - Parsing

private func splitProp(_ line: Substring) -> (name: String, params: [String: String], value: String)? {
    var inQuote = false
    var colon: Substring.Index?
    for i in line.utf8.indices {
        let c = line.utf8[i]
        if c == UInt8(ascii: "\"") { inQuote.toggle() } else if c == UInt8(ascii: ":") && !inQuote { colon = i; break }
    }
    guard let colon else { return nil }
    let parts = line[..<colon].split(separator: ";", omittingEmptySubsequences: false)
    var params: [String: String] = [:]
    for p in parts.dropFirst() {
        if let eq = p.firstIndex(of: "=") {
            params[p[..<eq].uppercased()] = String(p[p.index(after: eq)...]).trimmingCharacters(in: CharacterSet(charactersIn: "\""))
        }
    }
    return (parts[0].uppercased(), params, String(line[line.index(after: colon)...]))
}

func parseICSDate(_ value: String, _ params: [String: String]) -> ICSDate? {
    let v = value.trimmingCharacters(in: .whitespaces)
    let tz: TimeZone
    if v.hasSuffix("Z") { tz = TimeZone(identifier: "UTC")! }
    else if let id = params["TZID"], let t = TimeZone(identifier: id) { tz = t }
    else { tz = .current }
    let chars = Array(v.utf8)
    func num(_ at: Int, _ len: Int) -> Int? {
        guard at + len <= chars.count else { return nil }
        var n = 0
        for i in at..<(at + len) {
            let d = Int(chars[i]) - 48
            guard (0...9).contains(d) else { return nil }
            n = n * 10 + d
        }
        return n
    }
    guard let y = num(0, 4), let mo = num(4, 2), let d = num(6, 2) else { return nil }
    let allDay = params["VALUE"] == "DATE" || chars.count == 8
    var c = DateComponents(year: y, month: mo, day: d)
    if !allDay { c.hour = num(9, 2) ?? 0; c.minute = num(11, 2) ?? 0; c.second = num(13, 2) ?? 0 }
    var cal = Calendar(identifier: .gregorian)
    cal.timeZone = tz
    guard let date = cal.date(from: c) else { return nil }
    return ICSDate(date: date, allDay: allDay, tz: tz)
}

func parseICS(_ text: String, selfEmail: String) -> [RawEvent] {
    var events: [RawEvent] = []
    var current: RawEvent?
    var nested = 0  // depth of sub-components (VALARM) inside a VEVENT

    func handle(_ line: Substring) {
        if line.hasPrefix("BEGIN:") {
            if line == "BEGIN:VEVENT" { current = RawEvent(); nested = 0 } else if current != nil { nested += 1 }
            return
        }
        if line.hasPrefix("END:") {
            if line == "END:VEVENT" { if let e = current { events.append(e) }; current = nil } else if current != nil { nested -= 1 }
            return
        }
        guard current != nil, nested == 0, let p = splitProp(line) else { return }
        switch p.name {
        case "UID": current!.uid = p.value
        case "SUMMARY": current!.summary = unescapeText(p.value)
        case "DESCRIPTION": current!.description = p.value
        case "LOCATION": current!.location = p.value
        case "URL": current!.url = p.value
        case "X-GOOGLE-CONFERENCE": current!.conference = p.value
        case "STATUS": current!.status = p.value.uppercased()
        case "DTSTART": current!.start = parseICSDate(p.value, p.params)
        case "RECURRENCE-ID": current!.recurrenceId = parseICSDate(p.value, p.params)?.date
        case "EXDATE":
            for v in p.value.split(separator: ",") { if let d = parseICSDate(String(v), p.params) { current!.exdates.append(d.date) } }
        case "RRULE":
            var rule: [String: String] = [:]
            for part in p.value.split(separator: ";") {
                if let eq = part.firstIndex(of: "=") { rule[String(part[..<eq]).uppercased()] = String(part[part.index(after: eq)...]) }
            }
            current!.rrule = rule
        case "ATTENDEE":
            let cutype = p.params["CUTYPE"]?.uppercased() ?? "INDIVIDUAL"
            if !selfEmail.isEmpty && p.value.lowercased().contains(selfEmail) {
                current!.selfDeclined = p.params["PARTSTAT"]?.uppercased() == "DECLINED"
            } else if cutype == "INDIVIDUAL" || cutype == "GROUP" {
                current!.otherAttendees += 1
            }
        default: break
        }
    }

    // Unfold continuation lines (leading space/tab) while walking the text once.
    var pending = ""
    // Split on bytes: Swift treats "\r\n" as one Character, so splitting on "\n" would miss it.
    for bytes in text.utf8.split(separator: UInt8(ascii: "\n"), omittingEmptySubsequences: false) {
        var raw = Substring(bytes)
        if raw.utf8.last == UInt8(ascii: "\r") { raw = Substring(raw.utf8.dropLast()) }
        if let f = raw.first, f == " " || f == "\t" {
            pending += raw.dropFirst()
        } else {
            if !pending.isEmpty { handle(Substring(pending)) }
            pending = String(raw)
        }
    }
    if !pending.isEmpty { handle(Substring(pending)) }
    return events
}

func unescapeText(_ s: String) -> String {
    var out = ""
    var it = s.makeIterator()
    while let c = it.next() {
        if c == "\\", let n = it.next() {
            switch n {
            case "n", "N": out.append("\n")
            default: out.append(n)
            }
        } else {
            out.append(c)
        }
    }
    return out
}

// MARK: - Recurrence expansion

private func weekdayNum(_ s: String) -> Int? {
    ["SU": 1, "MO": 2, "TU": 3, "WE": 4, "TH": 5, "FR": 6, "SA": 7][s.uppercased()]
}

private func daysInMonth(_ cal: Calendar, _ monthStart: Date, byDay: [String], byMonthDay: [Int], bySetPos: [Int]) -> [Date] {
    let len = cal.range(of: .day, in: .month, for: monthStart)!.count
    var res: [Date] = []
    for md in byMonthDay {
        let d = md > 0 ? md : len + md + 1
        if (1...len).contains(d) { res.append(cal.date(byAdding: .day, value: d - 1, to: monthStart)!) }
    }
    for spec in byDay {
        guard let wd = weekdayNum(String(spec.suffix(2))) else { continue }
        let matches = (0..<len).compactMap { i -> Date? in
            let d = cal.date(byAdding: .day, value: i, to: monthStart)!
            return cal.component(.weekday, from: d) == wd ? d : nil
        }
        if let ord = Int(spec.dropLast(2)) {
            let idx = ord > 0 ? ord - 1 : matches.count + ord
            if matches.indices.contains(idx) { res.append(matches[idx]) }
        } else {
            res += matches
        }
    }
    res = Array(Set(res)).sorted()
    if !bySetPos.isEmpty {
        res = bySetPos.compactMap { pos in
            let idx = pos > 0 ? pos - 1 : res.count + pos
            return res.indices.contains(idx) ? res[idx] : nil
        }.sorted()
    }
    return res
}

/// Start times of `e` that fall within [from, to].
func occurrences(of e: RawEvent, from: Date, to: Date) -> [Date] {
    guard let s = e.start else { return [] }
    guard let rule = e.rrule else { return (s.date >= from && s.date <= to) ? [s.date] : [] }
    if s.date > to { return [] }

    var cal = Calendar(identifier: .gregorian)
    cal.timeZone = s.tz
    let freq = rule["FREQ"]?.uppercased() ?? ""
    let interval = max(1, Int(rule["INTERVAL"] ?? "") ?? 1)
    let until = rule["UNTIL"].flatMap { parseICSDate($0, ["TZID": s.tz.identifier]) }.map { u in
        u.allDay ? cal.date(byAdding: .day, value: 1, to: u.date)! : u.date
    }
    if let until, until < from { return [] }
    let count = rule["COUNT"].flatMap { Int($0) }
    let byDay = rule["BYDAY"]?.split(separator: ",").map(String.init) ?? []
    let byMonthDay = rule["BYMONTHDAY"]?.split(separator: ",").compactMap { Int($0) } ?? []
    let byMonth = rule["BYMONTH"]?.split(separator: ",").compactMap { Int($0) } ?? []
    let bySetPos = rule["BYSETPOS"]?.split(separator: ",").compactMap { Int($0) } ?? []
    let wkst = weekdayNum(rule["WKST"] ?? "MO") ?? 2

    let time = cal.dateComponents([.hour, .minute, .second], from: s.date)
    let startDay = cal.startOfDay(for: s.date)
    let startMonth = cal.date(from: cal.dateComponents([.year, .month], from: startDay))!
    let startYear = cal.date(from: cal.dateComponents([.year], from: startDay))!
    let startWeekday = cal.component(.weekday, from: startDay)
    let weekStart0 = cal.date(byAdding: .day, value: -((startWeekday - wkst + 7) % 7), to: startDay)!

    // Without COUNT we can jump straight to the periods near the window.
    var period = 0
    if count == nil {
        let unit: Calendar.Component
        switch freq {
        case "DAILY": unit = .day
        case "WEEKLY": unit = .weekOfYear
        case "MONTHLY": unit = .month
        default: unit = .year
        }
        let anchor = freq == "WEEKLY" ? weekStart0 : startDay
        let elapsed = cal.dateComponents([unit], from: anchor, to: from).value(for: unit) ?? 0
        period = max(0, elapsed / interval - 1)
    }

    var out: [Date] = []
    var n = 0
    while period < 100_000 {
        var days: [Date] = []
        switch freq {
        case "DAILY":
            days = [cal.date(byAdding: .day, value: period * interval, to: startDay)!]
        case "WEEKLY":
            let weekStart = cal.date(byAdding: .day, value: period * interval * 7, to: weekStart0)!
            let targets = byDay.isEmpty ? [startWeekday] : byDay.compactMap { weekdayNum(String($0.suffix(2))) }
            days = targets.map { cal.date(byAdding: .day, value: ($0 - wkst + 7) % 7, to: weekStart)! }.sorted()
        case "MONTHLY":
            let month = cal.date(byAdding: .month, value: period * interval, to: startMonth)!
            let md = byDay.isEmpty && byMonthDay.isEmpty ? [cal.component(.day, from: startDay)] : byMonthDay
            days = daysInMonth(cal, month, byDay: byDay, byMonthDay: md, bySetPos: bySetPos)
        case "YEARLY":
            let year = cal.date(byAdding: .year, value: period * interval, to: startYear)!
            let months = byMonth.isEmpty ? [cal.component(.month, from: startDay)] : byMonth
            for m in months.sorted() {
                let month = cal.date(byAdding: .month, value: m - 1, to: year)!
                let md = byDay.isEmpty && byMonthDay.isEmpty ? [cal.component(.day, from: startDay)] : byMonthDay
                days += daysInMonth(cal, month, byDay: byDay, byMonthDay: md, bySetPos: bySetPos)
            }
        default:
            return out
        }
        for day in days {
            guard let occ = cal.date(bySettingHour: time.hour ?? 0, minute: time.minute ?? 0, second: time.second ?? 0, of: day),
                  occ >= s.date else { continue }
            if let until, occ > until { return out }
            if let count, n >= count { return out }
            n += 1
            if occ > to { return out }
            if occ >= from && !e.exdates.contains(where: { abs($0.timeIntervalSince(occ)) < 1 }) { out.append(occ) }
        }
        period += 1
    }
    return out
}

// MARK: - Join links

private let joinPatterns: [NSRegularExpression] = [
    #"https://[A-Za-z0-9.-]*zoom(gov)?\.(us|com)/(j|w|my|s)/[^\s<>"'\\]+"#,
    #"zoommtg://[^\s<>"'\\]+"#,
    #"https://meet\.google\.com/[a-z]{3}-[a-z]{4}-[a-z]{3}"#,
    #"https://teams\.(microsoft|live)\.com/l/meetup-join/[^\s<>"'\\]+"#,
    #"https://[A-Za-z0-9.-]*(webex\.com|whereby\.com|around\.co)/[^\s<>"'\\]+"#,
].map { try! NSRegularExpression(pattern: $0, options: [.caseInsensitive]) }

func findJoinURL(conference: String, texts: [String]) -> URL? {
    if conference.hasPrefix("http"), let u = URL(string: conference) { return u }
    let cleaned = texts.map { unescapeText($0).replacingOccurrences(of: "&amp;", with: "&") }
    for regex in joinPatterns {
        for text in cleaned {
            let range = NSRange(text.startIndex..., in: text)
            if let m = regex.firstMatch(in: text, range: range), let r = Range(m.range, in: text) {
                let link = String(text[r]).trimmingCharacters(in: CharacterSet(charactersIn: ".,;)]"))
                if let u = URL(string: link) { return u }
            }
        }
    }
    return nil
}

// MARK: - Meetings from a feed

struct Meeting {
    let key: String
    let title: String
    let start: Date
    let joinURL: URL?
}

/// Meetings starting within [from, to]: real meetings only (has a join link or other people),
/// skipping all-day events, cancellations, and ones you declined.
func meetings(from events: [RawEvent], from: Date, to: Date) -> [Meeting] {
    // Moved/cancelled single occurrences override their recurring series.
    var overridden = Set<String>()
    for e in events where e.recurrenceId != nil {
        overridden.insert("\(e.uid)|\(e.recurrenceId!.timeIntervalSince1970)")
    }
    var out: [Meeting] = []
    for e in events {
        guard let s = e.start, !s.allDay, e.status != "CANCELLED", !e.selfDeclined else { continue }
        for occ in occurrences(of: e, from: from, to: to) {
            if e.recurrenceId == nil && overridden.contains("\(e.uid)|\(occ.timeIntervalSince1970)") { continue }
            let join = findJoinURL(conference: e.conference, texts: [e.url, e.location, e.description])
            guard join != nil || e.otherAttendees > 0 else { continue }
            out.append(Meeting(key: "\(e.uid)|\(occ.timeIntervalSince1970)",
                               title: e.summary.isEmpty ? "Meeting" : e.summary, start: occ, joinURL: join))
        }
    }
    return out.sorted { $0.start < $1.start }
}
