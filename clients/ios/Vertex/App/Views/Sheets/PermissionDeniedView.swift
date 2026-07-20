@preconcurrency import NetworkExtension
import SwiftUI

struct PermissionDeniedView: View {
    @Bindable var viewModel: TunnelViewModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: VxSpace.s7) {
            Spacer()

            VxLockedHero()

            VStack(spacing: VxSpace.s3) {
                Text("Permission required")
                    .font(.system(size: 24, weight: .semibold, design: .rounded))
                    .foregroundStyle(Color.textPrimary)
                    .multilineTextAlignment(.center)
                Text("Vertex needs permission to add a network configuration on this device. Tap Try again and approve the system prompt.")
                    .font(.vxBody)
                    .foregroundStyle(Color.textSecondary)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, VxSpace.s6)
            }

            Spacer()

            VStack(spacing: VxSpace.s3) {
                Button {
                    viewModel.permissionDenied = false
                    Task { await viewModel.connect() }
                } label: {
                    Text("Try again")
                        .font(.system(size: 18, weight: .semibold, design: .rounded))
                        .foregroundStyle(Color.textOnAccent)
                        .frame(maxWidth: .infinity, minHeight: 56)
                        .background(Color.accentPrimary, in: .capsule)
                }
                .buttonStyle(.plain)

                Button {
                    if let url = URL(string: UIApplication.openSettingsURLString) {
                        UIApplication.shared.open(url)
                    }
                } label: {
                    Text("Open Settings")
                        .font(.system(size: 17, weight: .medium, design: .rounded))
                        .foregroundStyle(Color.textPrimary)
                        .frame(maxWidth: .infinity, minHeight: 52)
                        .overlay(
                            Capsule().strokeBorder(Color.borderStrong, lineWidth: 1)
                        )
                }
                .buttonStyle(.plain)

                Button {
                    viewModel.permissionDenied = false
                    dismiss()
                } label: {
                    Text("Cancel")
                        .font(.vxBody)
                        .foregroundStyle(Color.textSecondary)
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.plain)
                .padding(.top, VxSpace.s1)
            }
            .padding(.horizontal, VxSpace.s6)
            .padding(.bottom, VxSpace.s6)
        }
        .vertexCanvas()
    }
}

/// V-asterisk hero in dormant/amber state with a small lock overlay on the
/// vertex node — see UI-SPEC §8.1 (`VxLockedHero`).
private struct VxLockedHero: View {
    var body: some View {
        ZStack {
            // The hero stays in disconnected mode but recoloured by the parent.
            VertexHero(status: .disconnected, contentSize: 180, haloPadding: 18)

            // Amber overlay: paint a soft glow on top to communicate "warning, locked".
            Circle()
                .fill(Color.stateTransitioningGlow)
                .frame(width: 220, height: 220)
                .blur(radius: 60)
                .blendMode(.plusLighter)
                .opacity(0.4)
                .allowsHitTesting(false)

            // Lock glyph anchored on the vertex node.
            // Vertex is at (110, 154.7) in 220-space; with hero contentSize=180 + haloPadding=18,
            // the bottom of the V sits roughly 56% down from the centre of the rendered frame.
            VStack {
                Spacer()
                Image(systemName: "lock.fill")
                    .font(.system(size: 20, weight: .bold))
                    .foregroundStyle(Color.stateTransitioning)
                    .padding(8)
                    .background(
                        Circle()
                            .fill(Color.bgCanvas)
                            .overlay(Circle().strokeBorder(Color.stateTransitioning, lineWidth: 1))
                    )
                    .padding(.bottom, 28)
                    .accessibilityHidden(true)
            }
        }
        .frame(width: 216, height: 216)
        .accessibilityElement()
        .accessibilityLabel("Permission required")
    }
}
