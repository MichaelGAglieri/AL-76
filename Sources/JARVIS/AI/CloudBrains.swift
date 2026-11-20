import Foundation

/// A "big brain" reached through the CLI of a subscription the user already has, with no paid API:
/// Claude (Claude Code), ChatGPT (OpenAI Codex CLI) or Gemini (Google Antigravity CLI). All three get
/// AL-76's tools from the same local MCP server and the same instructions (v1.30.68).
protocol CloudBridge: AnyObject {
    /// The name said and shown: "Claude", "ChatGPT", "Gemini".
    var name: String { get }
    var isAvailable: Bool { get }
    /// True if the next question opens a new session (none yet, or a pause of more than 30 minutes).
    var startsNewSession: Bool { get }
    func resetSession()
    func ask(_ prompt: String, systemPrompt: String, mcp: MCPServer) -> AsyncThrowingStream<ClaudeEvent, Error>
    /// "COLLEGATO", "LOGIN NECESSARIO" or "NON INSTALLATO", for the HUD.
    func status() async -> String
}

enum CloudError: LocalizedError {
    case notInstalled(String, hint: String)
    case notLoggedIn(String, hint: String)
    case failed(String, String)

    var errorDescription: String? {
        switch self {
        case .notInstalled(let name, let hint): return "\(name): CLI non installata (\(hint))"
        case .notLoggedIn(let name, let hint): return "\(name): accesso non fatto (\(hint))"
        case .failed(let name, let message): return "\(name): \(message)"
        }
    }

    /// A missing or expired login, from any of the three brains.
    static func isLogin(_ error: Error) -> Bool {
        if case ClaudeError.notLoggedIn = error { return true }
        if case CloudError.notLoggedIn = error { return true }
        return false
    }

    /// Words the CLIs use when the account is not signed in or the sign-in expired.
    static func soundsLikeLogin(_ text: String) -> Bool {
        let t = text.lowercased()
        return ["not logged in", "log in", "login", "sign in", "unauthorized", "401", "auth method", "authenticat", "credentials", "authentication required"]
            .contains { t.contains($0) }
    }
}

extension ClaudeBridge: CloudBridge {
    var name: String { "Claude" }

    func ask(_ prompt: String, systemPrompt: String, mcp: MCPServer) -> AsyncThrowingStream<ClaudeEvent, Error> {
        ask(prompt, systemPrompt: systemPrompt, mcpConfig: mcp.claudeConfig)
    }

    func status() async -> String {
        guard let binary = Self.findBinary() else { return "NON INSTALLATO" }
        let result = await Shell.run(binary, ["auth", "status"], timeout: 10)
        return result.out.contains("\"loggedIn\": true") ? "COLLEGATO" : "LOGIN NECESSARIO"
    }
}

