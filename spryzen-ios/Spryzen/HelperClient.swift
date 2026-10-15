import Foundation

/// Talks to the reachable helper (SideLoadr on a PC, or a self-hosted
/// helper on the local network) which performs the privileged steps:
/// resigned install / refresh over usbmux. Same split of responsibilities
/// as SideStore + SideServer: this app manages the library and drives the
/// protocol, the helper touches the device.
///
/// Helper API:
///   GET  {helper}/status                                        -> {ok, version}
///   POST {helper}/refresh  {udid, bundleID, profile, ipa_sha256} -> {ok, message}
///   POST {helper}/install?filename=x.ipa  (raw .ipa bytes)      -> {ok, message}
struct HelperResponse: Codable {
    var ok: Bool
    var message: String
    var bundleId: String?
}

struct HelperStatus: Codable {
    var ok: Bool
    var version: String
}

enum HelperClient {
    /// UDIDs of devices the helper currently sees over usbmux.
    static func devices(helperURL: String) async throws -> [String] {
        let url = URL(string: helperURL.hasSuffix("/") ? helperURL + "devices" : helperURL + "/devices")!
        let (data, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw HelperError.badStatus((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        return (try? JSONDecoder().decode([String].self, from: data)) ?? []
    }
    static func status(helperURL: String) async throws -> HelperStatus {
        let url = URL(string: helperURL.hasSuffix("/") ? helperURL + "status" : helperURL + "/status")!
        let (data, response) = try await URLSession.shared.data(from: url)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw HelperError.badStatus((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        return try JSONDecoder().decode(HelperStatus.self, from: data)
    }

    static func installApp(helperURL: String, fileName: String, ipaData: Data) async throws -> HelperResponse {
        var parts = URLComponents(string: helperURL.hasSuffix("/") ? helperURL + "install" : helperURL + "/install")!
        parts.queryItems = [URLQueryItem(name: "filename", value: fileName)]
        var req = URLRequest(url: parts.url!)
        req.httpMethod = "POST"
        req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        req.httpBody = ipaData
        let (data, response) = try await URLSession.shared.data(for: req)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw HelperError.badStatus((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        return try JSONDecoder().decode(HelperResponse.self, from: data)
    }
    /// Drives a refresh through the helper. Two modes, same endpoint:
    /// - free flow: Spryzen did the portal steps on-device and sends profile + cert + key.
    /// - custom flow: Spryzen sends its own IPA + .p12 + password + profile.
    /// - legacy: only udid + bundleID (helper uses its own saved credentials).
    static func refreshApp(
        helperURL: String,
        udid: String,
        bundleID: String,
        profileBase64: String = "",
        certPEM: String? = nil,
        keyPEM: String? = nil,
        ipaBase64: String? = nil,
        fileName: String? = nil,
        p12Base64: String? = nil,
        p12Password: String? = nil
    ) async throws -> HelperResponse {
        let url = URL(string: helperURL.hasSuffix("/") ? helperURL + "refresh" : helperURL + "/refresh")!
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        var body: [String: String] = ["udid": udid, "bundleID": bundleID, "profile": profileBase64, "ipa_sha256": ""]
        if let certPEM, !certPEM.isEmpty { body["certPem"] = certPEM }
        if let keyPEM, !keyPEM.isEmpty { body["keyPem"] = keyPEM }
        if let ipaBase64, !ipaBase64.isEmpty { body["ipaB64"] = ipaBase64 }
        if let fileName, !fileName.isEmpty { body["filename"] = fileName }
        if let p12Base64, !p12Base64.isEmpty { body["p12B64"] = p12Base64 }
        if let p12Password, !p12Password.isEmpty { body["p12Password"] = p12Password }
        req.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await URLSession.shared.data(for: req)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw HelperError.badStatus((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        return try JSONDecoder().decode(HelperResponse.self, from: data)
    }

    enum HelperError: LocalizedError {
        case badStatus(Int)
        public var errorDescription: String? {
            switch self {
            case .badStatus(let s): return "Helper returned status \(s). Is it running and reachable?"
            }
        }
    }
}
