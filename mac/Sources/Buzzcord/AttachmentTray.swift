import AppKit
import QuickLookThumbnailing
import SwiftUI

// A file waiting in an IM window's tray, to go with the next message.
struct PendingFile: Identifiable, Equatable {
    let id = UUID()
    let url: URL
    let size: Int64
    var name: String { url.lastPathComponent }
}

// The tray above the message box: a thumbnail (pictures and videos) or the file's icon, name,
// size and a remove button for each attached file, a line with the total and this chat's
// limit, and the note when a file wasn't attached or Discord refused it.
struct AttachmentTray: View {
    @ObservedObject var chat: ChatWindowController

    var body: some View {
        if !chat.pendingFiles.isEmpty || chat.attachNote != nil {
            VStack(alignment: .leading, spacing: 4) {
                if !chat.pendingFiles.isEmpty {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 6) {
                            ForEach(chat.pendingFiles) { f in chip(f) }
                        }
                    }
                    summary
                }
                if let note = chat.attachNote {
                    HStack(alignment: .top, spacing: 5) {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                        Text(note).lineLimit(1...4).textSelection(.enabled)
                        Spacer(minLength: 0)
                        Button { chat.attachNote = nil } label: { Image(systemName: "xmark") }
                            .buttonStyle(.borderless).help("Dismiss")
                    }
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.overLimit)
                }
            }
        }
    }

    private func chip(_ f: PendingFile) -> some View {
        HStack(spacing: 6) {
            FileThumbnail(url: f.url).frame(width: 34, height: 34)
            VStack(alignment: .leading, spacing: 1) {
                Text(f.name).font(.system(size: 11)).lineLimit(1).truncationMode(.middle)
                Text(Uploads.size(f.size)).font(.system(size: 10))
                    .foregroundStyle(f.size > chat.uploadContext.limit ? Theme.overLimit : Theme.sub)
            }
            .frame(maxWidth: 130, alignment: .leading)
            Button { chat.removeFile(f.id) } label: {
                Image(systemName: "xmark.circle.fill").foregroundStyle(Theme.sub)
            }
            .buttonStyle(.borderless).disabled(chat.sending).help("Remove " + f.name)
        }
        .padding(4)
        .background(Theme.window)
        .overlay(RoundedRectangle(cornerRadius: 4).stroke(Theme.accentLight))
        .help(f.name + ", " + Uploads.size(f.size))
    }

    @ViewBuilder private var summary: some View {
        let files = chat.pendingFiles
        let ctx = chat.uploadContext
        let total = files.reduce(Int64(0)) { $0 + $1.size }
        let warn = Uploads.trayWarning(files.map { .init(name: $0.name, size: $0.size) }, ctx)
        if let p = chat.uploadProgress {
            HStack(spacing: 6) {
                ProgressView(value: p).progressViewStyle(.linear).frame(maxWidth: 160)
                Text("Uploading... \(Int(p * 100))%").font(.system(size: 11)).foregroundStyle(Theme.sub)
            }
        } else {
            HStack(spacing: 0) {
                Text("\(files.count) \(files.count == 1 ? "file" : "files"), \(Uploads.size(total)). ")
                    + Text("Up to \(Uploads.size(ctx.limit)) each here" + (files.count >= UploadLimits.maxFiles ? ", \(UploadLimits.maxFiles) files at most. " : ". "))
                    + Text("Return sends, Esc removes all.")
                Spacer(minLength: 0)
            }
            .font(.system(size: 11)).foregroundStyle(Theme.sub).lineLimit(1)
            if let warn {
                Text(warn.text).font(.system(size: 11)).foregroundStyle(warn.over ? Theme.overLimit : Color.orange)
            }
        }
    }
}

// Quick Look's thumbnail of a file: the picture or a video frame, or the file's icon.
struct FileThumbnail: View {
    let url: URL
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
            } else {
                Image(nsImage: NSWorkspace.shared.icon(forFile: url.path)).resizable().aspectRatio(contentMode: .fit)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 3))
        .task(id: url) {
            let scale = NSScreen.main?.backingScaleFactor ?? 2
            let req = QLThumbnailGenerator.Request(fileAt: url, size: CGSize(width: 34, height: 34), scale: scale, representationTypes: .thumbnail)
            if let rep = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: req) {
                image = rep.nsImage
            }
        }
    }
}
