import Foundation
import Testing
@testable import JARVIS

/// read_webpage (1.30.89): only the article, all of it, and the rest of a long page with `from`.
@Suite("Pagine web")
struct WebPageTests {
    /// An invented page shaped like a news site: menus and banners around one long <article>.
    static func page(paragraphs: Int) -> String {
        let body = (1...paragraphs).map { "<p>Paragrafo \($0) dell&#8217;articolo &mdash; il robot misura la coppia del giunto e corregge l&rsquo;errore.</p>" }.joined()
        return """
        <html><head><title>Un robot che impara</title><script>var x = "<p>non testo</p>";</script></head><body>
        <header><nav>Home Notizie Abbonati Accedi</nav></header>
        <div class="banner">Iscriviti alla newsletter settimanale</div>
        <article><header><h1>Un robot che impara</h1></header>\(body)<aside>Leggi anche: altro</aside></article>
        <article><p>Scheda di un altro articolo</p></article>
        <footer>Privacy policy</footer></body></html>
        """
    }

    @Test("Solo l'articolo, tutto, con le entità decodificate")
    func article() {
        let text = WebActions.pageText(Self.page(paragraphs: 40))
        #expect(text.hasPrefix("Titolo: Un robot che impara"))
        #expect(text.contains("Paragrafo 1 dell’articolo — il robot"))
        #expect(text.contains("Paragrafo 40 "))
        for junk in ["Abbonati", "newsletter", "Privacy policy", "Leggi anche", "altro articolo", "non testo"] {
            #expect(!text.contains(junk), "\(junk)")
        }
        #expect(!text.contains("[Caratteri"))   // it fits: no note
    }

    @Test("Una pagina lunga si legge a pezzi con from")
    func chunks() throws {
        let html = Self.page(paragraphs: 400)
        let first = WebActions.pageText(html)
        #expect(first.contains("La pagina continua: per il resto richiama read_webpage con from \(WebActions.pageChunk)"))
        let second = WebActions.pageText(html, from: WebActions.pageChunk)
        #expect(second.hasPrefix("[Caratteri \(WebActions.pageChunk)–"))
        #expect(second.contains("Fine della pagina."))
        #expect(second.contains("Paragrafo 400 "))
    }
}
