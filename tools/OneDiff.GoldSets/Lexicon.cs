using System.Globalization;
using System.Text;

namespace OneDiff.GoldSets;

/// <summary>
/// The text of one language: sentence templates in two phrasings (the second is a rewrite of the first), the roles, defined
/// terms, headings and value formats. All of it is written for this generator; none of it is quoted from any document.
/// Slots: {U} supplier role, {C} customer role, {X} defined term, {A} amount, {D} duration, {P} percentage, {T} date,
/// {R} cross-reference, {J} jurisdiction, {M} and {W} interchangeable words (term swaps).
/// </summary>
internal sealed class Lexicon
{
    public required string Code { get; init; }
    /// <summary>Whether sentences are joined by a space.</summary>
    public bool Spaced { get; init; } = true;
    public required string Title { get; init; }
    public required string Preamble { get; init; }
    public required string[] Recitals { get; init; }
    public required string[] Headings { get; init; }
    public required string[] Definitions { get; init; }
    public required string Closing { get; init; }
    public required string[] TableHeader { get; init; }
    public required string[] TableItems { get; init; }
    public required string Supplier { get; init; }
    public required string Customer { get; init; }
    public required string[] Terms { get; init; }
    public required string[] Modal { get; init; }
    public required string[] Adverb { get; init; }
    public required string[] Jurisdictions { get; init; }
    public required string SectionWord { get; init; }
    public required string[] Months { get; init; }
    public required string[] DurationUnits { get; init; }
    /// <summary>Amount pattern with {0}.</summary>
    public required string AmountFormat { get; init; }
    public required string PercentFormat { get; init; }
    /// <summary>Date pattern with {d}, {M} (month name), {y}.</summary>
    public required string DateFormat { get; init; }
    public required string DurationFormat { get; init; }
    public required string ThousandsSeparator { get; init; }
    public required string Currency { get; init; }
    public required (string V0, string V1)[] Templates { get; init; }

    public string FormatAmount(long n)
    {
        string digits = n.ToString("N0", CultureInfo.InvariantCulture).Replace(",", ThousandsSeparator, StringComparison.Ordinal);
        return AmountFormat.Replace("{0}", digits, StringComparison.Ordinal);
    }

