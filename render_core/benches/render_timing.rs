//! Rough end-to-end timing for the render path, so perf claims are measured
//! rather than assumed. Run with:
//!     cargo test --bench render_timing -- --nocapture            (debug)
//!     cargo test --release --bench render_timing -- --nocapture  (release)

use std::ffi::CString;
use std::time::Instant;

use render_core::{close_document, free_render_result, open_document, render_low_res, STATUS_OK_PDFIUM};

fn time_render(handle: u64, width: i32, iterations: u32) -> f64 {
    // Warm up (first call binds PDFium and populates any lazy state).
    let warm = render_low_res(handle, 0, width);
    free_render_result(warm);

    let start = Instant::now();
    for i in 0..iterations {
        // Vary the page so the LRU cache can't serve every call and hide the
        // real render cost.
        let result = render_low_res(handle, (i % 20) as i32, width);
        assert_eq!(result.status, STATUS_OK_PDFIUM);
        free_render_result(result);
    }
    start.elapsed().as_secs_f64() * 1000.0 / iterations as f64
}

#[test]
fn measure_render_cost_at_realistic_widths() {
    let path = CString::new("tests/fixtures/sample_20pages.pdf").unwrap();
    let handle = open_document(path.as_ptr());
    assert_ne!(handle, 0);

    let profile = if cfg!(debug_assertions) { "DEBUG" } else { "RELEASE" };
    println!("\n=== render_low_res timing [{profile}] ===");

    for width in [200, 800, 1600, 3000] {
        let ms = time_render(handle, width, 20);
        println!("  width {width:>5}px : {ms:>8.2} ms/render");
    }

    close_document(handle);
}