/// Runs a CLI that prints one JSON object per line and hands every line over.
/// `finish` is called once, when the process has closed and its last output has arrived,
/// with what it wrote on stderr (where the CLIs explain a missing login).
enum JSONLines {
    static func run(_ binary: String, _ arguments: [String], directory: URL, environment extra: [String: String],
                    continuation: AsyncThrowingStream<ClaudeEvent, Error>.Continuation,
                    line: @escaping ([String: Any]) -> Void, finish: @escaping (String) -> Void) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: binary)
        process.arguments = arguments
        process.currentDirectoryURL = directory
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        for (key, value) in extra { env[key] = value }
        process.environment = env
        let stdout = Pipe()
        let stderr = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr
        // Without an explicit stdin the CLIs wait for input that will never come.
        process.standardInput = FileHandle.nullDevice

        let lock = NSLock()
        var buffer = Data()
        var errors = Data()
        stdout.fileHandleForReading.readabilityHandler = { handle in
            let chunk = handle.availableData
            guard !chunk.isEmpty else { return }
            lock.lock()
            buffer.append(chunk)
            var lines: [Data] = []
            while let newline = buffer.firstIndex(of: 0x0A) {
                lines.append(Data(buffer[..<newline]))
                buffer.removeSubrange(...newline)
            }
            lock.unlock()
            for data in lines {
                if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { line(json) }
            }
        }
        stderr.fileHandleForReading.readabilityHandler = { handle in
            let chunk = handle.availableData
            guard !chunk.isEmpty else { return }
            lock.lock()
            if errors.count < 20_000 { errors.append(chunk) }
            lock.unlock()
        }
        process.terminationHandler = { _ in
            // The last line of output may arrive shortly after the process closes.
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.5) {
                stdout.fileHandleForReading.readabilityHandler = nil
                stderr.fileHandleForReading.readabilityHandler = nil
                lock.lock()
                let text = String(decoding: errors, as: UTF8.self)
                lock.unlock()
                finish(text)
            }
        }
        continuation.onTermination = { _ in
            if process.isRunning { process.terminate() }
        }
        do {
            try process.run()
        } catch {
            continuation.finish(throwing: error)
        }
    }

    static func findBinary(_ name: String, extra: [String] = []) -> String? {
        let home = NSHomeDirectory()
        return (["/opt/homebrew/bin/\(name)", "/usr/local/bin/\(name)", "\(home)/.local/bin/\(name)", "\(home)/.npm-global/bin/\(name)"] + extra)
            .first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// The last line of an error message, without ANSI colours: what the user can read.
    static func lastLine(_ text: String) -> String {
        let clean = text.replacingOccurrences(of: #"\u001B\[[0-9;]*m"#, with: "", options: .regularExpression)
        return clean.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }
            .last { !$0.isEmpty }.map { String($0.prefix(200)) } ?? "nessuna risposta"
    }
}

/// Mutable state shared by the closures of one run.
private final class RunState {
    var finished = false
    var lastError: String?
    var tools: [String: String] = [:]
    let start = Date()
}

// MARK: - ChatGPT

/// ChatGPT through the OpenAI Codex CLI (`codex exec`), signed in with the ChatGPT subscription
/// (`codex login`). Tested 16 November with codex-cli 0.169.0: AL-76's MCP tools are approved one by
/// one only for its own server; the terminal is off (shell_tool, unified_exec), the sandbox is
/// read-only, web search is live. The user's ~/.codex/config.toml is ignored, as Claude's settings are.
final class CodexBridge: CloudBridge {
    /// Impostazioni › Permessi: Codex's own web search follows the "web" switch.
    var allowWeb = true
    let name = "ChatGPT"
    private(set) var sessionID: String?
    private var lastUse = Date.distantPast
    /// Empty: the CLI's default model for the subscription.
    var model = ""

    static func findBinary() -> String? {
        JSONLines.findBinary("codex", extra: ["/Applications/Codex.app/Contents/Resources/codex"])
    }

    var isAvailable: Bool { Self.findBinary() != nil }
    var startsNewSession: Bool { sessionID == nil || Date().timeIntervalSince(lastUse) > 1800 }
    func resetSession() { sessionID = nil }

    func status() async -> String {
        guard let binary = Self.findBinary() else { return "NON INSTALLATO" }
        let result = await Shell.run(binary, ["login", "status"], timeout: 10)
        return (result.out + result.err).contains("Logged in") ? "COLLEGATO" : "LOGIN NECESSARIO"
    }

