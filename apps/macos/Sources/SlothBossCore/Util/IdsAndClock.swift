import Foundation

public enum Ids {
    /// A new lowercase UUID v4 string.
    public static func new() -> String { UUID().uuidString.lowercased() }

    /// Lowercase canonical 8-4-4-4-12 UUID strings only.
    public static func isValid(_ value: String?) -> Bool {
        guard let value, value.utf8.count == 36, value == value.lowercased() else { return false }
        let bytes = Array(value.utf8)
        for (i, b) in bytes.enumerated() {
            if i == 8 || i == 13 || i == 18 || i == 23 {
                if b != UInt8(ascii: "-") { return false }
            } else {
                let isHex = (b >= 48 && b <= 57) || (b >= 97 && b <= 102)
                if !isHex { return false }
            }
        }
        return UUID(uuidString: value) != nil
    }
}

public enum SBClock {
    private static let lock = NSLock()
    private static var nowProvider: @Sendable () -> Date = { Date() }

    /// Overridable for tests.
    public static var now: @Sendable () -> Date {
        get { lock.lock(); defer { lock.unlock() }; return nowProvider }
        set { lock.lock(); nowProvider = newValue; lock.unlock() }
    }

    private static let gregorianUTC: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()

    /// UTC ISO-8601 with milliseconds: 2026-10-24T09:30:00.000Z
    public static func stamp() -> String { format(now()) }

    public static func format(_ date: Date) -> String {
        let c = gregorianUTC.dateComponents([.year, .month, .day, .hour, .minute, .second, .nanosecond], from: date)
        let ms = min(999, (c.nanosecond ?? 0) / 1_000_000)
        return String(format: "%04ld-%02ld-%02ldT%02ld:%02ld:%02ld.%03ldZ",
                      c.year ?? 0, c.month ?? 0, c.day ?? 0, c.hour ?? 0, c.minute ?? 0, c.second ?? 0, ms)
    }

    /// Today's local calendar date, YYYY-MM-DD.
    public static func today() -> String { localDateString(now()) }

    public static func localDateString(_ date: Date) -> String {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone.current
        let c = cal.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04ld-%02ld-%02ld", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    /// Parses YYYY-MM-DD into components; nil if not a real calendar date.
    public static func dateParts(_ s: String?) -> (year: Int, month: Int, day: Int)? {
        guard let s else { return nil }
        let b = Array(s.utf8)
        guard b.count == 10, b[4] == UInt8(ascii: "-"), b[7] == UInt8(ascii: "-") else { return nil }
        func num(_ r: Range<Int>) -> Int? {
            var v = 0
            for i in r {
                guard b[i] >= 48 && b[i] <= 57 else { return nil }
                v = v * 10 + Int(b[i] - 48)
            }
            return v
        }
        guard let y = num(0..<4), let m = num(5..<7), let d = num(8..<10) else { return nil }
        guard y >= 1, (1...12).contains(m), d >= 1 else { return nil }
        let leap = (y % 4 == 0 && y % 100 != 0) || y % 400 == 0
        let days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][m - 1]
        guard d <= days else { return nil }
        return (y, m, d)
    }

    public static func isValidDate(_ s: String?) -> Bool { dateParts(s) != nil }

    /// Adds days to a YYYY-MM-DD date string (calendar arithmetic, no time zone effects).
    public static func addDays(_ s: String, _ days: Int) -> String? {
        guard let p = dateParts(s) else { return nil }
        guard let date = gregorianUTC.date(from: DateComponents(year: p.year, month: p.month, day: p.day)),
              let moved = gregorianUTC.date(byAdding: .day, value: days, to: date) else { return nil }
        let c = gregorianUTC.dateComponents([.year, .month, .day], from: moved)
        return String(format: "%04ld-%02ld-%02ld", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }

    /// Converts a YYYY-MM-DD string to a local Date at midnight (for date pickers).
    public static func localDate(from s: String?) -> Date? {
        guard let p = dateParts(s) else { return nil }
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone.current
        return cal.date(from: DateComponents(year: p.year, month: p.month, day: p.day))
    }

    /// Lenient timestamp check (the reference uses DateTimeOffset.TryParse): ISO-8601 with or
    /// without fractional seconds / zone, or a plain date.
    public static func isValidTimestamp(_ s: String?) -> Bool {
        guard let s = s?.trimmingCharacters(in: .whitespaces), !s.isEmpty else { return false }
        let withFraction = ISO8601DateFormatter()
        withFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if withFraction.date(from: s) != nil { return true }
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        if plain.date(from: s) != nil { return true }
        if isValidDate(s) { return true }
        let noZone = DateFormatter()
        noZone.locale = Locale(identifier: "en_US_POSIX")
        noZone.timeZone = TimeZone(identifier: "UTC")
        for pattern in ["yyyy-MM-dd'T'HH:mm:ss.SSS", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss"] {
            noZone.dateFormat = pattern
            if noZone.date(from: s) != nil { return true }
        }
        return false
    }
}
