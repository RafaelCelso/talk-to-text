import OSLog

enum Log {
    static let audio = Logger(subsystem: "com.rafaelcelso.talktotext", category: "audio")
    static let speech = Logger(subsystem: "com.rafaelcelso.talktotext", category: "speech")
    static let hotkey = Logger(subsystem: "com.rafaelcelso.talktotext", category: "hotkey")
    static let inject = Logger(subsystem: "com.rafaelcelso.talktotext", category: "inject")
    static let app = Logger(subsystem: "com.rafaelcelso.talktotext", category: "app")
}
