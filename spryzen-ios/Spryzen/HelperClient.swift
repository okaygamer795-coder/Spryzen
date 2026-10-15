import Foundation

/// Talks to the reachable helper (SideLoadr on a PC, or a self-hosted
/// helper on the local network) which performs the privileged steps:
/// resigned install / refresh over usbmux. Same split of responsibilities
/// as SideStore + SideServer: this app manages the library and drives the
/// protocol, the helper touches the device.
///
/// Helper API (POST JSON, returns JSON):
///   POST {helper}/refresh  {udid, bundleID, profile, ipa_sha256} -> {ok, message}
///   POST {helper}/install  (multipart: ipa + profile)             -> {ok, message}
struct HelperResponse: Codable {
    var ok: Bool
    var message: String
}

enum HelperClient {
    static func refreshApp(
        helperURL: String,
        udid: String,
        bundleID: String,
        profileBase64: String,
        ipaSHA256: String
    ) async throws -> HelperResponse {
        let url = URL(string: helperURL.hasSuffix("/") ? helperURL + "refresh" : helperURL + "/refresh")!
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONSerialization.data(withJSONObject: [
            "udid": udid, "bundleID": bundleID,
            "profile": profileBase64, "ipa_sha256": ipaSHA256,
        ])
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
