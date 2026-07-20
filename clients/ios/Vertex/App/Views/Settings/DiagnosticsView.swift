import SwiftUI

/// Lists MetricKit payloads persisted by `MetricsCollector`. Tap a row to
/// inspect the raw JSON. iOS delivers payloads ~once per 24h on real devices
/// (and never under Xcode debug attach — for that, use Debug → Simulate
/// MetricKit Payloads from the Xcode menu while attached).
struct DiagnosticsView: View {
    @State private var payloads: [URL] = []

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: VxSpace.s8) {
                instructionsSection
                payloadsSection
            }
            .padding(.horizontal, VxSpace.s5)
            .padding(.top, VxSpace.s4)
            .padding(.bottom, VxSpace.s8)
        }
        .vertexInnerCanvas()
        .navigationTitle("Diagnostics")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { reload() }
    }

    private var instructionsSection: some View {
        VxSection(header: "MetricKit", footer: "Apple delivers a metric payload roughly every 24 hours when the device is unplugged from a debugger. Open the app at least once a day so the OS has a chance to deliver. Diagnostic payloads (hangs, CPU exceptions) arrive as they happen.") {
            VxRow {
                Text("Status").foregroundStyle(Color.textSecondary)
                Spacer()
                Text(payloads.isEmpty ? "No payloads yet" : "\(payloads.count) saved")
                    .foregroundStyle(Color.textPrimary)
                    .monospacedDigit()
            }
            VxDivider()
            Button {
                reload()
            } label: {
                HStack(spacing: VxSpace.s2) {
                    Image(systemName: "arrow.clockwise")
                    Text("Refresh")
                }
                .font(.vxBody)
                .foregroundStyle(Color.accentPrimary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, VxSpace.s3)
                .padding(.horizontal, VxSpace.s4)
            }
            .buttonStyle(.plain)
        }
    }

    @ViewBuilder
    private var payloadsSection: some View {
        if !payloads.isEmpty {
            VxSection(header: "Saved Payloads") {
                ForEach(Array(payloads.enumerated()), id: \.element) { idx, url in
                    if idx > 0 { VxDivider() }
                    NavigationLink {
                        PayloadDetailView(url: url)
                    } label: {
                        payloadRow(url: url)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private func payloadRow(url: URL) -> some View {
        let attrs = (try? FileManager.default.attributesOfItem(atPath: url.path)) ?? [:]
        let modified = (attrs[.modificationDate] as? Date) ?? Date.distantPast
        let size = (attrs[.size] as? Int) ?? 0
        let isMetric = url.lastPathComponent.hasPrefix("metric-")

        return HStack(spacing: VxSpace.s3) {
            Image(systemName: isMetric ? "chart.line.uptrend.xyaxis" : "exclamationmark.triangle")
                .foregroundStyle(isMetric ? Color.accentPrimary : Color.stateError)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(modified.formatted(date: .abbreviated, time: .shortened))
                    .foregroundStyle(Color.textPrimary)
                Text(url.lastPathComponent)
                    .font(.vxCaptionMono)
                    .foregroundStyle(Color.textTertiary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer()
            Text(formatBytes(size))
                .font(.vxCaptionMono)
                .foregroundStyle(Color.textTertiary)
                .monospacedDigit()
            Image(systemName: "chevron.right")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Color.textTertiary)
        }
        .padding(.vertical, VxSpace.s3)
        .padding(.horizontal, VxSpace.s4)
        .contentShape(.rect)
    }

    private func reload() {
        payloads = MetricsCollector.listPayloads()
    }

    private func formatBytes(_ bytes: Int) -> String {
        if bytes < 1024 { return "\(bytes) B" }
        let kb = Double(bytes) / 1024
        if kb < 1024 { return String(format: "%.1f KB", kb) }
        return String(format: "%.1f MB", kb / 1024)
    }
}

private struct PayloadDetailView: View {
    let url: URL
    @State private var prettyJSON: String = ""

    var body: some View {
        ScrollView([.vertical, .horizontal]) {
            Text(prettyJSON.isEmpty ? "Loading…" : prettyJSON)
                .font(.vxCaptionMono)
                .foregroundStyle(Color.textPrimary)
                .padding(VxSpace.s4)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .vertexInnerCanvas()
        .navigationTitle(url.lastPathComponent)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                ShareLink(item: url) {
                    Image(systemName: "square.and.arrow.up")
                }
            }
        }
        .task { await load() }
    }

    private func load() async {
        guard let data = try? Data(contentsOf: url) else {
            prettyJSON = "(unable to read file)"
            return
        }
        guard let obj = try? JSONSerialization.jsonObject(with: data),
              let pretty = try? JSONSerialization.data(withJSONObject: obj, options: [.prettyPrinted, .sortedKeys]),
              let str = String(data: pretty, encoding: .utf8) else {
            prettyJSON = String(data: data, encoding: .utf8) ?? "(binary)"
            return
        }
        prettyJSON = str
    }
}
