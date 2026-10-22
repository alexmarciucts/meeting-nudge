// Settings (saved to ~/Library/Application Support/MeetingNudge/settings.json) and the SwiftUI settings window.

import ServiceManagement
import SwiftUI

let SETTINGS_FILE = SUPPORT_DIR.appendingPathComponent("settings.json").path

enum RepeatRule: String, Codable, CaseIterable, Identifiable {
    case once, daily, weekdays, weekly
    var id: String { rawValue }
    var label: String {
        switch self {
        case .once: return "Once"
        case .daily: return "Every day"
        case .weekdays: return "Weekdays"
        case .weekly: return "Every week"
        }
    }
}

struct CustomReminder: Codable, Identifiable, Equatable {
    var id = UUID()
    var title = ""
    var time = Date()  // full date for "once"/"weekly" (its weekday), time of day otherwise
    var repeatRule: RepeatRule = .once
    var enabled = true

    private func candidate(onDayOffset d: Int, from now: Date) -> Date? {
        let cal = Calendar.current
        guard let day = cal.date(byAdding: .day, value: d, to: now) else { return nil }
        let hm = cal.dateComponents([.hour, .minute], from: time)
        let weekday = cal.component(.weekday, from: day)
        switch repeatRule {
        case .once: return nil
        case .daily: break
        case .weekdays: if weekday == 1 || weekday == 7 { return nil }
        case .weekly: if weekday != cal.component(.weekday, from: time) { return nil }
        }
        return cal.date(bySettingHour: hm.hour ?? 0, minute: hm.minute ?? 0, second: 0, of: day)
    }

    /// Latest scheduled time at or before `now`.
    func lastFire(atOrBefore now: Date) -> Date? {
        if repeatRule == .once { return time <= now ? time : nil }
        for d in 0...7 { if let c = candidate(onDayOffset: -d, from: now), c <= now { return c } }
        return nil
    }

    /// Next scheduled time after `now`.
    func nextFire(after now: Date) -> Date? {
        if repeatRule == .once { return time > now ? time : nil }
        for d in 0...7 { if let c = candidate(onDayOffset: d, from: now), c > now { return c } }
        return nil
    }
}

struct AppSettings: Codable {
    var meetingAlertsOn = true
    var leadMinutes: [Int] = [5]
    var snoozeMinutes = 1
    var reminders: [CustomReminder] = []
    var loginItemConfigured = false  // first launch turns "Open at login" on once
    var soundName = "Sosumi"  // a macOS system sound, or "None"

    init() {}

    // Missing keys fall back to defaults, so adding a setting never wipes someone's reminders.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AppSettings()
        meetingAlertsOn = try c.decodeIfPresent(Bool.self, forKey: .meetingAlertsOn) ?? d.meetingAlertsOn
        leadMinutes = try c.decodeIfPresent([Int].self, forKey: .leadMinutes) ?? d.leadMinutes
        snoozeMinutes = try c.decodeIfPresent(Int.self, forKey: .snoozeMinutes) ?? d.snoozeMinutes
        reminders = try c.decodeIfPresent([CustomReminder].self, forKey: .reminders) ?? d.reminders
        loginItemConfigured = try c.decodeIfPresent(Bool.self, forKey: .loginItemConfigured) ?? d.loginItemConfigured
        soundName = try c.decodeIfPresent(String.self, forKey: .soundName) ?? d.soundName
    }
}

/// Live state of the Google Calendar feed, shown in Settings.
final class FeedStatus: ObservableObject {
    static let shared = FeedStatus()
    enum State: Equatable { case notSet, loading, ok(Int), failed(String) }
    @Published var state: State = .notSet
}

final class SettingsStore: ObservableObject {
    static let shared = SettingsStore()
    @Published var s: AppSettings { didSet { save() } }

    private init() {
        if let data = FileManager.default.contents(atPath: SETTINGS_FILE),
           let loaded = try? JSONDecoder().decode(AppSettings.self, from: data) {
            s = loaded
        } else {
            s = AppSettings()
        }
    }

    private func save() {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let data = try? enc.encode(s) { try? data.write(to: URL(fileURLWithPath: SETTINGS_FILE), options: .atomic) }
    }
}

// MARK: - UI

struct SettingsView: View {
    @ObservedObject var store = SettingsStore.shared
    @ObservedObject var feed = FeedStatus.shared
    @State private var newLead = 10
    @State private var linkDraft = ""
    @State private var editingLink = feedURL() == nil
    @State private var linkError = ""
    @State private var openAtLogin = SMAppService.mainApp.status == .enabled
    let onTest: () -> Void
    let onLinkSaved: () -> Void

    private var feedStatusText: String {
        switch feed.state {
        case .notSet: return "Not connected"
        case .loading: return "Loading your calendar… (Google takes up to ~30s)"
        case .ok(let n): return "✅ Connected — \(n) meeting\(n == 1 ? "" : "s") in the next 24h"
        case .failed(let msg): return "⚠️ Couldn't load your calendar: \(msg)"
        }
    }

    private func saveLink() {
        let link = linkDraft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard link.hasPrefix("https://calendar.google.com/calendar/ical/"), link.hasSuffix(".ics") else {
            linkError = "That doesn't look like the \"Secret address in iCal format\" — it starts with https://calendar.google.com/calendar/ical/ and ends with .ics"
            return
        }
        do {
            try saveFeedURL(link)
            linkError = ""; linkDraft = ""; editingLink = false
            onLinkSaved()
        } catch {
            linkError = "Couldn't save: \(error.localizedDescription)"
        }
    }

