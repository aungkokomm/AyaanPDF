//! Reading a page's own Burmese back out of the glyphs it draws, by shaping.
//!
//! ⚠️ THIS IS NOT INVERSE SHAPING, AND THE DIFFERENCE IS THE WHOLE POINT.
//! Nothing here decodes a glyph. Every answer is produced by shaping candidate
//! text FORWARDS with the same font and demanding the page's own glyph ids
//! back, identically. A candidate that does not reproduce the page is thrown
//! away, so the failure mode is a refusal and never a wrong reading.
//!
//! ⚠️ THE PDF'S OWN `/ToUnicode` IS NOT THE GROUND TRUTH, THE FONT IS.
//! Measured on a Word-produced Myanmar file: 6 of 59 entries in one of its
//! fonts mapped a real Burmese letter to a bare space. Glyph 260 is U+1019,
//! provable by shaping U+1019 and getting 260, and the file's table called it
//! " ". Reconstruction driven by that table proved 1.2% of the page's glyphs.
//! The same reconstruction driven by the FONT proved 99.93%.
//!
//! ⚠️ AND IT NEEDS THE REAL FONT, NOT THE ONE IN THE FILE. A producer embeds a
//! subset with its layout tables pruned. Shaping one known word with the
//! subset gave 2 `.notdef` and 7 wrong glyphs out of 24; the same word through
//! the installed font matched all 24. A document whose font is not installed
//! is refused rather than guessed at.
//!
//! ⚠️ NOTHING CALLS THIS YET, ON PURPOSE. Reading a page is settled and tested;
//! putting a caret in one is not. Wiring it into the editor needs two things
//! this module deliberately does not decide: where a line ends, which has to
//! come from the page's own text positioning rather than a guess, and where the
//! words are, which on a justified line is drawn as `TJ` gaps and not as space
//! glyphs. Both belong to the caller.
#![allow(dead_code)]

use std::collections::{BTreeSet, HashMap};

/// What a glyph run says, indexed by what the font draws for each syllable.
pub(crate) struct Index {
    /// The glyphs a syllable draws, to the text that drew them.
    says: HashMap<Vec<u16>, String>,
    /// The most glyphs any one syllable draws, which bounds the walk.
    longest: usize,
}

/// Burmese syllables, as text.
///
/// The shape of a syllable is fixed by the script, so this is an ENUMERATION
/// and not a search: an optional kinzi, a base, an optional stacked consonant,
/// the medials in their fixed order, then the vowels and the tone marks in
/// theirs. Anything the font cannot draw is dropped when it is shaped.
fn syllables(only: Option<&BTreeSet<char>>) -> Vec<String> {
    let wanted = |s: &str| -> bool {
        match only {
            None => true,
            Some(set) => s.chars().all(|c| set.contains(&c)),
        }
    };

    let consonants: Vec<char> = (0x1000u32..=0x1021).filter_map(char::from_u32).collect();

    let mut plain: Vec<String> = Vec::new();
    for c in &consonants {
        plain.push(c.to_string());
    }
    for u in [0x1023u32, 0x1024, 0x1025, 0x1026, 0x1027, 0x1029, 0x102A, 0x103F] {
        if let Some(c) = char::from_u32(u) {
            plain.push(c.to_string());
        }
    }
    for u in 0x1040u32..=0x104F {
        if let Some(c) = char::from_u32(u) {
            plain.push(c.to_string());
        }
    }

    // Kinzi rides on the FOLLOWING consonant, so it is part of the base rather
    // than something that can be appended to one.
    let kinzi = "\u{1004}\u{103A}\u{1039}";
    let with_kinzi: Vec<String> = consonants.iter().map(|c| format!("{kinzi}{c}")).collect();

    let stacks: Vec<String> = consonants.iter().map(|c| format!("\u{1039}{c}")).collect();

    const MEDIALS: [&str; 12] = [
        "", "\u{103B}", "\u{103C}", "\u{103D}", "\u{103E}",
        "\u{103B}\u{103D}", "\u{103B}\u{103E}", "\u{103C}\u{103D}",
        "\u{103C}\u{103E}", "\u{103D}\u{103E}",
        "\u{103B}\u{103D}\u{103E}", "\u{103C}\u{103D}\u{103E}",
    ];
    // ⚠️ A STACKED CONSONANT DOES NOT TAKE THE FULL MEDIAL SET. Allowing it to
    // multiplied the enumeration by 12 instead of 5 and cost most of a
    // 7.6-million-entry index that a real page used about 300 of.
    const MEDIALS_ON_A_STACK: [&str; 5] =
        ["", "\u{103B}", "\u{103C}", "\u{103D}", "\u{103E}"];
    const ES: [&str; 2] = ["", "\u{1031}"];
    const VOWELS: [&str; 10] = [
        "", "\u{102B}", "\u{102C}", "\u{102D}", "\u{102E}", "\u{102F}",
        "\u{1030}", "\u{1032}", "\u{102D}\u{102F}", "\u{102E}\u{102F}",
    ];
    const TAILS: [&str; 10] = [
        "", "\u{1036}", "\u{1037}", "\u{103A}", "\u{1038}",
        "\u{1036}\u{1038}", "\u{1037}\u{103A}", "\u{102C}\u{103A}",
        "\u{1036}\u{1037}", "\u{103A}\u{1038}",
    ];

    let mut out: Vec<String> = Vec::new();
    let mut emit = |base: &str, stack: &str, medials: &[&str]| {
        for medial in medials {
            for e in ES {
                for vowel in VOWELS {
                    for tail in TAILS {
                        let s = format!("{base}{stack}{medial}{e}{vowel}{tail}");
                        if wanted(&s) {
                            out.push(s);
                        }
                    }
                }
            }
        }
    };

    for base in &plain {
        emit(base, "", &MEDIALS);
        for stack in &stacks {
            emit(base, stack, &MEDIALS_ON_A_STACK);
        }
    }
    // ⚠️ NO STACK ON TOP OF A KINZI. A kinzi already IS a stacked form, and
    // pairing the two is not Burmese.
    for base in &with_kinzi {
        emit(base, "", &MEDIALS);
    }

    // Whatever else a line of this text can hold.
    for u in 0x20u32..0x7F {
        if let Some(c) = char::from_u32(u) {
            let s = c.to_string();
            if wanted(&s) {
                out.push(s);
            }
        }
    }
    out
}

