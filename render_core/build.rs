use std::env;
use std::path::PathBuf;

/// Copies the vendored pdfium.dll into the build output directory so both
/// `cargo test` (binary runs from target/<profile>/deps) and the cdylib
/// consumer (WinUI app, which finds render_core.dll directly in
/// target/<profile>) can locate it next to the executable that loads it.
fn main() {
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let src = manifest_dir.join("vendor/pdfium/pdfium.dll");

    println!("cargo:rerun-if-changed={}", src.display());

    if !src.exists() {
        return;
    }

    // OUT_DIR looks like target/<profile>/build/<pkg>-<hash>/out
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());
    let profile_dir = out_dir
        .ancestors()
        .nth(3)
        .expect("OUT_DIR should be nested three levels under target/<profile>")
        .to_path_buf();

    let _ = std::fs::copy(&src, profile_dir.join("pdfium.dll"));
    let _ = std::fs::copy(&src, profile_dir.join("deps").join("pdfium.dll"));
}
