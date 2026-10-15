import SwiftUI
import BackgroundTasks

@main
struct SpryzenApp: App {
    @StateObject private var library = LibraryStore()

    var body: some Scene {
        WindowGroup {
            TabView {
                LibraryView()
                    .tabItem { Label("Library", systemImage: "app.badge") }
                SettingsView()
                    .tabItem { Label("Settings", systemImage: "gear") }
            }
            .environmentObject(library)
        }
        .backgroundTask(.appRefresh("com.spryzen.app.refresh")) {
            await refreshExpiringApps()
        }
    }

    /// Background refresh: re-drives expiry for apps with <= 2 days left.
    /// Full re-sign still needs the helper to be reachable (see README).
    private func refreshExpiringApps() async {
        for app in await library.apps where app.daysLeft <= 2 {
            // Placeholder hook: schedule a local notification / attempt helper refresh.
            // Kept minimal until the helper pairing flow is validated on-device.
            print("Spryzen: \(app.name) expires in \(app.daysLeft) day(s)")
        }
    }
}
