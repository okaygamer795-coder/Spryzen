import Foundation

/// App tracked in the on-device library (mirrors SideLoadr's SideloadedApp).
struct SideloadedApp: Codable, Identifiable {
    var id: String { bundleID + udid }
    var bundleID: String
    var name: String
    var udid: String
    var installedAt: Date
    var expiresAt: Date

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
