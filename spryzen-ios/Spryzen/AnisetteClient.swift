import Foundation

/// Fetches anisette provisioning data from an anisette server.
/// Different servers expose it at different paths, so both
/// "{base}/anisette" and "{base}" are tried (first success wins).
enum AnisetteClient {
    /// Well-known public server (run by the SideStore team).
    /// Verify/replace in Settings if it ever stops working — or self-host.
    static let defaultServerURL = "https://ani.sidestore.io/v1/"

    static func fetch(serverURL: String) async throws -> [String: String] {
        let base = serverURL.hasSuffix("/") ? String(serverURL.dropLast()) : serverURL
        let candidates = base.hasSuffix("/anisette") ? [base] : [base + "/anisette", base]
        var errors: [String] = []
        for urlString in candidates {
            guard let url = URL(string: urlString) else { continue }
            do {
                let (data, response) = try await URLSession.shared.data(from: url)
                guard (response as? HTTPURLResponse)?.statusCode == 200 else {
                    errors.append("\(urlString) -> bad status")
                    continue
                }
                if let dict = try JSONSerialization.jsonObject(with: data) as? [String: String],
                   !dict.isEmpty {
                    return dict
                }
                errors.append("\(urlString) -> unexpected JSON")
            } catch {
                errors.append("\(urlString) -> \(error.localizedDescription)")
            }
        }
        throw AnisetteError.noUsableData(errors.joined(separator: "; "))
    }

    enum AnisetteError: LocalizedError {
        case noUsableData(String)
        public var errorDescription: String? {
            switch self {
            case .noUsableData(let details): return "Anisette server gave no usable data (\(details))"
            }
        }
    }
}
