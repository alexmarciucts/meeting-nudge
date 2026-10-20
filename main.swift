// MeetingNudge — full-screen, impossible-to-ignore meeting reminders.
// Reads your Google Calendar through its secret iCal feed, takes over every screen
// before meetings and at your own reminders, and lives in the menu bar.
// Data lives in ~/Library/Application Support/MeetingNudge, the log in ~/Library/Logs/MeetingNudge.log.

import AppKit
import SwiftUI

let CHECK_SECONDS: Double = 15
let FETCH_SECONDS: Double = 180
let SUPPORT_DIR: URL = {
    let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("MeetingNudge")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir
}()
let FEED_URL_FILE = SUPPORT_DIR.appendingPathComponent("ical-url.txt").path
let LOG_FILE = NSHomeDirectory() + "/Library/Logs/MeetingNudge.log"

// MARK: - Google Calendar feed watcher

func feedURL() -> URL? {
    guard let raw = try? String(contentsOfFile: FEED_URL_FILE, encoding: .utf8) else { return nil }
    let link = raw.trimmingCharacters(in: .whitespacesAndNewlines)
    return link.isEmpty ? nil : URL(string: link)
}

// The link is a credential for the calendar: readable by this user only.
func saveFeedURL(_ link: String) throws {
    try link.write(toFile: FEED_URL_FILE, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: FEED_URL_FILE)
}

// The secret feed URL embeds the calendar's email: .../calendar/ical/<email>/private-.../basic.ics
func selfEmail(from url: URL) -> String {
    let parts = url.pathComponents
    guard let i = parts.firstIndex(of: "ical"), i + 1 < parts.count else { return "" }
    return (parts[i + 1].removingPercentEncoding ?? parts[i + 1]).lowercased()
}

/// Downloads the feed and returns meetings in the next 24h (nil on failure).
func fetchMeetings(_ url: URL, completion: @escaping ([Meeting]?) -> Void) {
    var req = URLRequest(url: url)
    req.timeoutInterval = 60
    URLSession.shared.dataTask(with: req) { data, resp, err in
        guard let data, (resp as? HTTPURLResponse)?.statusCode == 200, let text = String(data: data, encoding: .utf8) else {
            log("fetch failed: \(err?.localizedDescription ?? "HTTP \((resp as? HTTPURLResponse)?.statusCode ?? 0)")")
            completion(nil)
            return
        }
        let events = parseICS(text, selfEmail: selfEmail(from: url))
        let now = Date()
        completion(meetings(from: events, from: now.addingTimeInterval(-120), to: now.addingTimeInterval(86400)))
    }.resume()
}

func log(_ msg: String) {
    let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm:ss"
    let line = "[\(f.string(from: Date()))] \(msg)\n"
    if let h = FileHandle(forWritingAtPath: LOG_FILE) {
        h.seekToEndOfFile(); h.write(line.data(using: .utf8)!); try? h.close()
    } else {
        try? line.write(toFile: LOG_FILE, atomically: true, encoding: .utf8)
    }
}

final class Watcher {
    let overlay = OverlayController()
    private(set) var upcoming: [Meeting] = []
    private var alerted = Set<String>()
    private var snoozed: [(until: Date, alert: Alert)] = []
    var pausedUntil: Date?
    var onUpdate: (() -> Void)?

    var settings: AppSettings { SettingsStore.shared.s }
    var nextMeeting: Meeting? { upcoming.first { $0.start > Date() } }

    func start() {
        overlay.onSnooze = { [weak self] a in
            guard let self else { return }
            self.snoozed.append((Date().addingTimeInterval(Double(self.settings.snoozeMinutes) * 60), a))
        }
        Timer.scheduledTimer(withTimeInterval: CHECK_SECONDS, repeats: true) { [weak self] _ in self?.check() }
        Timer.scheduledTimer(withTimeInterval: FETCH_SECONDS, repeats: true) { [weak self] _ in self?.refresh() }
        refresh()
    }

    /// Re-fetches the calendar (also called right after a new link is saved).
    func refresh() {
        guard let url = feedURL() else {
            upcoming = []
            FeedStatus.shared.state = .notSet
            return
        }
        if case .ok = FeedStatus.shared.state {} else { FeedStatus.shared.state = .loading }
        fetchMeetings(url) { list in
            DispatchQueue.main.async {
                guard let list else {  // keep the last good list when offline
                    if case .ok = FeedStatus.shared.state {} else { FeedStatus.shared.state = .failed("check the link and your internet connection") }
                    return
                }
                FeedStatus.shared.state = .ok(list.count)
                self.upcoming = list
                log("feed ok: \(list.count) meetings in next 24h; next: \(list.first.map { "\($0.title) @ \($0.start)" } ?? "none")")
                self.check()
            }
        }
    }

    func check() {
        defer { onUpdate?() }
        guard !overlay.isShowing else { return }
        let now = Date()
        if let p = pausedUntil, p > now { return }
        pausedUntil = nil

        if let i = snoozed.firstIndex(where: { $0.until <= now }) {
            overlay.show(snoozed.remove(at: i).alert)
            return
        }

        for r in settings.reminders where r.enabled {
            guard let t = r.lastFire(atOrBefore: now), now.timeIntervalSince(t) < 120 else { continue }
            let key = "rem|\(r.id)|\(t.timeIntervalSince1970)"
            if alerted.contains(key) { continue }
            alerted.insert(key)
            log("reminder: \(r.title)")
            overlay.show(Alert(kind: .reminder, title: r.title.isEmpty ? "Reminder" : r.title, target: t, joinURL: nil))
            return
        }

        guard settings.meetingAlertsOn else { return }
        let leads = settings.leadMinutes.sorted()
        for m in upcoming where m.start > now.addingTimeInterval(-60) {
            // The most recently crossed alert time; earlier (bigger) ones count as done,
            // so starting the app late doesn't replay every alert back to back.
            guard let lead = leads.first(where: { now >= m.start.addingTimeInterval(-Double($0) * 60) }) else { continue }
            if alerted.contains("\(m.key)|\(lead)") { continue }
            for l in leads where l >= lead { alerted.insert("\(m.key)|\(l)") }
            log("alert (\(lead) min): \(m.title) @ \(m.start) join=\(m.joinURL?.absoluteString ?? "none")")
            overlay.show(Alert(kind: .meeting, title: m.title, target: m.start, joinURL: m.joinURL))
            return
        }
    }

