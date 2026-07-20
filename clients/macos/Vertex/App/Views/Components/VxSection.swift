import SwiftUI

/// Vertex section primitive — see design/UI-SPEC.md §6.5 / §7.4.
///
/// A grouped container with optional uppercased header and footer, rendered
/// as a `bgSurface` plate with a hairline `borderSubtle` outline. Rows live
/// as direct children. Use with `VxRow` / `VxDivider` / custom row content.
///
/// Used by Settings, IdentityKey, About, BrokerList, ExitList — every inner
/// screen. Built by hand (not Form/List) to bypass iOS 26 Liquid Glass
/// material tinting on scrollable containers.
struct VxSection<Content: View>: View {
    let header: String?
    let footer: String?
    @ViewBuilder var content: () -> Content

    init(header: String? = nil, footer: String? = nil, @ViewBuilder content: @escaping () -> Content) {
        self.header = header
        self.footer = footer
        self.content = content
    }

    var body: some View {
        VStack(alignment: .leading, spacing: VxSpace.s2) {
            if let header {
                Text(header)
                    .font(.vxCaption)
                    .tracking(0.8)
                    .textCase(.uppercase)
                    .foregroundStyle(Color.textTertiary)
                    .padding(.leading, VxSpace.s4)
            }
            VStack(alignment: .leading, spacing: 0) {
                content()
            }
            .background(Color.bgSurface, in: .rect(cornerRadius: VxRadius.lg))
            .overlay(
                RoundedRectangle(cornerRadius: VxRadius.lg)
                    .strokeBorder(Color.borderSubtle, lineWidth: 0.5)
            )
            if let footer {
                Text(footer)
                    .font(.vxFootnote)
                    .foregroundStyle(Color.textSecondary)
                    .padding(.horizontal, VxSpace.s4)
                    .padding(.top, VxSpace.s1)
            }
        }
    }
}

/// A single row inside a `VxSection`. Default vertical+horizontal padding.
struct VxRow<Content: View>: View {
    @ViewBuilder var content: () -> Content
    var body: some View {
        HStack(spacing: VxSpace.s3) {
            content()
        }
        .padding(.vertical, VxSpace.s3)
        .padding(.horizontal, VxSpace.s4)
        .frame(minHeight: 44)
    }
}

/// Hairline divider between rows of a `VxSection`. Indented from the leading
/// edge to align with text content (matches iOS list separator inset).
struct VxDivider: View {
    var leadingInset: CGFloat = VxSpace.s4

    var body: some View {
        Rectangle()
            .fill(Color.borderSubtle)
            .frame(height: 0.5)
            .padding(.leading, leadingInset)
    }
}
