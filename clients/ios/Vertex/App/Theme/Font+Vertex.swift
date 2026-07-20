import SwiftUI

// Vertex typography scale — see design/UI-SPEC.md §2.
// Three families: SF Pro Rounded (brand voice), SF Pro Text/Display (body),
// SF Mono (facts: IPs, byte counts, hex). Two weights dominate: regular and semibold.
extension Font {
    /// Hero status word ("Connected", "Connecting…"). 28pt rounded semibold.
    static let heroStatus = Font.system(size: 28, weight: .semibold, design: .rounded)

    /// Brand wordmark "VERTEX" in nav bar. 17pt rounded bold (apply tracking 1.5 at site).
    static let brandWordmark = Font.system(size: 17, weight: .bold, design: .rounded)

    /// Section large titles when used. 28pt display bold.
    static let titleLarge = Font.system(size: 28, weight: .bold, design: .default)

    /// Section title in main content. 22pt display semibold.
    static let vxTitle = Font.system(size: 22, weight: .semibold, design: .default)

    /// Card titles, primary list rows. 17pt text semibold.
    static let vxHeadline = Font.system(size: 17, weight: .semibold, design: .default)

    /// Default body. 17pt regular.
    static let vxBody = Font.system(size: 17, weight: .regular, design: .default)

    /// Selected list values. 17pt medium.
    static let vxBodyEmphasized = Font.system(size: 17, weight: .medium, design: .default)

    /// StatusPill body. 16pt regular.
    static let vxCallout = Font.system(size: 16, weight: .regular, design: .default)

    /// Sub-labels in cards ("Vertex", "Edge"). 15pt regular.
    static let vxSubheadline = Font.system(size: 15, weight: .regular, design: .default)

    /// Footers, helper text. 13pt regular.
    static let vxFootnote = Font.system(size: 13, weight: .regular, design: .default)

    /// Accessory chips. 12pt regular.
    static let vxCaption = Font.system(size: 12, weight: .regular, design: .default)

    /// Schemes, codes. 12pt monospaced medium.
    static let vxCaptionMono = Font.system(size: 12, weight: .medium, design: .monospaced)

    /// Stat values ("12.4 MB", uptime). 17pt monospaced medium.
    static let statValue = Font.system(size: 17, weight: .medium, design: .monospaced)

    /// Hero-scale mono (StatsSheet). 28pt monospaced semibold.
    static let statValueLarge = Font.system(size: 28, weight: .semibold, design: .monospaced)

    /// Pubkey hex. 13pt monospaced regular.
    static let identityHex = Font.system(size: 13, weight: .regular, design: .monospaced)
}
