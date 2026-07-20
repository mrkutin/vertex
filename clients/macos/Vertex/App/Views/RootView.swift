import SwiftUI

struct RootView: View {
    @Bindable var viewModel: TunnelViewModel
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        NavigationStack {
            ConnectScreen(viewModel: viewModel)
                .toolbar {
                    // VERTEX wordmark intentionally NOT in `.principal` —
                    // macOS 26 Liquid Glass wraps every toolbar item in a
                    // capsule halo and there's no clean opt-out per item.
                    // The wordmark is rendered inside ConnectScreen as the
                    // first content row instead.
                    ToolbarItem(placement: .primaryAction) {
                        Button {
                            openSettings()
                        } label: {
                            Image(systemName: "gear")
                                .foregroundStyle(Color.accentPrimary)
                        }
                        .accessibilityLabel("Settings")
                        .keyboardShortcut(",", modifiers: .command)
                    }
                }
        }
    }
}
