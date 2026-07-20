import SwiftUI

struct AboutView: View {
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s8) {
                versionSection
                howItWorksSection
            }
            .padding(.horizontal, VxSpace.s5)
            .padding(.top, VxSpace.s4)
            .padding(.bottom, VxSpace.s8)
        }
        .vertexInnerCanvas()
    }

    private var versionSection: some View {
        VxSection {
            VxRow {
                Text("Version").foregroundStyle(Color.textSecondary)
                Spacer()
                Text(appVersion)
                    .foregroundStyle(Color.textPrimary)
                    .monospacedDigit()
            }
            VxDivider()
            VxRow {
                Text("Build").foregroundStyle(Color.textSecondary)
                Spacer()
                Text(buildNumber)
                    .foregroundStyle(Color.textPrimary)
                    .monospacedDigit()
            }
            VxDivider()
            VxRow {
                Text("Configuration").foregroundStyle(Color.textSecondary)
                Spacer()
                Text(buildConfiguration)
                    .foregroundStyle(buildConfigurationColor)
                    .monospaced()
            }
            VxDivider()
            VxRow {
                Text("Copyright").foregroundStyle(Color.textSecondary)
                Spacer()
                Text("© 2026 Mr. Kutin").foregroundStyle(Color.textPrimary)
            }
            VxDivider()
            VxRow {
                Spacer()
                Text("Where paths meet")
                    .font(.system(size: 14, weight: .regular, design: .rounded))
                    .foregroundStyle(Color.textTertiary)
                    .italic()
            }
        }
    }

    private var howItWorksSection: some View {
        VxSection(header: "How it works") {
            VxRow {
                Text("Vertex routes your device through a trusted network vertex — a meeting point where edges converge. Every connection is end-to-end protected with modern cryptography (X25519 + ChaCha20-Poly1305); no relay along the path can read or alter your data.")
                    .font(.vxFootnote)
                    .foregroundStyle(Color.textSecondary)
                    .multilineTextAlignment(.leading)
                Spacer(minLength: 0)
            }
        }
    }

    private var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—"
    }

    private var buildNumber: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "—"
    }

    private var buildConfiguration: String {
        #if DEBUG
        return "Debug"
        #else
        return "Release"
        #endif
    }

    private var buildConfigurationColor: Color {
        #if DEBUG
        return Color.stateTransitioning
        #else
        return Color.textPrimary
        #endif
    }
}
