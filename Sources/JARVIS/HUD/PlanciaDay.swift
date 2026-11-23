import SwiftUI

/// The Plancia's "Giornata" (1.30.81): the old "Attività" summary on top, then the sessions, reports and
/// flashcards of the "Studio" panel (1.30.80), read-only, in the Settings look. It follows the files as they
/// change: a session started, closed or deleted by voice, a report saved, a flashcard graded show up at once.
struct DaySection: View {
    enum List: String, CaseIterable {
        case sessions, reports, cards
        var title: String {
            switch self {
            case .sessions: return "Sessioni"
            case .reports: return "Resoconti"
            case .cards: return "Schede"
            }
        }
    }

    @ObservedObject var activity: ActivityTracker
    @ObservedObject var flashcards: Flashcards
    var compact = false
    @AppStorage("studyPanelSection") private var list = List.sessions.rawValue

    private var current: List { List(rawValue: list) ?? .sessions }
    private static let italian = Locale(identifier: "it_IT")

    var body: some View {
        Form {
            Section { summary }
            Section {
                Picker("", selection: $list) {
                    ForEach(List.allCases, id: \.rawValue) { item in
                        Text("\(item.title) \(count(item))").tag(item.rawValue)
                    }
                }
                .pickerStyle(.segmented).labelsHidden()
                switch current {
                case .sessions: sessionsList
                case .reports: reportsList
                case .cards: cardsList
                }
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
    }

    // MARK: Summary

    private var summary: some View {
        TimelineView(.periodic(from: .now, by: 1)) { _ in
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .center, spacing: 14) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(activity.current.map { $0.isPaused ? "In pausa" : "In corso" } ?? "Nessuna sessione in corso")
                            .font(.caption).foregroundStyle(.secondary)
                        if let session = activity.current {
                            HStack(spacing: 6) {
                                Circle().fill(session.isPaused ? HUD.amber : Color.green.opacity(0.85)).frame(width: 7, height: 7)
                                Text("\(session.name) · \(Self.kind(session.category))").font(.system(size: 14, weight: .semibold))
                            }
                        } else {
                            Text("Di' «inizio a studiare…» per avviarne una.").font(.callout)
                        }
                    }
                    Spacer()
                    if let session = activity.current {
                        Text(ActivityTracker.clock(session.duration))
                            .font(HUD.font(compact ? 18 : 22, .bold)).monospacedDigit().foregroundStyle(HUD.ice)
                    }
                }
                HStack(alignment: .top) {
                    total("Oggi", Self.short(activity.todayTotal))
                    Spacer()
                    total("Settimana", Self.short(activity.weekTotal))
                    Spacer()
                    total("Da ripassare", "\(dueCards) \(dueCards == 1 ? "scheda" : "schede")")
                }
            }
        }
    }

    private var dueCards: Int { flashcards.cards.filter { $0.due <= Date() }.count }

    private func total(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(value).font(HUD.font(14, .bold)).foregroundStyle(HUD.ice)
        }
    }

    private func count(_ item: List) -> Int {
        switch item {
        case .sessions: return activity.sessions.count
        case .reports: return activity.reports.count
        case .cards: return flashcards.cards.count
        }
    }

    // MARK: Sessions

    /// By day, newest first; the open one ticks every second.
    @ViewBuilder private var sessionsList: some View {
        if activity.sessions.isEmpty {
            empty("Nessuna sessione. Di' «inizio a studiare…» per avviarne una.")
        }
        let days = Dictionary(grouping: activity.sessions) { Calendar.current.startOfDay(for: $0.start) }
            .sorted { $0.key > $1.key }
        ForEach(days, id: \.key) { day, list in
            dayHeader(day, total: list.reduce(0) { $0 + $1.duration })
            ForEach(Array(list.sorted { $0.start > $1.start }.enumerated()), id: \.offset) { _, session in
                sessionRow(session)
            }
        }
    }

    private func dayHeader(_ day: Date, total: TimeInterval) -> some View {
        HStack {
            Text(Self.dayName(day).capitalizedFirst).font(.caption.weight(.semibold)).foregroundStyle(HUD.cyan.opacity(0.8))
            Spacer()
            Text(Self.short(total)).font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private func sessionRow(_ session: ActivityTracker.Session) -> some View {
        if session.end == nil {
            TimelineView(.periodic(from: .now, by: 1)) { _ in sessionLine(session) }
        } else {
            sessionLine(session)
        }
    }

    private func sessionLine(_ session: ActivityTracker.Session) -> some View {
        let open = session.end == nil
        let span = Self.time(session.start) + "–" + (session.end.map(Self.time) ?? "ora")
        var detail = "\(Self.kind(session.category).capitalizedFirst) · \(span) · "
            + (open ? ActivityTracker.clock(session.duration) : Self.short(session.duration))
        let pauses = session.pauses?.count ?? 0
        if pauses > 0 { detail += " · \(pauses) \(pauses == 1 ? "pausa" : "pause") (\(Self.short(session.pausedTime)))" }
        return VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                Text(session.name).font(.system(size: 13, weight: .medium))
                if open {
                    Text(session.isPaused ? "in pausa" : "in corso").font(.caption2.weight(.semibold))
                        .padding(.horizontal, 6).padding(.vertical, 1)
                        .foregroundStyle(HUD.background).background(session.isPaused ? HUD.amber : HUD.cyan, in: Capsule())
                }
            }
            Text(detail).font(.caption).foregroundStyle(.secondary)
        }
        .textSelection(.enabled)
    }

    // MARK: Reports

    @ViewBuilder private var reportsList: some View {
        let list = activity.reportList
        if list.isEmpty {
            empty("Nessun resoconto. Si scrive quando chiudi una sessione di almeno 2 minuti.")
        }
        ForEach(Array(list.enumerated()), id: \.offset) { _, item in
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline) {
                    Text(item.name).font(.system(size: 13, weight: .medium))
                    Spacer()
                    Text(Self.dayName(item.report.date).capitalizedFirst + ", " + Self.time(item.report.date))
                        .font(.caption).foregroundStyle(.secondary)
                }
                Text(item.report.text).font(.callout).foregroundStyle(HUD.ice.opacity(0.9))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .textSelection(.enabled)
        }
    }

    // MARK: Flashcards

    /// By subject; in each, the ones due first.
    @ViewBuilder private var cardsList: some View {
        if flashcards.cards.isEmpty {
            empty("Nessuna scheda. Chiedi «fammi le schede di…»: si fanno dai tuoi appunti.")
        }
        let now = Date()
        let topics = Dictionary(grouping: flashcards.cards, by: \.topic).sorted { $0.key.localizedCompare($1.key) == .orderedAscending }
        ForEach(topics, id: \.key) { topic, list in
            let due = list.filter { $0.due <= now }.count
            HStack {
                Text(topic.capitalizedFirst).font(.caption.weight(.semibold)).foregroundStyle(HUD.cyan.opacity(0.8))
                Spacer()
                Text("\(list.count) · da rivedere \(due)").font(.caption).foregroundStyle(due > 0 ? HUD.amber : .secondary)
            }
            ForEach(list.sorted { ($0.due, $0.id) < ($1.due, $1.id) }, id: \.id) { card in
                cardRow(card, now: now)
            }
        }
    }

    private func cardRow(_ card: Flashcards.Card, now: Date) -> some View {
        let due = card.due <= now
        let when = due ? "da rivedere oggi" : "prossima " + card.due.formatted(.dateTime.day().month(.abbreviated).locale(Self.italian))
        return VStack(alignment: .leading, spacing: 3) {
            Text(card.question).font(.system(size: 13, weight: .medium))
            Text(card.answer).font(.callout).foregroundStyle(HUD.ice.opacity(0.9))
            Text("#\(card.id) · \(card.source) · livello \(card.box + 1)/6 · \(when)")
                .font(.caption).foregroundStyle(due ? HUD.amber.opacity(0.9) : .secondary)
        }
        .fixedSize(horizontal: false, vertical: true)
        .textSelection(.enabled)
    }

    // MARK: Text

    private func empty(_ text: String) -> some View {
        Text(text).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }

    private static func kind(_ category: ActivityTracker.Category) -> String {
        switch category {
        case .study: return "studio"
        case .project: return "progetto"
        case .other: return "attività"
        }
    }

    private static func time(_ date: Date) -> String { date.formatted(.dateTime.hour().minute().locale(italian)) }

    private static func dayName(_ date: Date) -> String {
        if Calendar.current.isDateInToday(date) { return "oggi" }
        if Calendar.current.isDateInYesterday(date) { return "ieri" }
        return date.formatted(.dateTime.weekday(.wide).day().month(.wide).locale(italian))
    }

    /// "1 h 05 min", "12 min".
    private static func short(_ interval: TimeInterval) -> String {
        let minutes = Int(interval / 60)
        return minutes < 60 ? "\(minutes) min" : "\(minutes / 60) h \(String(format: "%02d", minutes % 60)) min"
    }
}

private extension String {
    var capitalizedFirst: String { prefix(1).uppercased() + dropFirst() }
}
