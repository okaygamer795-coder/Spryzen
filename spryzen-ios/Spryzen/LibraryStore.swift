import Foundation

/// On-device library: sideloaded apps + 7-day expiry tracking, stored as JSON.
/// Non-secret settings only (UserDefaults); secrets stay in the Keychain.
@MainActor
final class LibraryStore: ObservableObject {
    @Published private(set) var apps: [SideloadedApp] = []

    private var fileURL: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("spryzen-library.json")
    }

    init() { load() }

    func load() {
        guard let data = try? Data(contentsOf: fileURL),
              let decoded = try? JSONDecoder().decode([SideloadedApp].self, from: data) else { return }
        apps = decoded.sorted { $0.expiresAt < $1.expiresAt }
    }

    func recordInstalled(bundleID: String, name: String, udid: String) {
        apps.removeAll { $0.bundleID == bundleID && $0.udid == udid }
        let now = Date()
        apps.append(SideloadedApp(bundleID: bundleID, name: name, udid: udid,
                                  installedAt: now, expiresAt: now.addingTimeInterval(7 * 86400)))
        save()
    }

    func remove(_ app: SideloadedApp) {
        apps.removeAll { $0.id == app.id }
        save()
    }

    private func save() {
        apps.sort { $0.expiresAt < $1.expiresAt }
        try? JSONEncoder().encode(apps).write(to: fileURL, options: .atomic)
    }
}

/// Non-secret settings.
enum Settings {
    private static let defaults = UserDefaults.standard
    static var appleID: String {
        get { defaults.string(forKey: "appleID") ?? "" }
        set { defaults.set(newValue, forKey: "appleID") }
    }
    static var anisetteServerURL: String {
        get {
            let v = defaults.string(forKey: "anisetteServerURL") ?? ""
            return v.isEmpty ? AnisetteClient.defaultServerURL : v
        }
        set { defaults.set(newValue, forKey: "anisetteServerURL") }
    }
    static var helperURL: String {
        get { defaults.string(forKey: "helperURL") ?? "" }
        set { defaults.set(newValue, forKey: "helperURL") }
    }
}