/// The glyphs `text` draws in `face`.
///
/// ⚠️ THE FACE IS BUILT BY THE CALLER AND REUSED. Rebuilding it per call is
/// affordable once and ruinous a million times.
pub(crate) fn draws(face: &rustybuzz::Face, text: &str) -> Vec<u16> {
    let mut buffer = rustybuzz::UnicodeBuffer::new();
    buffer.push_str(text);
    let shaped = rustybuzz::shape(face, &[], buffer);
    shaped.glyph_infos().iter().map(|i| i.glyph_id as u16).collect()
}

impl Index {
    /// Builds the index by shaping every syllable the script allows.
    ///
    /// `chars` and `glyphs`, when given, keep only the syllables spelled with
    /// those characters or drawn with those glyphs. Both are SOUND to narrow a
    /// document by: a page can only draw the glyphs it contains, so a syllable
    /// needing any other cannot be on it. Narrowing is what makes this
    /// affordable, and the caller can always widen and ask again.
    pub(crate) fn build(
        font: &[u8],
        chars: Option<&BTreeSet<char>>,
        glyphs: Option<&BTreeSet<u16>>,
    ) -> Option<Index> {
        let face = rustybuzz::Face::from_slice(font, 0)?;
        let mut says: HashMap<Vec<u16>, String> = HashMap::new();
        let mut longest = 1usize;

        for text in syllables(chars) {
            let drawn = draws(&face, &text);
            // A syllable the font cannot spell draws `.notdef`, and no page
            // contains one, so it can only add noise.
            if drawn.is_empty() || drawn.contains(&0) {
                continue;
            }
            if let Some(allowed) = glyphs {
                if drawn.iter().any(|g| !allowed.contains(g)) {
                    continue;
                }
            }
            longest = longest.max(drawn.len());
            match says.entry(drawn) {
                std::collections::hash_map::Entry::Vacant(v) => {
                    v.insert(text);
                }
                std::collections::hash_map::Entry::Occupied(mut o) => {
                    // ⚠️ TWO SPELLINGS CAN DRAW IDENTICALLY, and the page
                    // cannot tell us which was typed. The shorter one is
                    // preferred: it is the one without a mark the font ignored.
                    if text.chars().count() < o.get().chars().count() {
                        o.insert(text);
                    }
                }
            }
        }
        if says.is_empty() {
            return None;
        }
        Some(Index { says, longest })
    }

    /// How many syllables the index knows.
    pub(crate) fn len(&self) -> usize {
        self.says.len()
    }