    var body: some View {
        Form {
            Section("Google Calendar") {
                if editingLink {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("1. Open calendar.google.com → ⚙️ → Settings")
                        Text("2. Left sidebar: click your calendar → \"Integrate calendar\"")
                        Text("3. Copy \"Secret address in iCal format\" (not the public one)")
                        Text("4. Paste it below. It stays on this Mac only — treat it like a password.")
                    }
                    .font(.callout).foregroundStyle(.secondary)
                    HStack {
                        SecureField("https://calendar.google.com/calendar/ical/…/basic.ics", text: $linkDraft)
                            .textFieldStyle(.roundedBorder)
                            .onSubmit(saveLink)
                        Button("Connect", action: saveLink).keyboardShortcut(.defaultAction)
                    }
                    if !linkError.isEmpty { Text(linkError).font(.caption).foregroundStyle(.red) }
                } else {
                    HStack {
                        Text(feedStatusText)
                        Spacer()
                        Button("Change link") { editingLink = true }
                    }
                }
            }

            Section("Meeting alerts") {
                Toggle("Full-screen alert before every meeting", isOn: $store.s.meetingAlertsOn)
                HStack(spacing: 8) {
                    Text("Alert me")
                    ForEach(store.s.leadMinutes.sorted(by: >), id: \.self) { m in
                        HStack(spacing: 4) {
                            Text("\(m) min")
                            Button { store.s.leadMinutes.removeAll { $0 == m } } label: { Image(systemName: "xmark.circle.fill") }
                                .buttonStyle(.borderless)
                        }
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Capsule().fill(Color.accentColor.opacity(0.18)))
                    }
                    if store.s.leadMinutes.isEmpty { Text("— no alerts").foregroundStyle(.secondary) }
                    Text("before")
                }
                HStack {
                    Stepper("Add alert \(newLead) min before", value: $newLead, in: 1...240)
                    Spacer()
                    Button("Add") {
                        if !store.s.leadMinutes.contains(newLead) { store.s.leadMinutes.append(newLead) }
                    }
                }
                Stepper("Snooze length: \(store.s.snoozeMinutes) min", value: $store.s.snoozeMinutes, in: 1...30)
                HStack {
                    Picker("Alert sound", selection: $store.s.soundName) {
                        ForEach(alertSoundNames(), id: \.self) { Text($0).tag($0) }
                    }
                    Button("Preview") { playAlertSound() }
                        .disabled(store.s.soundName == NO_SOUND)
                }
            }

            Section("My reminders") {
                if store.s.reminders.isEmpty {
                    Text("Nothing yet — add things like \"Stand up & stretch\" or \"Send timesheet\".")
                        .foregroundStyle(.secondary)
                }
                ForEach($store.s.reminders) { $r in
                    ReminderRow(r: $r) { store.s.reminders.removeAll { $0.id == r.id } }
                }
                Button {
                    let start = Calendar.current.date(byAdding: .minute, value: 30, to: Date())!
                    store.s.reminders.append(CustomReminder(title: "", time: start))
                } label: { Label("Add reminder", systemImage: "plus") }
            }

            Section {
                Toggle("Open MeetingNudge when I log in", isOn: $openAtLogin)
                    .onChange(of: openAtLogin) { _, on in setOpenAtLogin(on) }
                HStack {
                    Text("Settings save automatically.").foregroundStyle(.secondary)
                    Spacer()
                    Button("Show test alert", action: onTest)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 580, height: 720)
    }
}

struct ReminderRow: View {
    @Binding var r: CustomReminder
    let onDelete: () -> Void

    private var nextText: String {
        guard r.enabled else { return "Off" }
        guard let next = r.nextFire(after: Date()) else { return "Done" }
        let f = DateFormatter()
        f.dateFormat = Calendar.current.isDateInToday(next) ? "'Today' HH:mm" : "EEE d MMM, HH:mm"
        return "Next: \(f.string(from: next))"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Toggle("", isOn: $r.enabled).labelsHidden().toggleStyle(.switch).controlSize(.small)
                TextField("Remind me to…", text: $r.title).textFieldStyle(.roundedBorder)
                Button(action: onDelete) { Image(systemName: "trash") }.buttonStyle(.borderless)
            }
            HStack {
                Picker("", selection: $r.repeatRule) {
                    ForEach(RepeatRule.allCases) { Text($0.label).tag($0) }
                }
                .labelsHidden().frame(width: 120)
                DatePicker("", selection: $r.time,
                           displayedComponents: r.repeatRule == .once || r.repeatRule == .weekly ? [.date, .hourAndMinute] : [.hourAndMinute])
                    .labelsHidden()
                Spacer()
                Text(nextText).font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 4)
        .opacity(r.enabled ? 1 : 0.55)
    }
}

func setOpenAtLogin(_ on: Bool) {
    do {
        if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
    } catch {
        log("open at login \(on ? "register" : "unregister") failed: \(error.localizedDescription)")
    }
}

// MARK: - Alert sound

let NO_SOUND = "None"

/// "None" plus the system sounds (and any the user added to ~/Library/Sounds).
func alertSoundNames() -> [String] {
    let dirs = ["/System/Library/Sounds", NSHomeDirectory() + "/Library/Sounds"]
    var names = Set<String>()
    for dir in dirs {
        for f in (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [] {
            names.insert((f as NSString).deletingPathExtension)
        }
    }
    return [NO_SOUND] + names.sorted()
}

private var currentSound: NSSound?

func playAlertSound() {
    let name = SettingsStore.shared.s.soundName
    guard name != NO_SOUND else { return }
    currentSound?.stop()
    currentSound = NSSound(named: name) ?? NSSound(named: "Sosumi")
    currentSound?.play()
}