    /// A TOML basic string: JSON escapes are valid TOML, as long as "/" is not escaped.
    static func toml(_ text: String) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: [text], options: [.withoutEscapingSlashes]) else { return "\"\"" }
        let array = String(decoding: data, as: UTF8.self)
        return String(array.dropFirst().dropLast())
    }

    func ask(_ prompt: String, systemPrompt: String, mcp: MCPServer) -> AsyncThrowingStream<ClaudeEvent, Error> {
        AsyncThrowingStream { continuation in
            guard let binary = Self.findBinary() else {
                continuation.finish(throwing: CloudError.notInstalled(name, hint: "brew install --cask codex"))
                return
            }
            if startsNewSession { sessionID = nil }
            var options = [
                "--json", "--skip-git-repo-check", "--ignore-user-config",
                // No terminal: AL-76's tools only, plus web search and reading.
                "--disable", "shell_tool", "--disable", "unified_exec",
                "-c", "approval_policy=\"never\"", "-c", "sandbox_mode=\"read-only\"", "-c", allowWeb ? "web_search=\"live\"" : "web_search=\"disabled\"",
                "-c", "mcp_servers.jarvis.url=" + Self.toml(mcp.url),
                // The token stays out of the command line: Codex reads it from this variable.
                "-c", "mcp_servers.jarvis.bearer_token_env_var=\"JARVIS_MCP_TOKEN\"",
                "-c", "mcp_servers.jarvis.default_tools_approval_mode=\"approve\"",
                "-c", "developer_instructions=" + Self.toml(systemPrompt),
            ]
            if !model.isEmpty { options += ["-c", "model=" + Self.toml(model)] }
            let arguments = sessionID.map { ["exec", "resume"] + options + ["--", $0, prompt] } ?? ["exec"] + options + ["--", prompt]
            let run = RunState()
            let name = self.name
            JSONLines.run(binary, arguments, directory: URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Projects"),
                          environment: ["JARVIS_MCP_TOKEN": mcp.token], continuation: continuation, line: { [weak self] json in
                let item = json["item"] as? [String: Any] ?? [:]
                let kind = item["type"] as? String ?? ""
                switch json["type"] as? String {
                case "thread.started":
                    self?.sessionID = json["thread_id"] as? String
                case "item.started" where kind == "mcp_tool_call":
                    let tool = item["tool"] as? String ?? "?"
                    continuation.yield(.tool("mcp__jarvis__" + tool))
                    continuation.yield(.trace(ClaudeBridge.describeToolUse(tool, item["arguments"] as? [String: Any] ?? [:])))
                case "item.completed":
                    switch kind {
                    case "agent_message":
                        if let text = item["text"] as? String, !text.isEmpty { continuation.yield(.text(text + "\n")) }
                    case "mcp_tool_call":
                        let error = (item["error"] as? [String: Any])?["message"] as? String
                        let content = (item["result"] as? [String: Any])?["content"] ?? error
                        continuation.yield(.trace(ClaudeBridge.describeToolResult(content, tool: item["tool"] as? String, isError: error != nil)))
                    case let web where web.contains("web_search"):
                        let query = item["query"] as? String ?? ((item["action"] as? [String: Any])?["query"] as? String) ?? ""
                        continuation.yield(.tool("web_search"))
                        continuation.yield(.trace("⌕ web: \"\(query)\""))
                    default:
                        break
                    }
                case "turn.completed":
                    run.finished = true
                    self?.lastUse = Date()
                    continuation.yield(.trace(String(format: "✓ ChatGPT · %.1f s", Date().timeIntervalSince(run.start))))
                    continuation.finish()
                case "turn.failed":
                    run.finished = true
                    let message = ((json["error"] as? [String: Any])?["message"] as? String) ?? run.lastError ?? "errore sconosciuto"
                    continuation.finish(throwing: CloudError.soundsLikeLogin(message)
                                        ? CloudError.notLoggedIn(name, hint: "codex login") : CloudError.failed(name, message))
                case "error":
                    // Also temporary notices ("Reconnecting…"): fatal only if the turn fails.
                    run.lastError = json["message"] as? String
                default:
                    break
                }
            }, finish: { stderr in
                guard !run.finished else { return }
                let message = run.lastError ?? JSONLines.lastLine(stderr)
                continuation.finish(throwing: CloudError.soundsLikeLogin(message + stderr)
                                    ? CloudError.notLoggedIn(name, hint: "codex login") : CloudError.failed(name, message))
            })
        }
    }
}

// MARK: - Gemini

/// Gemini through Google's Antigravity CLI (`agy -p`), signed in with the Google account. The Gemini CLI
/// stopped serving personal accounts (free and AI Pro) on 18 June 2026: tried on 16 November, the sign-in
/// worked and then every request failed with "This client is no longer supported…, migrate to Antigravity".
/// It runs in its own folder, Application Support/JARVIS/antigravity, whose .agents/mcp_config.json gives it
/// AL-76's MCP server and whose AGENTS.md holds the instructions. In headless mode everything that would ask
/// for approval is refused by itself, but "safe" commands and edits inside the folder run without asking
/// (tested: it ran `ls ~` and wrote a file). So AL-76 uses it only when agy's own settings
/// (~/.gemini/antigravity-cli/settings.json, the only place for rules) deny command, write_file, read_url and
/// execute_url and allow mcp(jarvis/*): the user chose these global rules on 16 November.
final class GeminiBridge: CloudBridge {
    let name = "Gemini"
    private(set) var sessionID: String?
    private var lastUse = Date.distantPast
    /// Empty: the model chosen in agy (/model).
    var model = ""

