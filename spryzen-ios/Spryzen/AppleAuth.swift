import Foundation

/// Apple ID login for the free-provisioning flow (same shape as the
/// SideLoadr PC app: Apple ID + password, 2FA code when prompted).
/// Session tokens live in the Keychain; the password is kept in the
/// Keychain too so background refresh can re-authenticate.
enum AppleAuth {
    struct LoginResult {
        var session: AppleSession
    }

    static func login(
        appleID: String,
        password: String,
        anisetteServerURL: String,
        request2FACode: () async throws -> String
    ) async throws -> LoginResult {
        let anisette = try await AnisetteClient.fetch(serverURL: anisetteServerURL)

        var signin = URLRequest(url: URL(string: "https://idmsa.apple.com/appleauth/auth/signin")!)
        signin.httpMethod = "POST"
        signin.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let anisetteJSON = try JSONSerialization.data(withJSONObject: anisette)
        signin.setValue(anisetteJSON.base64EncodedString(), forHTTPHeaderField: "X-Apple-Anisette-Headers")
        signin.httpBody = try JSONSerialization.data(withJSONObject: [
            "accountName": appleID, "password": password, "rememberMe": false,
        ])
        let (_, signinResponse) = try await URLSession.shared.data(for: signin)
        let status = (signinResponse as? HTTPURLResponse)?.statusCode ?? -1
        if status == 409 {
            let code = try await request2FACode()
            try await submit2FA(code: code)
        } else if status < 200 || status >= 300 {
            throw AuthError.signinFailed(status)
        }

        let session = try await fetchFreeTeam(appleID: appleID)
        KeychainHelper.save(password, account: "apple-password:\(appleID)")
        return LoginResult(session: session)
    }

    private static func submit2FA(code: String) async throws {
        var req = URLRequest(url: URL(string: "https://idmsa.apple.com/appleauth/auth/verify/trusteddevice/securitycode")!)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONSerialization.data(withJSONObject: ["securityCode": ["code": code]])
        let (_, response) = try await URLSession.shared.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? -1
        guard (200..<300).contains(status) else { throw AuthError.twoFactorFailed(status) }
    }

    private static func fetchFreeTeam(appleID: String) async throws -> AppleSession {
        let url = URL(string: "https://developerservices2.apple.com/services/v1/teams")!
        let (data, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw AuthError.teamsFailed((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        // NOTE: exact response shape must be validated against a real free
        // Apple ID on first run; errors surface in the UI.
        guard let teams = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else {
            throw AuthError.unexpectedResponse
        }
        for team in teams {
            let type = (team["type"] as? String ?? "").lowercased()
            if type.contains("free") || type.contains("personal"),
               let teamID = team["teamId"] as? String {
                let name = team["name"] as? String ?? teamID
                return AppleSession(appleID: appleID, teamID: teamID, teamName: name)
            }
        }
        throw AuthError.noFreeTeam
    }

    enum AuthError: LocalizedError {
        case signinFailed(Int), twoFactorFailed(Int), teamsFailed(Int), noFreeTeam, unexpectedResponse
        public var errorDescription: String? {
            switch self {
            case .signinFailed(let s): return "Apple sign-in failed (\(s)). Try an app-specific password."
            case .twoFactorFailed(let s): return "2FA verification failed (\(s))."
            case .teamsFailed(let s): return "Could not list developer teams (\(s))."
            case .noFreeTeam: return "No free/personal team on this Apple ID."
            case .unexpectedResponse: return "Unexpected response from Apple."
            }
        }
    }
}