    /// The text that draws `glyphs`, or nothing.
    ///
    /// ⚠️ GREEDY IS NOT ENOUGH. A long match taken early can leave a tail that
    /// no syllable spells, so this walks backwards from the end: a position is
    /// reachable only when some syllable starting there leads to another
    /// reachable position. Longer syllables are preferred where both work,
    /// because a page draws a cluster as a cluster.
    pub(crate) fn read(&self, glyphs: &[u16]) -> Option<String> {
        let n = glyphs.len();
        if n == 0 {
            return None;
        }
        let mut step: Vec<Option<(usize, &str)>> = vec![None; n + 1];
        let mut reaches = vec![false; n + 1];
        reaches[n] = true;

        for at in (0..n).rev() {
            let most = self.longest.min(n - at);
            for len in (1..=most).rev() {
                if !reaches[at + len] {
                    continue;
                }
                if let Some(text) = self.says.get(&glyphs[at..at + len]) {
                    reaches[at] = true;
                    step[at] = Some((len, text.as_str()));
                    break;
                }
            }
        }
        if !reaches[0] {
            return None;
        }

        let mut out = String::new();
        let mut at = 0usize;
        while at < n {
            let (len, text) = step[at]?;
            out.push_str(text);
            at += len;
        }
        Some(out)
    }
}