    static let folder = JarvisConfig.directory.appendingPathComponent("antigravity", isDirectory: true)
    static let settingsURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent(".gemini/antigravity-cli/settings.json")
    static let toolsRule = "mcp(jarvis/*)"
    /// No terminal, no edits, no web pages or browser: the same limits as Claude and ChatGPT.
    static let denyRules = ["command(*)", "write_file(*)", "read_url(*)", "execute_url(*)"]

    static func findBinary() -> String? { JSONLines.findBinary("agy") }

    var isAvailable: Bool { Self.findBinary() != nil }
    var startsNewSession: Bool { sessionID == nil || Date().timeIntervalSince(lastUse) > 1800 }
    func resetSession() { sessionID = nil }

    /// Signed in: `agy models` lists the models only with a valid account.
    func status() async -> String {
        guard let binary = Self.findBinary() else { return "NON INSTALLATO" }
        let result = await Shell.run(binary, ["models"], timeout: 20)
        return result.out.lowercased().contains("gemini") ? "COLLEGATO" : "LOGIN NECESSARIO"
    }

    /// True when agy's settings allow AL-76's tools and deny terminal, edits and web pages.
    static var toolsAllowed: Bool {
        guard let data = try? Data(contentsOf: settingsURL),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let permissions = json["permissions"] as? [String: Any],
              let allow = permissions["allow"] as? [String] else { return false }
        let deny = permissions["deny"] as? [String] ?? []
        return allow.contains(toolsRule) && denyRules.allSatisfy(deny.contains)
    }

