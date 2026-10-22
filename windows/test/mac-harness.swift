// Regenerates expected.txt from the Mac parser (ICS.swift), so both apps are held to the same answers:
//   swiftc -o /tmp/h windows/test/mac-harness.swift ICS.swift && /tmp/h windows/test/fixture.ics 2026-10-19T00:00:00Z 2026-11-03T00:00:00Z > windows/test/expected.txt
import Foundation
let a = CommandLine.arguments
let text = try! String(contentsOfFile: a[1], encoding: .utf8)
let iso = ISO8601DateFormatter()
let list = meetings(from: parseICS(text, selfEmail: "me@example.com"), from: iso.date(from: a[2])!, to: iso.date(from: a[3])!)
let f = DateFormatter(); f.timeZone = TimeZone(identifier: "UTC"); f.dateFormat = "yyyy-MM-dd'T'HH:mm'Z'"
for m in list { print("\(f.string(from: m.start)) | \(m.title) | \(m.joinURL?.absoluteString ?? "NONE")") }
