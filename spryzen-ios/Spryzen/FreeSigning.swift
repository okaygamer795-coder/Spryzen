import Foundation

/// On-device half of the free-Apple-ID flow (same system as the SideLoadr PC
/// app): ask the helper which devices it sees, log in, do the portal steps,
/// build a CSR and fetch cert + profile. The helper then only does the
/// mechanical resign + install with these materials.
enum FreeSigning {
    struct Materials {
        var udid: String
        var profileB64: String
        var certPEM: String
        var keyPEM: String
    }

    static func prepare(
        bundleID: String,
        appleID: String,
        password: String,
        anisetteServerURL: String,
        helperURL: String,
        request2FACode: () async throws -> String
    ) async throws -> Materials {
        let udids = try await HelperClient.devices(helperURL: helperURL)
        guard let udid = udids.first, !udid.isEmpty else {
            throw SigningError.noDevice("Helper sees no device — plug the iPhone into the helper PC and trust it.")
        }
        let login = try await AppleAuth.login(
            appleID: appleID, password: password,
            anisetteServerURL: anisetteServerURL, request2FACode: request2FACode)
        let teamID = login.session.teamID
        try await DeveloperPortal.ensureAppID(bundleID: bundleID, teamID: teamID)
        try await DeveloperPortal.ensureDevice(udid: udid, teamID: teamID)
        let (pub, priv) = try CSR.generateKeyPair(tag: "com.spryzen.signing")
        let csr = try CSR.pkcs10(commonName: "iPhone Developer", publicKey: pub, privateKey: priv)
        let cert = try await DeveloperPortal.fetchDevelopmentCert(csrDER: csr, teamID: teamID)
        let profile = try await DeveloperPortal.fetchProfile(bundleID: bundleID, teamID: teamID)
        let keyPEM = try CSR.privateKeyPKCS8PEM(priv)
        return Materials(udid: udid, profileB64: profile.base64EncodedString(), certPEM: cert, keyPEM: keyPEM)
    }

    enum SigningError: LocalizedError {
        case noDevice(String)
        public var errorDescription: String? {
            if case .noDevice(let m) = self { return m }
            return nil
        }
    }
}
