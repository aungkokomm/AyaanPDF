using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Tests that hold the code to a wall-clock bound, run on their own once the
/// parallel tests are done.
/// </summary>
/// <remarks>
/// ⚠️ IN PARALLEL THE CLOCK MEASURES THE QUEUE, NOT THE CODE. The tests that
/// drive PDFium share one global lock in the core, so a test that times a page
/// waits behind whichever of them holds it: measured, a page that prepares in
/// milliseconds took 1.7 seconds in a full run and failed its 500 ms bound.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimedAlone
{
    public const string Name = "Timed alone";
}