    public string FormatPercent(int p) => PercentFormat.Replace("{0}", p.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public string FormatDate(int y, int m, int d) => DateFormat
        .Replace("{d}", d.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{M}", Months[m - 1], StringComparison.Ordinal)
        .Replace("{y}", y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public string FormatDuration(int n, int unit) => DurationFormat
        .Replace("{0}", n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{u}", DurationUnits[unit], StringComparison.Ordinal);

    public string Ref(string label) => SectionWord.Replace("{0}", label, StringComparison.Ordinal);

    public static readonly string[] Codes = ["en", "de", "fr", "es", "zh", "ja", "ar"];

    public static Lexicon For(string code) => All[code];

    private static readonly Dictionary<string, Lexicon> All = new()
    {
        ["en"] = new Lexicon
        {
            Code = "en",
            Title = "Services Agreement",
            Preamble = "This Agreement is made on {T} between Northwind Ltd ({U}) and Contoso Inc ({C}).",
            Recitals =
            [
                "{U} provides the {X} to business customers.", "{C} wishes to obtain the {X} on the terms of this Agreement.",
                "The parties have discussed the scope of the {X} in detail.", "Both parties have the authority to enter into this Agreement.",
                "The parties want to record their agreement in writing.", "Each party has taken legal advice before signing.",
            ],
            Headings = ["Definitions", "Payment", "Term and termination", "Liability", "Confidentiality", "Governing law", "Warranty", "Notices", "Assignment", "Delivery"],
            Definitions =
            [
                "\"{X}\" means the services described in Schedule 1.", "\"Business Day\" means a day other than a Saturday, Sunday or public holiday.",
                "\"Confidential Information\" means all non-public information disclosed by one party to the other.",
            ],
            Closing = "Signed by the parties on the date written above.",
            TableHeader = ["Item", "Period", "Fee"],
            TableItems = ["Setup", "Hosting", "Support", "Training", "Licence", "Maintenance"],
            Supplier = "the Supplier", Customer = "the Customer",
            Terms = ["Services", "Deliverables", "Platform", "Software"],
            Modal = ["shall", "must"], Adverb = ["promptly", "without delay"],
            Jurisdictions = ["England", "New York", "Ireland", "Delaware"],
            SectionWord = "Section {0}",
            Months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"],
            DurationUnits = ["days", "months", "years"],
            AmountFormat = "USD {0}", PercentFormat = "{0}%", DateFormat = "{d} {M} {y}", DurationFormat = "{0} {u}",
            ThousandsSeparator = ",", Currency = "USD",
            Templates =
            [
                ("{C} {M} pay each invoice within {D} of receipt.", "{C} {M} settle every invoice no later than {D} after it is received."),
                ("The monthly fee for the {X} is {A}, payable in advance.", "A fee of {A} per month is due in advance for the {X}."),
                ("Late payments bear interest at {P} per year.", "Overdue amounts accrue interest of {P} annually."),
                ("This Agreement starts on {T} and continues for {D}.", "This Agreement takes effect on {T} and remains in force for {D}."),
                ("Either party may terminate this Agreement by giving {D} written notice.", "This Agreement may be ended by either party on {D} notice in writing."),
                ("The total liability of {U} under this Agreement is limited to {A}.", "In no event will the aggregate liability of {U} exceed {A}."),
                ("Each party {M} keep the other party's confidential information secret for {D} after termination.", "Neither party will disclose the confidential information of the other for {D} after termination."),
                ("This Agreement is governed by the laws of {J}.", "The laws of {J} apply to this Agreement."),
                ("{U} warrants that the {X} will conform to the specification for {D} from delivery.", "For {D} after delivery, {U} guarantees that the {X} meets the specification."),
                ("Notices {M} be given {W} in writing to the address set out in {R}.", "Any notice must be written and sent to the address in {R}."),
                ("Neither party may assign this Agreement without the prior written consent of the other.", "A party needs the written consent of the other party before it assigns this Agreement."),
                ("{U} {M} deliver the {X} on or before {T}.", "The {X} will be delivered by {U} no later than {T}."),
            ],
        },
        ["de"] = new Lexicon
        {
            Code = "de",
            Title = "Dienstleistungsvertrag",
            Preamble = "Dieser Vertrag wird am {T} zwischen Northwind GmbH ({U}) und Contoso AG ({C}) geschlossen.",
            Recitals =
            [
                "{U} erbringt die {X} für Geschäftskunden.", "{C} möchte die {X} zu den Bedingungen dieses Vertrags beziehen.",
                "Die Parteien haben den Umfang der {X} ausführlich besprochen.", "Beide Parteien sind zum Abschluss dieses Vertrags berechtigt.",
                "Die Parteien möchten ihre Vereinbarung schriftlich festhalten.", "Jede Partei hat sich vor der Unterzeichnung rechtlich beraten lassen.",
            ],
            Headings = ["Begriffsbestimmungen", "Zahlung", "Laufzeit und Kündigung", "Haftung", "Vertraulichkeit", "Anwendbares Recht", "Gewährleistung", "Mitteilungen", "Abtretung", "Lieferung"],
            Definitions =
            [
                "\"{X}\" bezeichnet die in Anlage 1 beschriebenen Leistungen.", "\"Werktag\" bezeichnet einen Tag, der kein Samstag, Sonntag oder gesetzlicher Feiertag ist.",
                "\"Vertrauliche Informationen\" bezeichnet alle nicht öffentlichen Informationen, die eine Partei der anderen offenlegt.",
            ],
            Closing = "Von den Parteien am oben genannten Tag unterzeichnet.",
            TableHeader = ["Position", "Zeitraum", "Entgelt"],
            TableItems = ["Einrichtung", "Hosting", "Support", "Schulung", "Lizenz", "Wartung"],
            Supplier = "der Lieferant", Customer = "der Kunde",
            Terms = ["Leistungen", "Lieferungen", "Plattform", "Software"],
            Modal = ["muss", "soll"], Adverb = ["unverzüglich", "ohne Verzögerung"],
            Jurisdictions = ["Deutschland", "Österreich", "der Schweiz", "Frankreich"],
            SectionWord = "Abschnitt {0}",
            Months = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"],
            DurationUnits = ["Tagen", "Monaten", "Jahren"],
            AmountFormat = "{0} EUR", PercentFormat = "{0} %", DateFormat = "{d}. {M} {y}", DurationFormat = "{0} {u}",
            ThousandsSeparator = ".", Currency = "EUR",
            Templates =
            [
                ("{C} {M} jede Rechnung innerhalb von {D} nach Erhalt bezahlen.", "{C} {M} jede Rechnung spätestens nach {D} nach ihrem Eingang begleichen."),
                ("Das monatliche Entgelt für die {X} beträgt {A} und ist im Voraus zu zahlen.", "Für die {X} ist monatlich im Voraus ein Entgelt von {A} fällig."),
                ("Bei Zahlungsverzug fallen Zinsen in Höhe von {P} pro Jahr an.", "Überfällige Beträge werden mit {P} jährlich verzinst."),
                ("Dieser Vertrag beginnt am {T} und hat eine Laufzeit von {D}.", "Dieser Vertrag tritt am {T} in Kraft und gilt für eine Dauer von {D}."),
                ("Jede Partei kann diesen Vertrag mit einer Frist von {D} schriftlich kündigen.", "Eine Kündigung dieses Vertrags ist jeder Partei schriftlich mit einer Frist von {D} möglich."),
                ("Die Haftung ist insgesamt auf {A} begrenzt.", "Die Gesamthaftung aus diesem Vertrag übersteigt in keinem Fall {A}."),
                ("Jede Partei {M} vertrauliche Informationen der anderen Partei für einen Zeitraum von {D} nach Vertragsende geheim halten.", "Keine Partei darf vertrauliche Informationen der anderen Partei für einen Zeitraum von {D} nach Vertragsende offenlegen."),
                ("Es gilt das Recht von {J}.", "Dieser Vertrag unterliegt dem Recht von {J}."),
                ("Für einen Zeitraum von {D} nach der Lieferung gewährleistet {U}, dass die {X} der Spezifikation entsprechen.", "{U} garantiert, dass die {X} der Spezifikation für einen Zeitraum von {D} ab Lieferung entsprechen."),
                ("Mitteilungen {M} {W} schriftlich an die in {R} genannte Adresse gerichtet werden.", "Jede Mitteilung ist schriftlich an die Adresse in {R} zu senden."),
                ("Keine Partei darf diesen Vertrag ohne vorherige schriftliche Zustimmung der anderen Partei abtreten.", "Vor einer Abtretung dieses Vertrags benötigt eine Partei die schriftliche Zustimmung der anderen Partei."),
                ("{U} {M} die {X} bis spätestens {T} liefern.", "Die {X} werden von {U} spätestens am {T} geliefert."),
            ],
        },
        ["fr"] = new Lexicon
        {
            Code = "fr",
            Title = "Contrat de prestation de services",
            Preamble = "Le présent Contrat est conclu le {T} entre Northwind SARL ({U}) et Contoso SA ({C}).",
            Recitals =
            [
                "{U} fournit les {X} à des clients professionnels.", "{C} souhaite obtenir les {X} aux conditions du présent Contrat.",
                "Les parties ont discuté en détail de l'étendue des {X}.", "Chaque partie a le pouvoir de conclure le présent Contrat.",
                "Les parties souhaitent consigner leur accord par écrit.", "Chaque partie a pris conseil avant de signer.",
            ],
            Headings = ["Définitions", "Paiement", "Durée et résiliation", "Responsabilité", "Confidentialité", "Droit applicable", "Garantie", "Notifications", "Cession", "Livraison"],
            Definitions =
            [
                "\"{X}\" désigne les prestations décrites à l'Annexe 1.", "\"Jour Ouvré\" désigne un jour autre qu'un samedi, un dimanche ou un jour férié.",
                "\"Informations Confidentielles\" désigne toutes les informations non publiques communiquées par une partie à l'autre.",
            ],
            Closing = "Signé par les parties à la date indiquée ci-dessus.",
            TableHeader = ["Élément", "Période", "Prix"],
            TableItems = ["Mise en place", "Hébergement", "Assistance", "Formation", "Licence", "Maintenance"],
            Supplier = "le Fournisseur", Customer = "le Client",
            Terms = ["Services", "Livrables", "Prestations", "Logiciels"],
            Modal = ["doit", "devra"], Adverb = ["sans délai", "immédiatement"],
            Jurisdictions = ["la France", "la Belgique", "la Suisse", "le Luxembourg"],
            SectionWord = "l'article {0}",
            Months = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre"],
            DurationUnits = ["jours", "mois", "ans"],
            AmountFormat = "{0} EUR", PercentFormat = "{0} %", DateFormat = "{d} {M} {y}", DurationFormat = "{0} {u}",
            ThousandsSeparator = " ", Currency = "EUR",
            Templates =
            [
                ("{C} {M} payer chaque facture dans un délai de {D} à compter de sa réception.", "{C} {M} régler toute facture au plus tard {D} après l'avoir reçue."),
                ("La redevance mensuelle pour les {X} est de {A}, payable d'avance.", "Une redevance de {A} par mois est due d'avance pour les {X}."),
                ("Tout retard de paiement porte intérêt au taux de {P} par an.", "Les sommes échues portent intérêt à {P} par an."),
                ("Le présent Contrat prend effet le {T} et dure {D}.", "Le présent Contrat entre en vigueur le {T} pour une durée de {D}."),
                ("Chaque partie peut résilier le présent Contrat moyennant un préavis écrit de {D}.", "Le présent Contrat peut être résilié par écrit par l'une ou l'autre partie avec un préavis de {D}."),
                ("La responsabilité totale au titre du présent Contrat est limitée à {A}.", "En aucun cas la responsabilité cumulée ne dépassera {A}."),
                ("Chaque partie {M} garder confidentielles les informations de l'autre partie pendant {D} après la fin du Contrat.", "Aucune partie ne divulguera les informations confidentielles de l'autre pendant {D} après la fin du Contrat."),
                ("Le présent Contrat est régi par le droit de {J}.", "Le droit de {J} s'applique au présent Contrat."),
                ("{U} garantit que les {X} seront conformes à la spécification pendant {D} à compter de la livraison.", "Pendant {D} après la livraison, {U} garantit que les {X} respectent la spécification."),
                ("Toute notification {M} être adressée {W} par écrit à l'adresse indiquée à {R}.", "Toute notification est écrite et envoyée à l'adresse figurant à {R}."),
                ("Aucune partie ne peut céder le présent Contrat sans l'accord écrit préalable de l'autre.", "Une partie doit obtenir l'accord écrit de l'autre avant de céder le présent Contrat."),
                ("{U} {M} livrer les {X} au plus tard le {T}.", "Les {X} seront fournis par {U} au plus tard le {T}."),
            ],
        },
        ["es"] = new Lexicon
        {
            Code = "es",
            Title = "Contrato de servicios",
            Preamble = "Este Contrato se celebra el {T} entre Northwind S.L. ({U}) y Contoso S.A. ({C}).",
            Recitals =
            [
                "{U} presta los {X} a clientes empresariales.", "{C} desea obtener los {X} en los términos de este Contrato.",
                "Las partes han analizado en detalle el alcance de los {X}.", "Ambas partes tienen capacidad para celebrar este Contrato.",
                "Las partes desean dejar constancia escrita de su acuerdo.", "Cada parte ha recibido asesoramiento jurídico antes de firmar.",
            ],
            Headings = ["Definiciones", "Pago", "Duración y resolución", "Responsabilidad", "Confidencialidad", "Ley aplicable", "Garantía", "Notificaciones", "Cesión", "Entrega"],
            Definitions =
            [
                "\"{X}\" significa los servicios descritos en el Anexo 1.", "\"Día Hábil\" significa un día que no sea sábado, domingo ni festivo.",
                "\"Información Confidencial\" significa toda la información no pública que una parte revele a la otra.",
            ],
            Closing = "Firmado por las partes en la fecha indicada arriba.",
            TableHeader = ["Concepto", "Periodo", "Precio"],
            TableItems = ["Puesta en marcha", "Alojamiento", "Soporte", "Formación", "Licencia", "Mantenimiento"],
            Supplier = "el Proveedor", Customer = "el Cliente",
            Terms = ["Servicios", "Entregables", "Prestaciones", "Programas"],
            Modal = ["debe", "deberá"], Adverb = ["sin demora", "de inmediato"],
            Jurisdictions = ["España", "México", "Argentina", "Colombia"],
            SectionWord = "la cláusula {0}",
            Months = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"],
            DurationUnits = ["días", "meses", "años"],
            AmountFormat = "{0} EUR", PercentFormat = "{0} %", DateFormat = "{d} de {M} de {y}", DurationFormat = "{0} {u}",
            ThousandsSeparator = ".", Currency = "EUR",
            Templates =
            [
                ("{C} {M} pagar cada factura en un plazo de {D} desde su recepción.", "{C} {M} abonar toda factura como máximo {D} después de recibirla."),
                ("La cuota mensual por los {X} es de {A}, pagadera por adelantado.", "Se adeuda por adelantado una cuota de {A} al mes por los {X}."),
                ("Los pagos atrasados devengan intereses del {P} anual.", "Las cantidades vencidas generan un interés del {P} al año."),
                ("Este Contrato comienza el {T} y tiene una duración de {D}.", "Este Contrato entra en vigor el {T} y permanece vigente durante {D}."),
                ("Cualquiera de las partes puede resolver este Contrato con un preaviso escrito de {D}.", "Este Contrato puede darse por terminado por escrito por cualquiera de las partes con {D} de preaviso."),
                ("La responsabilidad total derivada de este Contrato se limita a {A}.", "En ningún caso la responsabilidad acumulada superará {A}."),
                ("Cada parte {M} mantener en secreto la información confidencial de la otra parte durante {D} tras la terminación.", "Ninguna parte revelará la información confidencial de la otra durante {D} tras la terminación."),
                ("Este Contrato se rige por las leyes de {J}.", "Las leyes de {J} se aplican a este Contrato."),
                ("{U} garantiza que los {X} cumplirán la especificación durante {D} desde la entrega.", "Durante {D} después de la entrega, {U} garantiza que los {X} cumplen la especificación."),
                ("Toda notificación {M} enviarse {W} por escrito a la dirección indicada en {R}.", "Cualquier notificación será escrita y enviada a la dirección de {R}."),
                ("Ninguna parte puede ceder este Contrato sin el consentimiento previo y por escrito de la otra.", "Una parte necesita el consentimiento escrito de la otra antes de ceder este Contrato."),
                ("{U} {M} entregar los {X} a más tardar el {T}.", "Los {X} serán entregados por {U} a más tardar el {T}."),
            ],
        },
        ["zh"] = new Lexicon
        {
            Code = "zh",
            Spaced = false,
            Title = "服务协议",
            Preamble = "本协议由北风有限公司（{U}）与康托索公司（{C}）于{T}签订。",
            Recitals =
            [
                "{U}向企业客户提供{X}。", "{C}希望按照本协议的条款获得{X}。",
                "双方已详细讨论{X}的范围。", "双方均有权签订本协议。",
                "双方希望以书面形式记录其协议。", "各方在签署前均已获得法律意见。",
            ],
            Headings = ["定义", "付款", "期限与终止", "责任", "保密", "适用法律", "保证", "通知", "转让", "交付"],
            Definitions =
            [
                "「{X}」是指附件一所述的服务。", "「工作日」是指除星期六、星期日和法定节假日以外的日子。",
                "「保密信息」是指一方向另一方披露的所有非公开信息。",
            ],
            Closing = "双方于上述日期签署。",
            TableHeader = ["项目", "期间", "费用"],
            TableItems = ["开通", "托管", "支持", "培训", "许可", "维护"],
            Supplier = "供应商", Customer = "客户",
            Terms = ["服务", "交付物", "平台", "软件"],
            Modal = ["应当", "须"], Adverb = ["立即", "及时"],
            Jurisdictions = ["中国", "新加坡", "香港", "英国"],
            SectionWord = "第{0}条",
            Months = ["1月", "2月", "3月", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月"],
            DurationUnits = ["天", "个月", "年"],
            AmountFormat = "人民币{0}元", PercentFormat = "{0}%", DateFormat = "{y}年{M}{d}日", DurationFormat = "{0}{u}",
            ThousandsSeparator = ",", Currency = "CNY",
            Templates =
            [
                ("{C}{M}自收到每张发票之日起{D}内付款。", "{C}{M}在收到发票后{D}内结清每张发票。"),
                ("{X}的月费为{A}，须提前支付。", "{X}每月收费{A}，应提前缴纳。"),
                ("逾期付款按每年{P}计收利息。", "逾期款项按年利率{P}计息。"),
                ("本协议自{T}起生效，有效期为{D}。", "本协议于{T}开始，期限{D}。"),
                ("任何一方可提前{D}书面通知终止本协议。", "任何一方均可通过提前{D}发出书面通知结束本协议。"),
                ("本协议项下的责任总额以{A}为限。", "在任何情况下，累计责任均不超过{A}。"),
                ("各方{M}在协议终止后{D}内对另一方的保密信息保密。", "协议终止后{D}内，任何一方不得披露另一方的保密信息。"),
                ("本协议适用{J}法律。", "{J}法律适用于本协议。"),
                ("{U}保证{X}自交付之日起{D}内符合规格。", "交付后{D}内，{U}保证{X}符合规格要求。"),
                ("通知{M}{W}以书面形式发送至{R}所列地址。", "任何通知均须以书面形式发送至{R}中的地址。"),
                ("未经另一方事先书面同意，任何一方不得转让本协议。", "一方转让本协议前，须取得另一方的书面同意。"),
                ("{U}{M}不迟于{T}交付{X}。", "{U}将在{T}之前交付{X}。"),
            ],
        },
        ["ja"] = new Lexicon
        {
            Code = "ja",
            Spaced = false,
            Title = "業務委託契約書",
            Preamble = "本契約は、{T}に、ノースウィンド株式会社（{U}）とコントソ株式会社（{C}）との間で締結される。",
            Recitals =
            [
                "{U}は、法人顧客に{X}を提供している。", "{C}は、本契約の条件に従って{X}の提供を受けることを希望している。",
                "両当事者は、{X}の範囲について詳細に協議した。", "両当事者は、本契約を締結する権限を有している。",
                "両当事者は、合意内容を書面に残すことを望んでいる。", "各当事者は、署名前に法的助言を受けている。",
            ],
            Headings = ["定義", "支払", "期間および解除", "責任", "秘密保持", "準拠法", "保証", "通知", "譲渡", "納入"],
            Definitions =
            [
                "「{X}」とは、別紙1に記載された役務をいう。", "「営業日」とは、土曜日、日曜日および祝日以外の日をいう。",
                "「秘密情報」とは、一方の当事者が他方に開示した非公開の情報をいう。",
            ],
            Closing = "上記の日付に両当事者が署名した。",
            TableHeader = ["項目", "期間", "料金"],
            TableItems = ["初期設定", "ホスティング", "サポート", "研修", "ライセンス", "保守"],
            Supplier = "供給者", Customer = "顧客",
            Terms = ["サービス", "成果物", "プラットフォーム", "ソフトウェア"],
            Modal = ["ものとする", "義務を負う"], Adverb = ["速やかに", "遅滞なく"],
            Jurisdictions = ["日本", "シンガポール", "英国", "ニューヨーク州"],
            SectionWord = "第{0}条",
            Months = ["1月", "2月", "3月", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月"],
            DurationUnits = ["日", "か月", "年"],
            AmountFormat = "{0}円", PercentFormat = "{0}%", DateFormat = "{y}年{M}{d}日", DurationFormat = "{0}{u}",
            ThousandsSeparator = ",", Currency = "JPY",
            Templates =
            [
                ("{C}は、各請求書を受領後{D}以内に支払う{M}。", "{C}は、請求書の受領から{D}を超えない期間内に全額を支払うものとする。"),
                ("{X}の月額料金は{A}とし、前払いで支払う。", "{X}について、毎月{A}の料金を前払いで支払うものとする。"),
                ("支払が遅延した場合、年{P}の利息を支払う。", "延滞金には年率{P}の利息が付される。"),
                ("本契約は{T}に開始し、{D}間継続する。", "本契約は{T}に効力を生じ、{D}にわたり有効とする。"),
                ("各当事者は、{D}前までに書面で通知することにより本契約を解除できる。", "いずれの当事者も、{D}の事前の書面通知をもって本契約を終了させることができる。"),
                ("本契約に基づく責任の総額は{A}を上限とする。", "いかなる場合も、累積責任額は{A}を超えない。"),
                ("各当事者は、契約終了後{D}間、相手方の秘密情報を秘密として保持する{M}。", "いずれの当事者も、契約終了後{D}間、相手方の秘密情報を開示してはならない。"),
                ("本契約は{J}の法律に準拠する。", "本契約には{J}の法律が適用される。"),
                ("{U}は、{X}が引渡しから{D}間仕様に適合することを保証する。", "引渡し後{D}間、{U}は{X}が仕様を満たすことを保証する。"),
                ("通知は、{W}書面で{R}に記載の住所宛てに送付する{M}。", "すべての通知は、書面により{R}記載の住所へ送らなければならない。"),
                ("いずれの当事者も、相手方の事前の書面による同意なく本契約を譲渡してはならない。", "当事者は、本契約を譲渡する前に相手方の書面による同意を得なければならない。"),
                ("{U}は、{T}までに{X}を納入する{M}。", "{X}は、{U}により{T}までに納入されるものとする。"),
            ],
        },
        ["ar"] = new Lexicon
        {
            Code = "ar",
            Title = "اتفاقية خدمات",
            Preamble = "أبرمت هذه الاتفاقية في {T} بين شركة نورث ويند ({U}) وشركة كونتوسو ({C}).",
            Recitals =
            [
                "يقدم {U} {X} لعملاء الأعمال.", "يرغب {C} في الحصول على {X} وفق شروط هذه الاتفاقية.",
                "ناقش الطرفان نطاق {X} بالتفصيل.", "لكل من الطرفين صلاحية إبرام هذه الاتفاقية.",
                "يرغب الطرفان في تسجيل اتفاقهما كتابةً.", "حصل كل طرف على مشورة قانونية قبل التوقيع.",
            ],
            Headings = ["التعريفات", "الدفع", "المدة والإنهاء", "المسؤولية", "السرية", "القانون الواجب التطبيق", "الضمان", "الإشعارات", "التنازل", "التسليم"],
            Definitions =
            [
                "\"{X}\" تعني الخدمات الموضحة في الملحق 1.", "\"يوم العمل\" يعني أي يوم عدا السبت والجمعة والعطلات الرسمية.",
                "\"المعلومات السرية\" تعني جميع المعلومات غير العامة التي يفصح عنها أحد الطرفين للآخر.",
            ],
            Closing = "وقع الطرفان في التاريخ المذكور أعلاه.",
            TableHeader = ["البند", "المدة", "الرسوم"],
            TableItems = ["الإعداد", "الاستضافة", "الدعم", "التدريب", "الترخيص", "الصيانة"],
            Supplier = "المورد", Customer = "العميل",
            Terms = ["الخدمات", "المخرجات", "المنصة", "البرمجيات"],
            Modal = ["يجب", "يتعين"], Adverb = ["فورًا", "دون تأخير"],
            Jurisdictions = ["مصر", "السعودية", "الإمارات", "الأردن"],
            SectionWord = "البند {0}",
            Months = ["يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر"],
            DurationUnits = ["يومًا", "أشهر", "سنوات"],
            AmountFormat = "{0} دولار", PercentFormat = "{0}%", DateFormat = "{d} {M} {y}", DurationFormat = "{0} {u}",
            ThousandsSeparator = ",", Currency = "USD",
            Templates =
            [
                ("{M} على {C} دفع كل فاتورة خلال {D} من تاريخ استلامها.", "{M} على {C} تسوية كل فاتورة في موعد أقصاه {D} بعد استلامها."),
                ("الرسوم الشهرية لـ{X} هي {A}، وتُدفع مقدمًا.", "تُستحق رسوم قدرها {A} شهريًا مقدمًا مقابل {X}."),
                ("تترتب على المدفوعات المتأخرة فائدة بنسبة {P} سنويًا.", "تُحتسب فائدة بنسبة {P} في السنة على المبالغ المتأخرة."),
                ("يبدأ هذا العقد في {T} ويستمر لمدة {D}.", "يسري هذا العقد اعتبارًا من {T} ويظل ساريًا لمدة {D}."),
                ("يجوز لأي طرف إنهاء هذا العقد بإشعار كتابي مدته {D}.", "يمكن لأي من الطرفين إنهاء هذا العقد بإخطار مكتوب قبل {D}."),
                ("تقتصر المسؤولية الإجمالية بموجب هذا العقد على {A}.", "لا تتجاوز المسؤولية التراكمية بأي حال من الأحوال {A}."),
                ("{M} على كل طرف الحفاظ على سرية معلومات الطرف الآخر لمدة {D} بعد انتهاء العقد.", "لا يجوز لأي طرف الإفصاح عن المعلومات السرية للطرف الآخر لمدة {D} بعد انتهاء العقد."),
                ("يخضع هذا العقد لقوانين {J}.", "تسري قوانين {J} على هذا العقد."),
                ("يضمن {U} مطابقة {X} للمواصفات لمدة {D} من التسليم.", "لمدة {D} بعد التسليم، يضمن {U} أن {X} تستوفي المواصفات."),
                ("{M} إرسال الإشعارات {W} كتابةً إلى العنوان المذكور في {R}.", "يجب إرسال أي إشعار كتابةً إلى العنوان الوارد في {R}."),
                ("لا يجوز لأي طرف التنازل عن هذا العقد دون موافقة كتابية مسبقة من الطرف الآخر.", "يحتاج الطرف إلى موافقة كتابية من الطرف الآخر قبل التنازل عن هذا العقد."),
                ("{M} على {U} تسليم {X} في موعد أقصاه {T}.", "سيسلّم {U} {X} في موعد لا يتجاوز {T}."),
            ],
        },
    };

    /// <summary>Capitalizes the first letter of a spaced-script sentence (a role such as "the Supplier" may start it).</summary>
    public static string CapitalizeFirst(string s)
    {
        if (s.Length == 0 || !char.IsLower(s[0]))
        {
            return s;
        }

        return new StringBuilder(s) { [0] = char.ToUpperInvariant(s[0]) }.ToString();
    }
}
