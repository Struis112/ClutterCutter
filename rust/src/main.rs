// GUI entry point. The actual window/message-loop machinery lives in
// `cluttercutter::gui` so it can be exercised from tests / other binaries.

#![windows_subsystem = "windows"]

fn main() {
    cluttercutter::gui::run();
}
