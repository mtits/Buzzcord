import AppKit

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    // Menu Bar Only: no Dock icon from the start.
    let menuBarOnly = Prefs.bool("MenuBarOnly", false) || CommandLine.arguments.contains("--menubar-only")
    app.setActivationPolicy(menuBarOnly ? .accessory : .regular)
    withExtendedLifetime(delegate) { app.run() }
}