    func showTest() {
        overlay.show(Alert(kind: .meeting, title: "Test alert — MeetingNudge", target: Date().addingTimeInterval(5 * 60), joinURL: nil))
    }

    // Prints the meetings the feed yields for the next 24h (debugging).
    func dump() {
        guard let url = feedURL() else { print("no feed url"); exit(1) }
        fetchMeetings(url) { list in
            let f = DateFormatter(); f.dateFormat = "EEE HH:mm"
            for m in list ?? [] { print("\(f.string(from: m.start))  \(m.title)  ->  \(m.joinURL?.absoluteString ?? "NO LINK")") }
            exit(list == nil ? 1 : 0)
        }
    }
}

// MARK: - Menu bar

final class AppController: NSObject, NSMenuDelegate {
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    let watcher = Watcher()
    private var settingsWindow: NSWindow?

    func start() {
        installEditMenu()
        statusItem.button?.image = NSImage(systemSymbolName: "alarm", accessibilityDescription: "MeetingNudge")
        statusItem.button?.imagePosition = .imageLeading
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu
        watcher.onUpdate = { [weak self] in self?.updateTitle() }
        watcher.start()
        updateTitle()
    }

    private func timeString(_ d: Date) -> String {
        let f = DateFormatter(); f.dateFormat = "HH:mm"; return f.string(from: d)
    }

    private func updateTitle() {
        let now = Date()
        var title = ""
        if let p = watcher.pausedUntil, p > now {
            title = " paused"
        } else if let m = watcher.nextMeeting {
            let mins = Int(ceil(m.start.timeIntervalSince(now) / 60))
            title = mins <= 60 ? " \(mins)m" : " \(timeString(m.start))"
        }
        statusItem.button?.title = title
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        if let m = watcher.nextMeeting {
            let mins = Int(ceil(m.start.timeIntervalSinceNow / 60))
            let info = NSMenuItem(title: "Next: \(m.title) — \(timeString(m.start)) (in \(mins) min)", action: nil, keyEquivalent: "")
            info.isEnabled = false
            menu.addItem(info)
            if m.joinURL != nil { menu.addItem(item("Join next meeting now", #selector(joinNext))) }
        } else {
            let info = NSMenuItem(title: "No meetings in the next 24h", action: nil, keyEquivalent: "")
            info.isEnabled = false
            menu.addItem(info)
        }
        menu.addItem(.separator())
        menu.addItem(item("Settings…", #selector(openSettings), key: ","))
        menu.addItem(item("Show test alert", #selector(showTest)))
        if let p = watcher.pausedUntil, p > Date() {
            menu.addItem(item("Resume alerts (paused until \(timeString(p)))", #selector(resume)))
        } else {
            menu.addItem(item("Pause alerts for 1 hour", #selector(pause)))
        }
        menu.addItem(.separator())
        menu.addItem(item("Quit MeetingNudge", #selector(quit), key: "q"))
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
        i.target = self
        return i
    }

    @objc private func joinNext() { if let url = watcher.nextMeeting?.joinURL { openJoinLink(url) } }
    @objc private func showTest() { watcher.showTest() }
    @objc private func pause() { watcher.pausedUntil = Date().addingTimeInterval(3600); updateTitle() }
    @objc private func resume() { watcher.pausedUntil = nil; watcher.check() }
    @objc private func quit() { NSApp.terminate(nil) }

    @objc func openSettings() {
        if settingsWindow == nil {
            let host = NSHostingController(rootView: SettingsView(
                onTest: { [weak self] in self?.watcher.showTest() },
                onLinkSaved: { [weak self] in FeedStatus.shared.state = .loading; self?.watcher.refresh() }))
            let w = NSWindow(contentViewController: host)
            w.title = "MeetingNudge"
            w.styleMask = [.titled, .closable, .miniaturizable]
            w.isReleasedWhenClosed = false
            w.center()
            settingsWindow = w
        }
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    // Menu-bar-only apps have no Edit menu, which is what routes ⌘C/⌘V/⌘W to text fields and windows.
    private func installEditMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem(); main.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Close Window", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        appItem.submenu = appMenu
        let editItem = NSMenuItem(); main.addItem(editItem)
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        NSApp.mainMenu = main
    }
}

// MARK: - Main

final class AppDelegate: NSObject, NSApplicationDelegate {
    let controller = AppController()

    func applicationDidFinishLaunching(_ n: Notification) {
        controller.start()
        if !SettingsStore.shared.s.loginItemConfigured {
            setOpenAtLogin(true)
            SettingsStore.shared.s.loginItemConfigured = true
        }
        if feedURL() == nil { controller.openSettings() }  // first run: connect the calendar
    }

    // Opening the app again (Finder, Spotlight) while it's running shows Settings.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        controller.openSettings()
        return false
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = AppDelegate()  // NSApplication holds its delegate weakly
if CommandLine.arguments.contains("--dump") {
    Watcher().dump()
} else {
    app.delegate = delegate
}
app.run()
