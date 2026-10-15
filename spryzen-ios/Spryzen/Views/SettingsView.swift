import SwiftUI

/// Apple ID, anisette server (with default), and helper URL.
struct SettingsView: View {
    @State private var appleID = Settings.appleID
    @State private var password = ""
    @State private var anisetteURL = Settings.anisetteServerURL
    @State private var helperURL = Settings.helperURL
    @State private var certMode = Settings.certMode
    @State private var p12Password = ""
    @State private var customStatus = ""
    @State private var showingP12Picker = false
    @State private var showingProvPicker = false
    @State private var status = ""
    @State private var isWorking = false

    var body: some View {
        NavigationStack {
            Form {
                Section("Apple ID (free account)") {
                    TextField("Email", text: $appleID)
                        .textContentType(.emailAddress)
                        .autocapitalization(.none)
                    SecureField("Password / app-specific password", text: $password)
                    Button("Sign in") { Task { await signIn() } }
                        .disabled(isWorking || appleID.isEmpty || password.isEmpty)
                }
                Section("Anisette server") {
                    TextField("Server URL", text: $anisetteURL)
                        .autocapitalization(.none)
                    Button("Restore default") {
                        anisetteURL = AnisetteClient.defaultServerURL
                    }
                    .font(.caption)
                }
                Section("Helper (SideLoadr on PC, same Wi-Fi)") {
                    TextField("http://<pc-ip>:<port>", text: $helperURL)
                        .autocapitalization(.none)
                }
                Section("Signing method") {
                    Picker("", selection: $certMode) {
                        Text("Free Apple ID").tag("free")
                        Text("My certificate").tag("custom")
                    }
                    .pickerStyle(.segmented)
                    .onChange(of: certMode) { Settings.certMode = $0 }
                    if certMode == "custom" {
                        SecureField("Certificate password", text: $p12Password)
                        Button("Import .p12 certificate") { showingP12Picker = true }
                        Button("Import .mobileprovision") { showingProvPicker = true }
                        Text(customCertSummary()).font(.caption).foregroundColor(.secondary)
                    }
                    if !customStatus.isEmpty {
                        Text(customStatus).font(.caption)
                    }
                }
                if !status.isEmpty {
                    Section { Text(status).font(.caption) }
                }
                Section {
                    Button("Save settings", action: save)
                    Button("Sign out", role: .destructive) {
                        KeychainHelper.delete(account: "apple-password:\(appleID)")
                        password = ""
                        status = "Signed out on this device."
                    }
                }
            }
            .navigationTitle("Settings")
            .sheet(isPresented: $showingP12Picker) {
                DocumentPicker(fileExtension: "p12") { url in importP12(url) }
            }
            .sheet(isPresented: $showingProvPicker) {
                DocumentPicker(fileExtension: "mobileprovision") { url in importProvision(url) }
            }
            .onAppear { customStatus = ""; _ = customCertSummary() }
        }
    }

    private func customCertSummary() -> String {
        let hasP12 = KeychainHelper.loadData(account: "custom-p12") != nil
        let hasProv = FileManager.default.fileExists(atPath: Settings.customProvisionURL.path)
        switch (hasP12, hasProv) {
        case (true, true): return "Certificate + profile ready."
        case (true, false): return "Certificate saved — still need the .mobileprovision."
        case (false, true): return "Profile saved — still need the .p12."
        case (false, false): return "No certificate imported yet (paid Apple ID: export .p12 + profile)."
        }
    }

    private func importP12(_ url: URL) {
        do {
            let data = try Data(contentsOf: url)
            guard !p12Password.isEmpty else {
                customStatus = "Type the certificate password first, then import again."
                return
            }
            guard KeychainHelper.saveData(data, account: "custom-p12"),
                  KeychainHelper.save(p12Password, account: "custom-p12-password") else {
                customStatus = "Could not save the certificate."
                return
            }
            customStatus = "Certificate saved."
        } catch {
            customStatus = "Import failed: \(error.localizedDescription)"
        }
    }

    private func importProvision(_ url: URL) {
        do {
            if FileManager.default.fileExists(atPath: Settings.customProvisionURL.path) {
                try FileManager.default.removeItem(at: Settings.customProvisionURL)
            }
            try FileManager.default.copyItem(at: url, to: Settings.customProvisionURL)
            customStatus = "Profile saved."
        } catch {
            customStatus = "Import failed: \(error.localizedDescription)"
        }
    }

    private func save() {
        Settings.appleID = appleID.trimmingCharacters(in: .whitespaces)
        Settings.anisetteServerURL = anisetteURL.trimmingCharacters(in: .whitespaces)
        Settings.helperURL = helperURL.trimmingCharacters(in: .whitespaces)
        Settings.certMode = certMode
        status = "Saved."
    }

    private func signIn() async {
        save()
        isWorking = true
        defer { isWorking = false }
        do {
            let result = try await AppleAuth.login(
                appleID: Settings.appleID,
                password: password,
                anisetteServerURL: Settings.anisetteServerURL,
                request2FACode: {
                    throw SettingsError.twoFactorNotSupportedYet
                })
            status = "Signed in (\(result.session.teamName))."
        } catch {
            status = "Sign-in failed: \(error.localizedDescription)"
        }
    }

    enum SettingsError: LocalizedError {
        case twoFactorNotSupportedYet
        public var errorDescription: String? {
            "2FA codes are entered during refresh on this build — approve on a trusted device first."
        }
    }
}
