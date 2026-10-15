import SwiftUI

/// Apple ID, anisette server (with default), and helper URL.
struct SettingsView: View {
    @State private var appleID = Settings.appleID
    @State private var password = ""
    @State private var anisetteURL = Settings.anisetteServerURL
    @State private var helperURL = Settings.helperURL
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
        }
    }

    private func save() {
        Settings.appleID = appleID.trimmingCharacters(in: .whitespaces)
        Settings.anisetteServerURL = anisetteURL.trimmingCharacters(in: .whitespaces)
        Settings.helperURL = helperURL.trimmingCharacters(in: .whitespaces)
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
