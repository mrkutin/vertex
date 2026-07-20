import VertexCore
import SwiftUI

struct IdentityKeyView: View {
    @State private var pubkeyHex: String = ""
    @State private var isRevealed: Bool = false
    @State private var placeholderMessage: String = "No identity key generated yet. The key is created on first connect."

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s8) {
                VxSection(
                    header: "Public Identity",
                    footer: "This device's permanent identity in the Vertex graph. Stored in the iOS Keychain — never leaves this device. Long-press the fingerprint to copy the full key for admin reset on the edge."
                ) {
                    if pubkeyHex.isEmpty {
                        VxRow {
                            Text(placeholderMessage)
                                .font(.vxBody)
                                .foregroundStyle(Color.textSecondary)
                                .multilineTextAlignment(.leading)
                            Spacer(minLength: 0)
                        }
                    } else {
                        identityContent
                    }
                }
            }
            .padding(.horizontal, VxSpace.s5)
            .padding(.top, VxSpace.s4)
            .padding(.bottom, VxSpace.s8)
        }
        .vertexInnerCanvas()
        .navigationTitle("Public Identity")
        .navigationBarTitleDisplayMode(.inline)
        .task { loadKey() }
    }

    private var identityContent: some View {
        VStack(alignment: .leading, spacing: VxSpace.s3) {
            Text("Fingerprint")
                .font(.vxSubheadline)
                .foregroundStyle(Color.textSecondary)

            HStack(spacing: VxSpace.s3) {
                Text(fingerprint)
                    .font(.identityHex)
                    .foregroundStyle(Color.textPrimary)
                    .contextMenu { copyMenuItem }
                Spacer(minLength: VxSpace.s2)
                Button {
                    withAnimation(.easeInOut(duration: 0.2)) {
                        isRevealed.toggle()
                    }
                    Haptics.selection()
                } label: {
                    HStack(spacing: 4) {
                        Text(isRevealed ? "Hide" : "Reveal")
                            .font(.vxBody)
                        Image(systemName: isRevealed ? "chevron.up" : "chevron.right")
                            .font(.caption.weight(.semibold))
                    }
                    .foregroundStyle(Color.accentPrimary)
                }
                .buttonStyle(.plain)
            }

            if isRevealed {
                ScrollView(.horizontal, showsIndicators: false) {
                    Text(pubkeyHex)
                        .font(.identityHex)
                        .foregroundStyle(Color.textPrimary)
                        .padding(VxSpace.s3)
                }
                .background(Color.bgSurfaceMuted, in: .rect(cornerRadius: VxRadius.md))
                .contextMenu { copyMenuItem }
            }
        }
        .padding(VxSpace.s4)
    }

    @ViewBuilder
    private var copyMenuItem: some View {
        Button {
            UIPasteboard.general.string = pubkeyHex
            Haptics.notify(.success)
        } label: {
            Label("Copy for admin reset", systemImage: "doc.on.doc")
        }
    }

    /// First 16 hex chars of the public key, formatted as 4 groups of 4 separated by spaces.
    /// E.g. "a3f1c290b847fe05..." → "a3f1 c290 b847 fe05"
    private var fingerprint: String {
        let prefix = pubkeyHex.prefix(16)
        let chars = Array(prefix)
        var groups: [String] = []
        var idx = 0
        while idx < chars.count {
            let end = min(idx + 4, chars.count)
            groups.append(String(chars[idx..<end]))
            idx += 4
        }
        return groups.joined(separator: " ")
    }

    private func loadKey() {
        do {
            let raw = try KeychainStore.loadIdentityKey()
            let key = try IdentityKey(rawRepresentation: raw)
            pubkeyHex = key.publicKeyHex
        } catch KeychainError.notFound {
            pubkeyHex = ""
            placeholderMessage = "No identity key generated yet. The key is created on first connect."
        } catch KeychainError.locked {
            // Boot-to-first-unlock window — Keychain item exists but
            // can't be read until the user unlocks the device.
            pubkeyHex = ""
            placeholderMessage = "iPhone hasn't been unlocked since reboot — identity key unavailable. Unlock the device and reopen this screen."
        } catch {
            pubkeyHex = ""
            placeholderMessage = "Identity key load failed: \(error.localizedDescription)"
        }
    }
}
