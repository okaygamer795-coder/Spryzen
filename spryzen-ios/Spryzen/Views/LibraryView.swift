import SwiftUI

/// The app library: installed apps, days left, per-app refresh, IPA import.
struct LibraryView: View {
    @EnvironmentObject private var library: LibraryStore
    @State private var showingPicker = false
    @State private var status: String = ""
    @State private var isWorking = false

    var body: some View {
        NavigationStack {
            List {
                if library.apps.isEmpty {
                    Text("No apps yet. Tap + to import an .ipa, then install it with the SideLoadr helper.")
                        .foregroundStyle(.secondary)
                }
                ForEach(library.apps) { app in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(app.name).font(.headline)
                        Text(app.bundleID).font(.caption).foregroundStyle(.secondary)
                        Text(app.daysLeft <= 2 ? "Expires in \(app.daysLeft) day(s) — refresh soon"
                                               : "\(app.daysLeft) days left")
                            .font(.caption)
                            .foregroundStyle(app.daysLeft <= 2 ? .red : .secondary)
                        Button("Refresh now") { Task { await refresh(app) } }
                            .font(.caption)
                            .disabled(isWorking)
                    }
                    .padding(.vertical, 4)
                }
                .onDelete { indexSet in
                    for i in indexSet { library.remove(library.apps[i]) }
                }
            }
            .navigationTitle("Spryzen")
            .toolbar {
                Button { showingPicker = true } label: { Image(systemName: "plus") }
            }
            .sheet(isPresented: $showingPicker) {
                DocumentPicker { url in importIPA(url) }
            }
            if !status.isEmpty {
                Text(status).font(.caption).foregroundStyle(.secondary).padding(.horizontal)
            }
        }
    }

    private func importIPA(_ url: URL) {
        // Copy into our sandbox so the file survives after the picker closes.
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let dest = docs.appendingPathComponent("imports").appendingPathComponent(url.lastPathComponent)
        do {
            try FileManager.default.createDirectory(at: dest.deletingLastPathComponent(),
                                                    withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: dest.path) {
                try FileManager.default.removeItem(at: dest)
            }
            try FileManager.default.copyItem(at: url, to: dest)
            status = "Imported \(url.lastPathComponent). Install it with the SideLoadr helper, then it appears here."
        } catch {
            status = "Import failed: \(error.localizedDescription)"
        }
    }

    private func refresh(_ app: SideloadedApp) async {
        let helper = Settings.helperURL.trimmingCharacters(in: .whitespaces)
        guard !helper.isEmpty else {
            status = "Set the helper URL in Settings first (SideLoadr on your PC)."
            return
        }
        isWorking = true
        defer { isWorking = false }
        do {
            let res = try await HelperClient.refreshApp(
                helperURL: helper, udid: app.udid, bundleID: app.bundleID,
                profileBase64: "", ipaSHA256: "")
            status = res.message
            if res.ok {
                library.recordInstalled(bundleID: app.bundleID, name: app.name, udid: app.udid)
            }
        } catch {
            status = "Refresh failed: \(error.localizedDescription)"
        }
    }
}
