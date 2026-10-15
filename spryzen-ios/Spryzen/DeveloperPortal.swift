import Foundation

/// Apple Developer Portal client for the free-account flow, mirroring the
/// SideLoadr PC app: register App ID + device, issue a development certificate
/// from our CSR, download the (7-day) provisioning profile.
/// NOTE: endpoint shapes must be validated against a real free Apple ID on
/// first run; every failure surfaces the server message.
enum DeveloperPortal {
    static let base = "https://developerservices2.apple.com/services/v1"

    static func ensureAppID(bundleID: String, teamID: String) async throws {
        let url = URL(string: base + "/bundleIds")!
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONSerialization.data(withJSONObject: [
            "teamId": teamID, "identifier": bundleID, "name": bundleID, "type": "explicit",
        ])
        let (data, response) = try await URLSession.shared.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? -1
        let body = String(data: data, encoding: .utf8) ?? ""
        if !(200..<300).contains(status) && !body.localizedCaseInsensitiveContains("already") {
            throw PortalError.requestFailed("App ID registration (\(status)): \(body.prefix(300))")
        }
    }

    static func ensureDevice(udid: String, teamID: String) async throws {
        let url = URL(string: base + "/devices")!
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONSerialization.data(withJSONObject: [
            "teamId": teamID, "udid": udid, "name": "Spryzen device", "platform": "ios",
        ])
        let (data, response) = try await URLSession.shared.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? -1
        let body = String(data: data, encoding: .utf8) ?? ""
        if !(200..<300).contains(status) && !body.localizedCaseInsensitiveContains("already") {
            throw PortalError.requestFailed("Device registration (\(status)): \(body.prefix(300))")
        }
    }

    /// Submits a PKCS#10 CSR, returns the issued certificate PEM.
    static func fetchDevelopmentCert(csrDER: Data, teamID: String) async throws -> String {
        let url = URL(string: base + "/certificates")!
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONSerialization.data(withJSONObject: [
            "teamId": teamID,
            "csrContent": csrDER.base64EncodedString(),
            "certificateType": "IOS_DEVELOPMENT",
        ])
        let (data, response) = try await URLSession.shared.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? -1
        guard (200..<300).contains(status),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let cert = json["certificateContent"] as? String, !cert.isEmpty else {
            throw PortalError.requestFailed("Certificate request (\(status)): \(String(data: data, encoding: .utf8)?.prefix(300) ?? "")")
        }
        return cert
    }

    /// Downloads the provisioning profile for the bundle ID. Returns raw bytes.
    static func fetchProfile(bundleID: String, teamID: String) async throws -> Data {
        var parts = URLComponents(string: base + "/profiles")!
        parts.queryItems = [URLQueryItem(name: "teamId", value: teamID)]
        let (data, response) = try await URLSession.shared.data(from: parts.url!)
        let status = (response as? HTTPURLResponse)?.statusCode ?? -1
        guard (200..<300).contains(status) else {
            throw PortalError.requestFailed("Profile download (\(status)): \(String(data: data, encoding: .utf8)?.prefix(300) ?? "")")
        }
        guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else {
            throw PortalError.requestFailed("Unexpected profile response.")
        }
        for p in list {
            if let bid = p["bundleId"] as? String,
               bid.caseInsensitiveCompare(bundleID) == .orderedSame,
               let b64 = p["profileContent"] as? String,
               let profile = Data(base64Encoded: b64) {
                return profile
            }
        }
        throw PortalError.requestFailed("No profile for this App ID (free accounts cap at ~10 IDs / 3 apps).")
    }

    enum PortalError: LocalizedError {
        case requestFailed(String)
        public var errorDescription: String? {
            if case .requestFailed(let m) = self { return m }
            return nil
        }
    }
}
