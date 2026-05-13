fn main() {
    // Compiles app.rc which embeds the ClutterCutter.ico icon and basic
    // version info into the GUI binary. Linker is invoked transparently.
    embed_resource::compile("app.rc", embed_resource::NONE);
}
