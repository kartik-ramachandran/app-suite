namespace KitchenManager.Api.Services;

/// <summary>
/// Crisis helplines by country.
///
/// These numbers are shown to people at the worst moment they will ever use
/// this product, so three rules apply:
///
///  1. <b>Never AI-generated.</b> A hallucinated helpline number is arguably the
///     single most harmful thing this codebase could emit. Everything here is
///     static data.
///  2. <b>Always degrade to something usable.</b> An unknown country resolves to
///     <see cref="International"/> (findahelpline.com + local emergency
///     services), never to an empty message.
///  3. <b>Verify before launch in a new market.</b> Numbers change. There is a
///     review date below; treat it as a maintenance obligation, not decoration.
///
/// Last verified: 2026-08. Sources: national health services and
/// findahelpline.com. Re-check annually.
/// </summary>
public static class HelplineDirectory
{
    public record Line(string Name, string Contact);

    public record Region(
        string CountryCode,
        string CountryName,
        string Emergency,
        Line[] Crisis,
        Line[] DomesticViolence,
        Line[] Health);

    /// <summary>Shown when we cannot tell where someone is.</summary>
    public static readonly Region International = new(
        "", "International", "your local emergency number",
        Crisis: [new Line("Find a helpline in your country", "findahelpline.com")],
        DomesticViolence: [new Line("Find a helpline in your country", "findahelpline.com")],
        Health: [new Line("Find a helpline in your country", "findahelpline.com")]);

    // 116 123 is a harmonised emotional-support number across much of the EU,
    // and 112 is the EU-wide emergency number.
    private static readonly Line Eu116 = new("Emotional support", "116 123");

