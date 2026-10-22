// The full-screen, flashing alert shown on every display.

import AppKit

struct Alert {
    enum Kind { case meeting, reminder }
    let kind: Kind
    let title: String
    let target: Date  // meeting start, or the reminder's scheduled time
    let joinURL: URL?
}

// Zoom web links (https://x.zoom.us/j/123?pwd=abc) go through a browser page first;
// zoommtg:// opens the Zoom app straight into the meeting. Falls back to https if Zoom isn't installed.
func zoomAppURL(for url: URL) -> URL? {
    guard let host = url.host?.lowercased(), host.contains("zoom.") else { return nil }
    let parts = url.pathComponents
    guard let i = parts.firstIndex(where: { $0 == "j" || $0 == "w" }), i + 1 < parts.count else { return nil }
    guard NSWorkspace.shared.urlForApplication(toOpen: URL(string: "zoommtg://zoom.us")!) != nil else { return nil }
    var c = URLComponents()
    c.scheme = "zoommtg"; c.host = host; c.path = "/join"
    var items = [URLQueryItem(name: "action", value: "join"), URLQueryItem(name: "confno", value: parts[i + 1])]
    if let pwd = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "pwd" })?.value {
        items.append(URLQueryItem(name: "pwd", value: pwd))
    }
    c.queryItems = items
    return c.url
}

func openJoinLink(_ url: URL) {
    NSWorkspace.shared.open(zoomAppURL(for: url) ?? url)
}

final class OverlayController {
    private var windows: [NSWindow] = []
    private var flashTimer: Timer?
    private var tickTimer: Timer?
    private var headlineLabels: [NSTextField] = []
    private var countdownLabels: [NSTextField] = []
    private var flashOn = false
    private var alert: Alert?
    var onSnooze: ((Alert) -> Void)?
    var onClose: (() -> Void)?

    var isShowing: Bool { !windows.isEmpty }

    func show(_ a: Alert) {
        close()
        alert = a
        for screen in NSScreen.screens {
            let w = NSWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false, screen: screen)
            w.level = .screenSaver
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            w.isOpaque = false
            w.isReleasedWhenClosed = false
            w.backgroundColor = NSColor.systemRed.withAlphaComponent(0.92)
            w.contentView = buildContent(for: a, size: screen.frame.size)
            w.setFrame(screen.frame, display: true)
            w.orderFrontRegardless()
            windows.append(w)
        }
        NSApp.activate(ignoringOtherApps: true)
        windows.first?.makeKey()
        playAlertSound()

        flashTimer = Timer.scheduledTimer(withTimeInterval: 0.6, repeats: true) { [weak self] _ in self?.flash() }
        tickTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.tick() }
        tick()
    }

    func close() {
        flashTimer?.invalidate(); flashTimer = nil
        tickTimer?.invalidate(); tickTimer = nil
        windows.forEach { $0.orderOut(nil) }
        let wasShowing = !windows.isEmpty
        windows.removeAll()
        headlineLabels.removeAll()
        countdownLabels.removeAll()
        if wasShowing { onClose?() }
    }

    private func flash() {
        flashOn.toggle()
        let color = flashOn ? NSColor.systemOrange : NSColor.systemRed
        windows.forEach { $0.backgroundColor = color.withAlphaComponent(0.92) }
    }

    private func headline(for a: Alert) -> String {
        guard a.kind == .meeting else { return "⏰ REMINDER" }
        let mins = Int(ceil(a.target.timeIntervalSinceNow / 60))
        return mins > 0 ? "⏰ MEETING IN \(mins) MIN — JOIN NOW" : "⏰ MEETING STARTED — JOIN NOW"
    }

    private func tick() {
        guard let a = alert else { return }
        let secs = Int(a.target.timeIntervalSinceNow.rounded())
        let text: String
        switch a.kind {
        case .meeting:
            text = secs > 0
                ? String(format: "Starts in %d:%02d", secs / 60, secs % 60)
                : String(format: "STARTED %d:%02d AGO", (-secs) / 60, (-secs) % 60)
        case .reminder:
            let f = DateFormatter(); f.dateFormat = "HH:mm"
            text = "Set for \(f.string(from: a.target))"
        }
        let head = headline(for: a)
        countdownLabels.forEach { $0.stringValue = text }
        headlineLabels.forEach { $0.stringValue = head }
        // Re-ding every 30s so it can't fade into the background.
        if secs % 30 == 0 { playAlertSound() }
    }

    private func label(_ text: String, size: CGFloat, weight: NSFont.Weight) -> NSTextField {
        let l = NSTextField(labelWithString: text)
        l.font = .systemFont(ofSize: size, weight: weight)
        l.textColor = .white
        l.alignment = .center
        l.lineBreakMode = .byTruncatingTail
        l.maximumNumberOfLines = 2
        return l
    }

    private func button(_ title: String, size: CGFloat, action: Selector) -> NSButton {
        let b = NSButton(title: title, target: self, action: action)
        b.bezelStyle = .rounded
        b.controlSize = .large
        b.font = .systemFont(ofSize: size, weight: .bold)
        return b
    }

    private func buildContent(for a: Alert, size: CGSize) -> NSView {
        let root = NSView(frame: NSRect(origin: .zero, size: size))

        let headline = label(headline(for: a), size: 64, weight: .black)
        headlineLabels.append(headline)
        let title = label(a.title, size: 40, weight: .semibold)
        let countdown = label("", size: 32, weight: .medium)
        countdownLabels.append(countdown)

        let primaryTitle: String
        switch a.kind {
        case .meeting: primaryTitle = a.joinURL != nil ? "🚀  JOIN NOW" : "Open Google Calendar"
        case .reminder: primaryTitle = "✓  Got it"
        }
        let primary = button(primaryTitle, size: 32, action: #selector(primaryTapped))
        primary.keyEquivalent = "\r"
        let snooze = button("Snooze \(SettingsStore.shared.s.snoozeMinutes) min", size: 20, action: #selector(snoozeTapped))
        let dismiss = button("Dismiss", size: 20, action: #selector(dismissTapped))
        dismiss.keyEquivalent = "\u{1b}"

        let secondary = NSStackView(views: [snooze, dismiss])
        secondary.orientation = .horizontal
        secondary.spacing = 16

        let stack = NSStackView(views: [headline, title, countdown, primary, secondary])
        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 28
        stack.setCustomSpacing(48, after: countdown)
        stack.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: root.centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: root.centerYAnchor),
            stack.widthAnchor.constraint(lessThanOrEqualTo: root.widthAnchor, constant: -80),
            primary.widthAnchor.constraint(greaterThanOrEqualToConstant: 360),
            primary.heightAnchor.constraint(greaterThanOrEqualToConstant: 64),
        ])
        return root
    }

    @objc private func primaryTapped() {
        if let a = alert, a.kind == .meeting {
            if let url = a.joinURL { openJoinLink(url) } else { NSWorkspace.shared.open(URL(string: "https://calendar.google.com")!) }
        }
        close()
    }

    @objc private func snoozeTapped() {
        if let a = alert { onSnooze?(a) }
        close()
    }

    @objc private func dismissTapped() { close() }
}
