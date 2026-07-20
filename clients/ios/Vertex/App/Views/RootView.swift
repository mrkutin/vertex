import SwiftUI

struct RootView: View {
    @Bindable var viewModel: TunnelViewModel

    var body: some View {
        NavigationStack {
            ConnectScreen(viewModel: viewModel)
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .principal) {
                        Text("VERTEX")
                            .font(.brandWordmark)
                            .tracking(1.5)
                            .foregroundStyle(Color.textPrimary)
                            .accessibilityAddTraits(.isHeader)
                    }
                    ToolbarItem(placement: .topBarTrailing) {
                        NavigationLink {
                            SettingsScreen(viewModel: viewModel)
                        } label: {
                            Image(systemName: "gear")
                                .foregroundStyle(Color.accentPrimary)
                        }
                        .accessibilityLabel("Settings")
                    }
                }
                .toolbarBackground(.hidden, for: .navigationBar)
        }
        .fullScreenCover(isPresented: $viewModel.permissionDenied) {
            PermissionDeniedView(viewModel: viewModel)
        }
    }
}