    private static readonly Dictionary<string, Region> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NZ"] = new("NZ", "New Zealand", "111",
            [new Line("Need to talk?", "call or text 1737 (free, 24/7)"),
             new Line("Lifeline Aotearoa", "0800 543 354"),
             new Line("Suicide Crisis Helpline", "0508 828 865")],
            [new Line("Women's Refuge", "0800 733 843"),
             new Line("Shine", "0508 744 633"),
             new Line("Are You OK", "0800 456 450")],
            [new Line("Healthline", "0800 611 116")]),

        ["AU"] = new("AU", "Australia", "000",
            [new Line("Lifeline", "13 11 14"),
             new Line("Suicide Call Back Service", "1300 659 467"),
             new Line("Beyond Blue", "1300 22 4636")],
            [new Line("1800RESPECT", "1800 737 732")],
            [new Line("healthdirect", "1800 022 222")]),

        ["US"] = new("US", "United States", "911",
            [new Line("Suicide & Crisis Lifeline", "call or text 988"),
             new Line("Crisis Text Line", "text HOME to 741741")],
            [new Line("National Domestic Violence Hotline", "1-800-799-7233")],
            [new Line("SAMHSA National Helpline", "1-800-662-4357")]),

        ["CA"] = new("CA", "Canada", "911",
            [new Line("Suicide Crisis Helpline", "call or text 988"),
             new Line("Crisis Text Line", "text CONNECT to 686868")],
            [new Line("Assaulted Women's Helpline", "1-866-863-0511")],
            [new Line("Wellness Together Canada", "1-866-585-0445")]),

        ["GB"] = new("GB", "United Kingdom", "999",
            [new Line("Samaritans", "116 123 (free, 24/7)"),
             new Line("Shout", "text SHOUT to 85258")],
            [new Line("National Domestic Abuse Helpline", "0808 2000 247")],
            [new Line("NHS", "111")]),

        ["IE"] = new("IE", "Ireland", "112 or 999",
            [new Line("Samaritans", "116 123"),
             new Line("Text About It", "text HELLO to 50808")],
            [new Line("Women's Aid", "1800 341 900")],
            [new Line("HSE", "1800 700 700")]),

        ["IN"] = new("IN", "India", "112",
            [new Line("Tele-MANAS", "14416 (24/7)"),
             new Line("AASRA", "+91 98204 66726"),
             new Line("iCall", "+91 91529 87821")],
            [new Line("Women Helpline", "181"),
             new Line("National Commission for Women", "7827 170 170")],
            [new Line("National Health Helpline", "1800 180 1104")]),

        ["ZA"] = new("ZA", "South Africa", "10111",
            [new Line("SADAG Suicide Crisis Line", "0800 567 567"),
             new Line("Lifeline South Africa", "0861 322 322")],
            [new Line("GBV Command Centre", "0800 428 428")],
            [new Line("Emergency ambulance", "10177")]),

        ["SG"] = new("SG", "Singapore", "995",
            [new Line("Samaritans of Singapore", "1767"),
             new Line("SOS Care Text", "go.gov.sg/care-text")],
            [new Line("National Anti-Violence Helpline", "1800 777 0000")],
            [new Line("HealthLine", "1800 333 9999")]),

        ["MY"] = new("MY", "Malaysia", "999",
            [new Line("Befrienders KL", "03-7627 2929"),
             new Line("Talian HEAL", "15555")],
            [new Line("Talian Kasih", "15999")],
            [new Line("Ministry of Health", "03-8881 0200")]),

        ["PH"] = new("PH", "Philippines", "911",
            [new Line("NCMH Crisis Hotline", "1553"),
             new Line("Hopeline", "0917 558 4673")],
            [new Line("PNP Women and Children Protection", "177")],
            [new Line("DOH", "1555")]),

        ["JP"] = new("JP", "Japan", "119",
            [new Line("Yorisoi Hotline", "0120-279-338"),
             new Line("TELL Lifeline (English)", "03-5774-0992")],
            [new Line("DV Consultation Navi", "#8008")],
            [new Line("Medical consultation", "#7119")]),

        ["HK"] = new("HK", "Hong Kong", "999",
            [new Line("The Samaritans (multilingual)", "2896 0000"),
             new Line("Suicide Prevention Services", "2382 0000")],
            [new Line("Harmony House", "2522 0434")],
            [new Line("Hospital Authority", "2300 6555")]),

        ["DE"] = new("DE", "Germany", "112",
            [new Line("Telefonseelsorge", "0800 111 0 111 / 0800 111 0 222"),
             Eu116],
            [new Line("Hilfetelefon Gewalt gegen Frauen", "116 016")],
            [new Line("Ärztlicher Bereitschaftsdienst", "116 117")]),

        ["FR"] = new("FR", "France", "112 or 15",
            [new Line("Numéro national de prévention du suicide", "3114"),
             new Line("SOS Amitié", "09 72 39 40 50")],
            [new Line("Violences Femmes Info", "3919")],
            [new Line("SAMU", "15")]),

        ["NL"] = new("NL", "Netherlands", "112",
            [new Line("113 Zelfmoordpreventie", "113 or 0800 0113")],
            [new Line("Veilig Thuis", "0800 2000")],
            [new Line("Huisartsenpost", "0800 1821")]),

        ["BE"] = new("BE", "Belgium", "112",
            [new Line("Zelfmoordlijn", "1813"),
             new Line("Centre de Prévention du Suicide", "0800 32 123")],
            [new Line("Écoute Violences Conjugales", "0800 30 030")],
            [new Line("Medical on-call", "1733")]),

        ["ES"] = new("ES", "Spain", "112",
            [new Line("Línea de atención a la conducta suicida", "024"),
             new Line("Teléfono de la Esperanza", "717 003 717")],
            [new Line("Violencia de género", "016")],
            [new Line("Emergencias sanitarias", "061")]),

        ["IT"] = new("IT", "Italy", "112",
            [new Line("Telefono Amico", "02 2327 2327"),
             new Line("Samaritans Onlus", "06 77208977")],
            [new Line("Antiviolenza Donna", "1522")],
            [new Line("Emergenza sanitaria", "118")]),

        ["PT"] = new("PT", "Portugal", "112",
            [new Line("SOS Voz Amiga", "213 544 545"),
             new Line("Linha SNS 24", "808 24 24 24")],
            [new Line("Violência Doméstica", "800 202 148")],
            [new Line("SNS 24", "808 24 24 24")]),

        ["SE"] = new("SE", "Sweden", "112",
            [new Line("Mind Självmordslinjen", "90101"),
             new Line("Jourhavande medmänniska", "08-702 16 80")],
            [new Line("Kvinnofridslinjen", "020 50 50 50")],
            [new Line("Vårdguiden", "1177")]),

        ["NO"] = new("NO", "Norway", "113",
            [new Line("Mental Helse Hjelpetelefonen", "116 123"),
             new Line("Kirkens SOS", "22 40 00 40")],
            [new Line("Vold- og overgrepslinjen", "116 006")],
            [new Line("Legevakt", "116 117")]),

        ["DK"] = new("DK", "Denmark", "112",
            [new Line("Livslinien", "70 201 201"),
             new Line("Sct. Nicolai Tjenesten", "70 120 110")],
            [new Line("Lev Uden Vold", "1888")],
            [new Line("Lægevagten", "1813")]),

        ["FI"] = new("FI", "Finland", "112",
            [new Line("MIELI Crisis Helpline", "09 2525 0111"),
             new Line("Crisis Helpline (English/Arabic)", "09 2525 0113")],
            [new Line("Nollalinja", "080 005 005")],
            [new Line("Health advice", "116 117")]),

        ["PL"] = new("PL", "Poland", "112",
            [new Line("Centrum Wsparcia", "800 70 2222"),
             Eu116],
            [new Line("Niebieska Linia", "800 120 002")],
            [new Line("Teleplatforma", "800 137 200")]),

        ["BR"] = new("BR", "Brazil", "192",
            [new Line("CVV", "188 (24/7)")],
            [new Line("Central de Atendimento à Mulher", "180")],
            [new Line("SAMU", "192")]),

        ["MX"] = new("MX", "Mexico", "911",
            [new Line("Línea de la Vida", "800 911 2000"),
             new Line("SAPTEL", "55 5259 8121")],
            [new Line("Vida sin Violencia", "800 108 4053")],
            [new Line("Emergencias", "911")]),

        ["AR"] = new("AR", "Argentina", "911",
            [new Line("Salud Mental Responde", "0800 999 0091"),
             new Line("Centro de Asistencia al Suicida", "135")],
            [new Line("Violencia de género", "144")],
            [new Line("Emergencias médicas", "107")]),

        ["AE"] = new("AE", "United Arab Emirates", "999",
            [new Line("Estijaba (mental health)", "800 1717")],
            [new Line("Ewa'a Shelters", "800 7283")],
            [new Line("Ministry of Health", "800 11111")]),
    };

    /// <summary>Look up a region by ISO 3166-1 alpha-2 code, falling back to international.</summary>
    public static Region Resolve(string? countryCode) =>
        !string.IsNullOrWhiteSpace(countryCode) && Regions.TryGetValue(countryCode.Trim(), out var r)
            ? r
            : International;

    public static bool IsKnown(string? countryCode) =>
        !string.IsNullOrWhiteSpace(countryCode) && Regions.ContainsKey(countryCode.Trim());

    /// <summary>
    /// Best-effort country for the current request.
    ///
    /// Order matters: an explicit user choice beats an edge header, and an edge
    /// header beats a browser language (a Kiwi with an en-US browser should not
    /// be handed US numbers if Cloudflare already said NZ).
    /// </summary>
    public static string? DetectCountry(HttpContext? http, string? explicitCountry = null)
    {
        if (IsKnown(explicitCountry)) return explicitCountry!.Trim().ToUpperInvariant();
        if (http is null) return null;

        // Edge/CDN geo headers, in order of how much we trust them.
        foreach (var header in (string[])["CF-IPCountry", "X-Vercel-IP-Country", "X-AppEngine-Country", "X-Country-Code"])
        {
            var v = http.Request.Headers[header].FirstOrDefault();
            // Cloudflare uses "XX" for unknown and "T1" for Tor.
            if (!string.IsNullOrWhiteSpace(v) && v is not ("XX" or "T1") && IsKnown(v))
                return v.Trim().ToUpperInvariant();
        }

        // Fall back to the region subtag of Accept-Language (en-NZ -> NZ).
        var accept = http.Request.Headers.AcceptLanguage.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(accept))
        {
            foreach (var part in accept.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = part.Split(';')[0].Trim();
                var bits = tag.Split('-');
                if (bits.Length >= 2)
                {
                    var region = bits[^1];
                    if (region.Length == 2 && IsKnown(region)) return region.ToUpperInvariant();
                }
            }
        }

        return null;
    }

    /// <summary>Markdown bullet list of the given lines.</summary>
    public static string Format(Line[] lines) =>
        string.Join("\n", lines.Select(l => $"• **{l.Name}** — {l.Contact}"));
}