    /// Adds mcp(jarvis/*) to agy's allow list and the four deny rules, keeping everything else in the file.
    /// Called from Settings, on the user's choice: it is the user's own configuration.
    static func allowTools() throws {
        var json: [String: Any] = [:]
        if let data = try? Data(contentsOf: settingsURL) {
            guard let existing = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
            json = existing
        }
        var permissions = json["permissions"] as? [String: Any] ?? [:]
        var allow = permissions["allow"] as? [String] ?? []
        var deny = permissions["deny"] as? [String] ?? []
        if !allow.contains(toolsRule) { allow.append(toolsRule) }
        for rule in denyRules where !deny.contains(rule) { deny.append(rule) }
        permissions["allow"] = allow
        permissions["deny"] = deny
        json["permissions"] = permissions
        try FileManager.default.createDirectory(at: settingsURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .withoutEscapingSlashes, .sortedKeys])
        try data.write(to: settingsURL, options: .atomic)
    }

    /// MCP server and instructions, rewritten before every question: port and token change at every launch.
    /// The file with the token is readable only by the user (the JARVIS folder is closed to others too).
    private func prepareFolder(mcp: MCPServer, systemPrompt: String) throws {
        let agents = Self.folder.appendingPathComponent(".agents", isDirectory: true)
        try FileManager.default.createDirectory(at: agents, withIntermediateDirectories: true)
        let config: [String: Any] = ["mcpServers": ["jarvis": [
            "serverUrl": mcp.url,
            "headers": ["Authorization": "Bearer \(mcp.token)"],
        ]]]
        let file = agents.appendingPathComponent("mcp_config.json")
        try JSONSerialization.data(withJSONObject: config, options: [.prettyPrinted, .withoutEscapingSlashes]).write(to: file, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        try Data(systemPrompt.utf8).write(to: Self.folder.appendingPathComponent("AGENTS.md"), options: .atomic)
    }

    func ask(_ prompt: String, systemPrompt: String, mcp: MCPServer) -> AsyncThrowingStream<ClaudeEvent, Error> {
        AsyncThrowingStream { continuation in
            let name = self.name
            let hint = "curl -fsSL https://antigravity.google/cli/install.sh | bash, poi agy"
            guard let binary = Self.findBinary() else {
                continuation.finish(throwing: CloudError.notInstalled(name, hint: hint))
                return
            }
            // Never without the deny rules: agy would run commands and write files by itself.
            guard Self.toolsAllowed else {
                continuation.finish(throwing: CloudError.failed(name, "Antigravity non è ancora preparato per AL-76 (Impostazioni › Cervello)"))
                return
            }
            do { try prepareFolder(mcp: mcp, systemPrompt: systemPrompt) } catch {
                continuation.finish(throwing: error)
                return
            }
            if startsNewSession { sessionID = nil }
            var arguments = ["-p", prompt, "--output-format", "stream-json", "--disable-slash-commands", "--print-timeout", "5m"]
            if !model.isEmpty { arguments += ["--model", model] }
            if let sessionID { arguments += ["--conversation", sessionID] }
            let run = RunState()
            var started: Set<Int> = []
            JSONLines.run(binary, arguments, directory: Self.folder, environment: [:], continuation: continuation, line: { [weak self] json in
                switch json["event"] as? String {
                case "init":
                    if let id = json["conversation_id"] as? String, !id.isEmpty { self?.sessionID = id }
                case "step_update":
                    let step = json["step_update"] as? [String: Any] ?? [:]
                    let index = step["step_index"] as? Int ?? -1
                    switch step["step_type"] as? String {
                    case "agent_response":
                        if let text = step["text_delta"] as? String, !text.isEmpty { continuation.yield(.text(text)) }
                    case "tool":
                        let info = step["tool_info"] as? [String: Any] ?? [:]
                        var tool = Self.toolName(step["tool_name"] as? String ?? info["name"] as? String ?? "?")
                        var parameters = info["parameters"] as? [String: Any] ?? [:]
                        // MCP tools all go through one generic tool: the real name is inside.
                        if tool == "call_mcp_tool", let real = parameters["ToolName"] as? String {
                            tool = real
                            parameters = parameters["Arguments"] as? [String: Any] ?? [:]
                        }
                        if started.insert(index).inserted {
                            continuation.yield(.tool(tool))
                            continuation.yield(.trace(ClaudeBridge.describeToolUse(tool, parameters)))
                        }
                        if ["DONE", "ERROR"].contains(step["state"] as? String) {
                            let error = (info["error"] as? [String: Any])?["message"] as? String
                            continuation.yield(.trace(ClaudeBridge.describeToolResult(error ?? info["output"], tool: tool, isError: error != nil)))
                        }
                    default:
                        break
                    }
                case "result":
                    run.finished = true
                    let result = json["result"] as? [String: Any] ?? [:]
                    if let id = result["conversation_id"] as? String, !id.isEmpty { self?.sessionID = id }
                    if result["status"] as? String == "SUCCESS" {
                        self?.lastUse = Date()
                        continuation.yield(.text("\n"))
                        continuation.yield(.trace(String(format: "✓ Gemini · %.1f s", Date().timeIntervalSince(run.start))))
                        continuation.finish()
                    } else {
                        let message = result["error"] as? String ?? "esito \(result["status"] as? String ?? "sconosciuto")"
                        continuation.finish(throwing: CloudError.soundsLikeLogin(message)
                                            ? CloudError.notLoggedIn(name, hint: "apri «agy» nel Terminale e accedi") : CloudError.failed(name, message))
                    }
                default:
                    break
                }
            }, finish: { stderr in
                guard !run.finished else { return }
                let message = JSONLines.lastLine(stderr)
                continuation.finish(throwing: CloudError.soundsLikeLogin(stderr)
                                    ? CloudError.notLoggedIn(name, hint: "apri «agy» nel Terminale e accedi") : CloudError.failed(name, message))
            })
        }
    }

    /// MCP tools may come with the server's name in front: the rest of AL-76 knows them without it.
    private static func toolName(_ name: String) -> String {
        for prefix in ["mcp_jarvis_", "jarvis__", "mcp__jarvis__", "jarvis/", "jarvis_"] where name.hasPrefix(prefix) {
            return String(name.dropFirst(prefix.count))
        }
        return name
    }
}
