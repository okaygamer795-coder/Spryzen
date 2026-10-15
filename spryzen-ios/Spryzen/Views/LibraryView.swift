import SwiftUI

/// The app library: installed apps, days left, per-app refresh, IPA import.
/// Refresh has two modes (Settings → Signing method):
/// free Apple ID (portal steps run on-device, helper only resigns+installs)
/// or the user's own certificate (IPA + materials are sent to the helper).
struct LibraryView: View {
    @EnvironmentObject private var library: LibraryStore
    @State private var showingPicker = false
    @State private var imports: [String] = []
    @State private var status: String = ""
    @State private var isWorking = false

    var body: some View {
        NavigationStack {
            List {
                Section("Installed") {
                    if library.apps.isEmpty {
                        Text("Nothing installed yet. Import an .ipa below, install it, and it appears here.")
                            .foregroundColor(.secondary)
                    }
                    ForEach(library.apps) { app in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(app.name).font(.headline)
                            Text(app.bundleID).font(.caption).foregroundColor(.secondary)
                            Text(app.daysLeft <= 2 ? "Expires in \(app.daysLeft) day(s) — refresh soon"
                                                   : "\(app.daysLeft) days left")
                                .font(.caption)
                                .foregroundColor(app.daysLeft <= 2 ? .red : .secondary)
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
                Section("Imports (tap Install to put one on the iPhone)") {
                    if imports.isEmpty {
                        Text("No .ipa files imported. Tap + above.")
                            .foregroundColor(.secondary)
                    }
                    ForEach(imports, id: \.self) { name in
                        HStack {
                            Text(name).font(.caption).lineLimit(1)
                            Spacer()
                            Button("Install") { Task { await installImport(name) } }
                                .font(.caption)
                                .disabled(isWorking)
                        }
                    }
                }
            }
            .navigationTitle("Spryzen")
            .toolbar {
                Button { showingPicker = true } label: { Image(systemName: "plus") }
            }
            .sheet(isPresented: $showingPicker) {
                DocumentPicker(fileExtension: "ipa") { url in importIPA(url) }
            }
            .onAppear { reloadImports() }
            if !status.isEmpty {
                Text(status).font(.caption).foregroundColor(.secondary).padding(.horizontal)
            }
        }
    }

    private func helperOrNil() -> String? {
        let helper = Settings.helperURL.trimmingCharacters(in: .whitespaces)
        if helper.isEmpty {
            status = "Set the helper URL in Settings first (SideLoadr on your PC)."
            return nil
        }
        return helper
    }

    private func reloadImports() {
        let files = (try? FileManager.default.contentsOfDirectory(at: Settings.importsDir, includingPropertiesForKeys: nil)) ?? []
        imports = files.filter { $0.pathExtension.lowercased() == "ipa" }.map { $0.lastPathComponent }.sorted()
    }

    private func importIPA(_ url: URL) {
        let dest = Settings.importsDir.appendingPathComponent(url.lastPathComponent)
        do {
            try FileManager.default.createDirectory(at: Settings.importsDir, withIntermediateDirectories: true)
            if FileManager.default.fileExists(atPath: dest.path) {
                try FileManager.default.removeItem(at: dest)
            }
            try FileManager.default.copyItem(at: url, to: dest)
            reloadImports()
            status = "Imported \(url.lastPathComponent). Tap Install to put it on the iPhone."
        } catch {
            status = "Import failed: \(error.localizedDescription)"
        }
    }

    private func installImport(_ name: String) async {
        guard let helper = helperOrNil() else { return }
        isWorking = true
        defer { isWorking = false }
        do {
            let url = Settings.importsDir.appendingPathComponent(name)
            let data = try Data(contentsOf: url)
            let res = try await HelperClient.installApp(helperURL: helper, fileName: name, ipaData: data)
            status = res.message
            if res.ok {
                library.recordInstalled(bundleID: res.bundleId ?? name, name: res.bundleId ?? name, udid: "", ipaPath: url.path)
            }
        } catch {
            status = "Install failed: \(error.localizedDescription)"
        }
    }

    private func refresh(_ app: SideloadedApp) async {
        guard let helper = helperOrNil() else { return }
        isWorking = true
        defer { isWorking = false }
        do {
            let res: HelperResponse
            if Settings.certMode == "custom" {
                res = try await customRefresh(helper: helper, app: app)
            } else {
                res = try await freeRefresh(helper: helper, app: app)
            }
            status = res.message
            if res.ok {
                library.recordInstalled(bundleID: app.bundleID, name: app.name, udid: app.udid, ipaPath: app.ipaPath)
            }
        } catch {
            status = "Refresh failed: \(error.localizedDescription)"
        }
    }

    private func freeRefresh(helper: String, app: SideloadedApp) async throws -> HelperResponse {
        let appleID = Settings.appleID
        guard !appleID.isEmpty,
              let password = KeychainHelper.load(account: "apple-password:\(appleID)"),
              !password.isEmpty else {
            throw RefreshError.signInFirst("Sign in with your free Apple ID in Settings first.")
        }
        let materials = try await FreeSigning.prepare(
            bundleID: app.bundleID, appleID: appleID, password: password,
            anisetteServerURL: Settings.anisetteServerURL, helperURL: helper,
            request2FACode: { throw RefreshError.signInFirst("Apple asked for a 2FA code — approve in Settings sign-in, then refresh.") })
        return try await HelperClient.refreshApp(
            helperURL: helper, udid: materials.udid, bundleID: app.bundleID,
            profileBase64: materials.profileB64, certPEM: materials.certPEM, keyPEM: materials.keyPEM)
    }

    private func customRefresh(helper: String, app: SideloadedApp) async throws -> HelperResponse {
        guard let p12 = KeychainHelper.loadData(account: "custom-p12"),
              let password = KeychainHelper.load(account: "custom-p12-password"),
              FileManager.default.fileExists(atPath: Settings.customProvisionURL.path) else {
            throw RefreshError.signInFirst("Import your .p12 + password + profile in Settings first.")
        }
        guard !app.ipaPath.isEmpty, FileManager.default.fileExists(atPath: app.ipaPath) else {
            throw RefreshError.signInFirst("Original IPA file is gone — re-import it, then refresh.")
        }
        let ipa = try Data(contentsOf: URL(fileURLWithPath: app.ipaPath))
        let profile = try Data(contentsOf: Settings.customProvisionURL)
        let udid = app.udid.isEmpty ? (try await HelperClient.devices(helperURL: helper).first ?? "") : app.udid
        return try await HelperClient.refreshApp(
            helperURL: helper, udid: udid, bundleID: app.bundleID,
            profileBase64: profile.base64EncodedString(),
            ipaBase64: ipa.base64EncodedString(), fileName: URL(fileURLWithPath: app.ipaPath).lastPathComponent,
            p12Base64: p12.base64EncodedString(), p12Password: password)
    }

    enum RefreshError: LocalizedError {
        case signInFirst(String)
        public var errorDescription: String? {
            if case .signInFirst(let m) = self { return m }
            return nil
        }
    }
}
