import Foundation

/// App tracked in the on-device library (mirrors SideLoadr's SideloadedApp).
struct SideloadedApp: Codable, Identifiable {
    var id: String { bundleID + udid }
    var bundleID: String
    var name: String
    var udid: String
    var installedAt: Date
    var expiresAt: Date
    var ipaPath: String

    init(bundleID: String, name: String, udid: String, installedAt: Date, expiresAt: Date, ipaPath: String = "") {
        self.bundleID = bundleID
        self.name = name
        self.udid = udid
        self.installedAt = installedAt
        self.expiresAt = expiresAt
        self.ipaPath = ipaPath
    }

    enum CodingKeys: String, CodingKey {
        case bundleID, name, udid, installedAt, expiresAt, ipaPath
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        bundleID = try c.decode(String.self, forKey: .bundleID)
        name = try c.decode(String.self, forKey: .name)
        udid = try c.decode(String.self, forKey: .udid)
        installedAt = try c.decode(Date.self, forKey: .installedAt)
        expiresAt = try c.decode(Date.self, forKey: .expiresAt)
        ipaPath = try c.decodeIfPresent(String.self, forKey: .ipaPath) ?? ""
    }

    var daysLeft: Int {
        max(0, Int(ceil(expiresAt.timeIntervalSinceNow / 86400)))
    }
}

/// Authenticated Apple Developer session (free account).
struct AppleSession: Codable {
    var appleID: String
    var teamID: String
    var teamName: String
}
