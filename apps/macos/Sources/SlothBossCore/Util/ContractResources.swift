import Foundation

/// Access to the bundled copies of /contracts (migration SQL, prompts, model-output schemas).
public enum ContractResources {
    static let bundleName = "SlothBoss_SlothBossCore.bundle"

    /// The `contracts` folder inside the resource bundle.
    ///
    /// Looks next to the app first (Contents/Resources of SlothBoss.app, where build-app.sh puts the
    /// bundle so code signing stays valid), then falls back to SwiftPM's `Bundle.module`.
    public static let directory: URL? = {
        let fm = FileManager.default
        var candidates: [URL] = []
        if let resources = Bundle.main.resourceURL { candidates.append(resources.appendingPathComponent(bundleName)) }
        candidates.append(Bundle.main.bundleURL.appendingPathComponent(bundleName))
        for candidate in candidates {
            for sub in ["contracts", "Contents/Resources/contracts"] {
                let url = candidate.appendingPathComponent(sub)
                var isDir: ObjCBool = false
                if fm.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue { return url }
            }
        }
        return Bundle.module.url(forResource: "contracts", withExtension: nil)
    }()

    public static func url(_ relativePath: String) throws -> URL {
        guard let dir = directory else { throw SlothBossError("The app's bundled contract resources are missing.") }
        return dir.appendingPathComponent(relativePath)
    }

    public static func read(_ relativePath: String) throws -> String {
        let url = try url(relativePath)
        do {
            let data = try Data(contentsOf: url)
            return String(decoding: data, as: UTF8.self)
        } catch {
            throw SlothBossError("Missing bundled resource '\(relativePath)'.")
        }
    }

    /// Migration scripts as (version, file name) sorted by version: schema/001_initial.sql -> 1.
    public static func migrations() throws -> [(version: Int, name: String)] {
        let dir = try url("schema")
        let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
        return names
            .filter { $0.hasSuffix(".sql") }
            .sorted()
            .compactMap { name in Int(name.prefix(3)).map { (version: $0, name: name) } }
    }
}
