import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// .ipa/.p12/.mobileprovision file picker (filters by extension).
struct DocumentPicker: UIViewControllerRepresentable {
    var fileExtension: String = "ipa"
    var onPick: (URL) -> Void

    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.data], asCopy: true)
        picker.delegate = context.coordinator
        return picker
    }

    func updateUIViewController(_ uiViewController: UIDocumentPickerViewController, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(fileExtension: fileExtension, onPick: onPick) }

    final class Coordinator: NSObject, UIDocumentPickerDelegate {
        let fileExtension: String
        let onPick: (URL) -> Void
        init(fileExtension: String, onPick: @escaping (URL) -> Void) {
            self.fileExtension = fileExtension
            self.onPick = onPick
        }
        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
            guard let url = urls.first,
                  url.pathExtension.lowercased() == fileExtension else { return }
            onPick(url)
        }
    }
}
