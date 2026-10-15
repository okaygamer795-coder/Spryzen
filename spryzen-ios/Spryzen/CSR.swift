import Foundation
import Security

/// RSA key management + hand-built PKCS#10 (CSR) encoding.
/// iOS has no built-in CSR API, so the DER below is assembled manually.
/// Private keys are software Keychain keys (exportable) because the helper
/// that resigns needs the key material; nothing is Exclave-bound.
enum CSR {
    static func generateKeyPair(tag: String) throws -> (publicKey: SecKey, privateKey: SecKey) {
        deleteKey(tag: tag)
        let privAttrs: [String: Any] = [
            kSecAttrIsPermanent as String: true,
            kSecAttrApplicationTag as String: tag.data(using: .utf8)!,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let attrs: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeRSA,
            kSecAttrKeySizeInBits as String: 2048,
            kSecPrivateKeyAttrs as String: privAttrs,
        ]
        var pub: SecKey?
        var priv: SecKey?
        let status = SecKeyGeneratePair(attrs as CFDictionary, &pub, &priv)
        guard status == errSecSuccess, let pub, let priv else { throw CSRError.keygen(status) }
        return (pub, priv)
    }

    static func deleteKey(tag: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrApplicationTag as String: tag.data(using: .utf8) as Any,
        ]
        SecItemDelete(query as CFDictionary)
    }

    /// PKCS#1 public key DER as produced by the Security framework.
    static func publicKeyDER(_ publicKey: SecKey) throws -> Data {
        var err: Unmanaged<CFError>?
        guard let data = SecKeyCopyExternalRepresentation(publicKey, &err) as Data? else {
            throw CSRError.exportFailed((err?.takeRetainedValue() as Error?)?.localizedDescription ?? "public key")
        }
        return data
    }

    /// Private key wrapped as PKCS#8 PEM (what resign tools expect).
    static func privateKeyPKCS8PEM(_ privateKey: SecKey) throws -> String {
        var err: Unmanaged<CFError>?
        guard let pkcs1 = SecKeyCopyExternalRepresentation(privateKey, &err) as Data? else {
            throw CSRError.exportFailed((err?.takeRetainedValue() as Error?)?.localizedDescription ?? "private key")
        }
        let version = integer(0)
        let algId = seq([oid([1, 2, 840, 113549, 1, 1, 1]), nullBytes()])
        let pkcs8 = seq([version, algId, octetString(pkcs1)])
        return pemWrap(pkcs8, label: "PRIVATE KEY")
    }

    /// Full PKCS#10 CertificationRequest DER for the given keys.
    static func pkcs10(commonName: String, publicKey: SecKey, privateKey: SecKey) throws -> Data {
        let pubDER = try publicKeyDER(publicKey)
        let spki = seq([seq([oid([1, 2, 840, 113549, 1, 1, 1]), nullBytes()]), bitString(pubDER)])
        let cri = seq([integer(0), distinguishedName(commonName: commonName), spki, Data([0xA0, 0x00])])
        var err: Unmanaged<CFError>?
        guard let sig = SecKeyCreateSignature(privateKey, .rsaSignatureMessagePKCS1v15SHA256, cri as CFData, &err) as Data? else {
            throw CSRError.signFailed((err?.takeRetainedValue() as Error?)?.localizedDescription ?? "signing")
        }
        let algId = seq([oid([1, 2, 840, 113549, 1, 1, 11]), nullBytes()])
        return seq([cri, algId, bitString(sig)])
    }

    // MARK: - DER helpers

    static func len(_ n: Int) -> Data {
        if n < 128 { return Data([UInt8(n)]) }
        var bytes: [UInt8] = []
        var v = n
        while v > 0 { bytes.insert(UInt8(v & 0xFF), at: 0); v >>= 8 }
        return Data([UInt8(0x80 | bytes.count)] + bytes)
    }

    static func tlv(_ tag: UInt8, _ content: Data) -> Data {
        var d = Data([tag])
        d.append(len(content.count))
        d.append(content)
        return d
    }

    static func seq(_ parts: [Data]) -> Data {
        tlv(0x30, parts.reduce(Data(), +))
    }

    static func integer(_ v: Int) -> Data {
        if v == 0 { return Data([0x02, 0x01, 0x00]) }
        var bytes: [UInt8] = []
        var x = v
        while x > 0 { bytes.insert(UInt8(x & 0xFF), at: 0); x >>= 8 }
        if bytes[0] & 0x80 != 0 { bytes.insert(0, at: 0) }
        return tlv(0x02, Data(bytes))
    }

    static func oid(_ arcs: [Int]) -> Data {
        var b: [UInt8] = [UInt8(arcs[0] * 40 + arcs[1])]
        for a in arcs.dropFirst(2) {
            var groups: [UInt8] = []
            var x = a
            repeat { groups.insert(UInt8(x & 0x7F), at: 0); x >>= 7 } while x > 0
            for i in 0..<(groups.count - 1) { groups[i] |= 0x80 }
            b.append(contentsOf: groups)
        }
        return tlv(0x06, Data(b))
    }

    static func nullBytes() -> Data { Data([0x05, 0x00]) }

    static func bitString(_ d: Data) -> Data {
        var c = Data([0x00])
        c.append(d)
        return tlv(0x03, c)
    }

    static func octetString(_ d: Data) -> Data {
        tlv(0x04, d)
    }

    static func distinguishedName(commonName: String) -> Data {
        let cn = commonName.data(using: .utf8) ?? Data()
        let atv = seq([oid([2, 5, 4, 3]), tlv(0x0C, cn)])
        return seq([tlv(0x31, atv)])
    }

    static func pemWrap(_ der: Data, label: String) -> String {
        let b64 = der.base64EncodedString(options: [.lineLength64Characters, .endLineWithLineFeed])
        return "-----BEGIN \(label)-----\n\(b64)\n-----END \(label)-----\n"
    }

    enum CSRError: LocalizedError {
        case keygen(OSStatus)
        case exportFailed(String)
        case signFailed(String)
        public var errorDescription: String? {
            switch self {
            case .keygen(let s): return "RSA key generation failed (\(s))."
            case .exportFailed(let w): return "Key export failed (\(w))."
            case .signFailed(let w): return "CSR signing failed (\(w))."
            }
        }
    }
}