/// What a run of glyphs says, PROVEN: the reading is shaped again and must
/// come back as the very glyphs it was read from.
///
/// ⚠️ THE ONLY ENTRY POINT WORTH CALLING. `Index::read` proposes; this is what
/// establishes. Without the second shaping there is no difference between this
/// and guessing, and a silent wrong reading of someone's book is the one
/// outcome that must never happen.
pub(crate) fn prove(face: &rustybuzz::Face, index: &Index, glyphs: &[u16]) -> Option<String> {
    let reading = index.read(glyphs)?;
    if draws(face, &reading) == glyphs {
        Some(reading)
    } else {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const MYANMAR_TEXT: &str = r"C:\Windows\Fonts\mmrtext.ttf";
    const PYIDAUNGSU: [&str; 2] = [
        r"%USERPROFILE%\AppData\Local\Microsoft\Windows\Fonts\Pyidaungsu-2.5.3_Regular.ttf",
        r"C:\Windows\Fonts\Pyidaungsu.ttf",
    ];

    /// Real Burmese, with the reordering and conjuncts that make the glyph
    /// order differ from the typing order.
    ///
    /// WARNING: NO STACKED CONSONANT LIVES HERE, and that is about cost, not
    /// coverage. One U+1039 in the corpus admits every base-and-stack pair into
    /// the enumeration, which measured as roughly ten times the shaping work.
    /// Stacking has its own test, scoped to the two words that need it.
    const LINES: [&str; 5] = [
        "အရှေ့မိုးကုပ်စက်ဝိုင်းမှ အရုဏ်ဦး ရောင်နီသည်",
        "ကျွန်တော့်ဧည့်ခန်းလေးထဲကို တဖြည်းဖြည်းချင်း ဝင်ရောက်လာနေသည်။",
        "နေ၏ ရွှေရောင်ခြည်တန်းများသည် မိုးသောက်ယံ၏ အမှောင်ထုကို ဖြိုခွင်းကာ",
        "မျှော်လင့်ခြင်းနှင့် အခွင့်အလမ်း အသစ်များဖြင့် ပြည့်နှက်နေသော",
        "နက်မှောင်သော ညတာသည် သက်ဝင်လှုပ်ရှားသော အရာရာ ငြိမ်သက်ခြင်း၊",
    ];

    fn font(path: &str) -> Option<Vec<u8>> {
        std::path::Path::new(path).exists().then(|| std::fs::read(path).unwrap())
    }

    fn first_present(paths: &[&str]) -> Option<Vec<u8>> {
        paths.iter().find_map(|p| font(p))
    }

    /// The characters and glyphs a body of text uses, which is what a caller
    /// narrows the index by.
    fn scope(face: &rustybuzz::Face, lines: &[&str]) -> (BTreeSet<char>, BTreeSet<u16>) {
        let mut chars = BTreeSet::new();
        let mut glyphs = BTreeSet::new();
        for line in lines {
            chars.extend(line.chars());
            glyphs.extend(draws(face, line));
        }
        (chars, glyphs)
    }

    /// An index over `lines`, and a face to check its answers with.
    ///
    /// The font bytes are leaked so the face can outlive this call. Deliberate,
    /// and bounded: a few hundred kilobytes once per test, in a test binary.
    fn scoped(bytes: &[u8], lines: &[&str]) -> (rustybuzz::Face<'static>, Index) {
        let leaked: &'static [u8] = Box::leak(bytes.to_vec().into_boxed_slice());
        let face = rustybuzz::Face::from_slice(leaked, 0).expect("not a face");
        let (chars, glyphs) = scope(&face, lines);
        let index = Index::build(leaked, Some(&chars), Some(&glyphs)).expect("no index");
        (face, index)
    }

    fn round_trips(bytes: &[u8], label: &str) {
        let (face, index) = scoped(bytes, &LINES);
        for line in LINES {
            let drawn = draws(&face, line);
            let read = prove(&face, &index, &drawn)
                .unwrap_or_else(|| panic!("{label}: could not prove {line:?}"));
            assert_eq!(read, line, "{label}: read a different spelling");
        }
    }

    #[test]
    fn myanmar_text_reads_back_the_burmese_it_drew() {
        let Some(bytes) = font(MYANMAR_TEXT) else { return };
        round_trips(&bytes, "Myanmar Text");
    }

    #[test]
    fn pyidaungsu_reads_back_the_burmese_it_drew() {
        let Some(bytes) = first_present(&PYIDAUNGSU) else { return };
        round_trips(&bytes, "Pyidaungsu");
    }

    /// Stacked consonants, which draw one letter beneath another and so have a
    /// glyph run that looks nothing like the character run.
    #[test]
    fn a_stacked_consonant_reads_back() {
        let Some(bytes) = font(MYANMAR_TEXT) else { return };
        const WORDS: [&str; 2] = ["ကမ္ဘာမြေ", "အမြိုက်သုဒ္ဓါ"];
        let (face, index) = scoped(&bytes, &WORDS);
        for word in WORDS {
            let drawn = draws(&face, word);
            assert_eq!(prove(&face, &index, &drawn).as_deref(), Some(word));
        }
    }

    /// The one case the enumeration is shaped around: text whose glyph order is
    /// not its typing order.
    #[test]
    fn a_reordered_cluster_reads_in_logical_order() {
        let Some(bytes) = font(MYANMAR_TEXT) else { return };
        // "မြန်မာ": the medial ra is DRAWN before the consonant it follows.
        const WORD: &str = "\u{1019}\u{103C}\u{1014}\u{103A}\u{1019}\u{102C}";
        let (face, index) = scoped(&bytes, &[WORD]);
        let drawn = draws(&face, WORD);
        assert_eq!(prove(&face, &index, &drawn).as_deref(), Some(WORD));
    }

    /// WARNING: THE PROPERTY EVERYTHING ELSE RESTS ON. A run the index cannot
    /// account for is REFUSED. It is not approximated and it is not partially
    /// read: showing a reader the wrong word from their own book, confidently,
    /// is the outcome this whole approach exists to make impossible.
    #[test]
    fn a_run_the_font_cannot_account_for_is_refused() {
        let Some(bytes) = font(MYANMAR_TEXT) else { return };
        let (face, index) = scoped(&bytes, &[LINES[0]]);

        // A glyph id far outside anything this font draws.
        assert_eq!(index.read(&[60000]), None);
        assert_eq!(prove(&face, &index, &[60000]), None);

        // And a real run with one impossible glyph spliced into it.
        let mut drawn = draws(&face, LINES[0]);
        assert!(prove(&face, &index, &drawn).is_some(), "the control did not prove");
        drawn.insert(drawn.len() / 2, 60000);
        assert_eq!(prove(&face, &index, &drawn), None,
            "a run holding a glyph nothing spells was read anyway");
    }

    /// WARNING: NARROWING MUST NOT CHANGE THE ANSWER, only the cost. If a
    /// scoped index read something a wider one would not, the scope would be
    /// deciding what a page says.
    #[test]
    fn narrowing_the_index_does_not_change_what_a_run_says() {
        let Some(bytes) = font(MYANMAR_TEXT) else { return };
        let one = [LINES[0]];
        let face = rustybuzz::Face::from_slice(&bytes, 0).unwrap();
        let (chars, glyphs) = scope(&face, &one);

        let by_chars = Index::build(&bytes, Some(&chars), None).unwrap();
        let by_both = Index::build(&bytes, Some(&chars), Some(&glyphs)).unwrap();
        assert!(by_both.len() <= by_chars.len(), "narrowing grew the index");

        let drawn = draws(&face, LINES[0]);
        assert_eq!(prove(&face, &by_chars, &drawn), prove(&face, &by_both, &drawn),
            "the glyph scope changed the reading");
        assert_eq!(prove(&face, &by_both, &drawn).as_deref(), Some(LINES[0]));
    }
}
