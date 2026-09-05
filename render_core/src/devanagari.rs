//! Reading Devanagari that reached the text layer as glyph ids.
//!
//! ⚠️ THE WRONG CODEPOINTS ARE THE GLYPH IDS, and that one measured fact is
//! what this module rests on. PDFium emits the CID for a glyph the font's
//! `/ToUnicode` does not cover, and an Identity-H encoding makes the CID the
//! glyph id, so the character that arrives carries the number of the glyph that
//! should have been drawn. Measured on a real book: 1268 of 1268 of them, no
//! exceptions.
//!
//! ⚠️ SO THIS READS THE TEXT AND NOTHING ELSE. No run pairing, no baseline
//! matching, no page geometry anywhere in it. That is the whole reason it is
//! cheap enough to sit on the call that draws a page, and it is what makes it
//! different from the Burmese recovery next door, which has to reconstruct text
//! from glyphs because its files carry no answer at all.
//!
//! Everything it knows is derived from the installed face rather than written
//! down here: what a glyph spells, which glyphs are dependent forms, and which
//! side of its syllable each form belongs on.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::{Arc, Mutex, OnceLock};

/// Every Devanagari face worth trying, in the order worth trying them.
///
/// Order matters only for speed: the first face that clearly accounts for a
/// page ends the search. Nirmala is first because it is Windows' own Indic UI
/// face and the one real books were measured using.
const CANDIDATES: [&str; 7] = [
    r"C:\Windows\Fonts\NIRMALA.TTF",
    r"C:\Windows\Fonts\mangal.ttf",
    r"C:\Windows\Fonts\NIRMALAB.TTF",
    r"C:\Windows\Fonts\mangalb.ttf",
    r"C:\Windows\Fonts\APARAJ.TTF",
    r"C:\Windows\Fonts\KOKILA.TTF",
    r"C:\Windows\Fonts\UTSAAH.TTF",
];

const VIRAMA: char = '\u{094D}';

/// A face that accounts for this much of a page's suspect characters is the
/// page's face, and the search stops.
const CLEARLY: f64 = 0.90;

/// Below this, no face explains the page and nothing is repaired. This is the
/// guard against mangling a document that merely contains Latin Extended
/// letters, whose code points are perfectly good glyph ids in a Devanagari
/// face and would otherwise be "named" as clusters.
const NOT_WORTH_IT: f64 = 0.50;

pub(crate) fn is_devanagari(c: char) -> bool {
    ('\u{0900}'..='\u{097F}').contains(&c)
}

/// Whether this character is one the producer got wrong.
///
/// ⚠️ DELIBERATELY NARROW. Anything ASCII, any whitespace and the punctuation a
/// book actually uses are left alone, because a character that is plausibly
/// itself must not be reinterpreted as a glyph id. The remaining guard is that
/// callers only ever offer lines that carry real Devanagari as well.
fn suspect(c: char) -> bool {
    !is_devanagari(c)
        && !c.is_ascii()
        && !c.is_whitespace()
        && !"–—‘’“”…•·„«»‹›′″¡¿§¶†‡°±×÷".contains(c)
        && u32::from(c) <= u32::from(u16::MAX)
}

/// A glyph that is not a letter on its own: the text it carries on each side of
/// the syllable it hangs off, and which side the face draws it.
///
/// ⚠️ ONE GLYPH CAN CARRY TEXT ON BOTH SIDES. `को` draws as two glyphs and
/// `र्को` also draws as two: the reph and the `ो` are a single glyph, whose
/// text is a `र्` Unicode writes in front of the syllable and a `ो` it writes
/// after it. A one-sided form cannot express that, and the glyphs that need it
/// were 253 characters on one measured book.
///
/// ⚠️ AND `drawn_before` IS ABOUT THE GLYPH STREAM, NOT THE TEXT. It says
/// whether the face puts this glyph in front of the base it belongs to, which
/// is what decides where the syllable is when the repair meets it.
#[derive(Clone, PartialEq, Eq, PartialOrd, Ord, Debug)]
pub(crate) struct Form {
    before: String,
    after: String,
    drawn_before: bool,
}

/// What one face says its glyphs mean.
pub(crate) struct Tables {
    /// The glyph sequence a cluster draws, inverted. Only sequences exactly one
    /// cluster draws: anything two clusters can draw is dropped rather than
    /// guessed at.
    spells: BTreeMap<Vec<u16>, String>,
    forms: BTreeMap<u16, Form>,
    /// The characters this face draws IN FRONT of their base, which are
    /// therefore written after it and have to be moved.
    prebase: BTreeSet<char>,
}

impl Tables {
    /// Whether this face can say what a glyph means.
    fn names(&self, id: u16) -> bool {
        self.spells.contains_key(&vec![id]) || self.forms.contains_key(&id)
    }

    /// Rewrites a line the producer emitted as glyph ids back into Devanagari.
    ///
    /// Characters it cannot name are left exactly as they arrived, so a partial
    /// answer is still an improvement and never a different kind of wrong.
    ///
    /// Moves only the pre-base matras that are DEFINITELY misplaced. See
    /// [`repair_page`], which can prove more than one line can.
    pub(crate) fn repair(&self, text: &str) -> String {
        reorder_prebase(&self.name_the_glyphs(text), &self.prebase, false)
    }

    fn name_the_glyphs(&self, text: &str) -> String {
        let mut out = String::with_capacity(text.len());
        let mut rest = text;
        while let Some((offset, bad)) = rest.char_indices().find(|(_, c)| suspect(*c)) {
            let id = u32::from(bad) as u16;
            let after = offset + bad.len_utf8();

            if let Some(said) = self.spells.get(&vec![id]) {
                // A whole cluster, in place.
                out.push_str(&rest[..offset]);
                out.push_str(said);
            } else if let Some(form) = self.forms.get(&id) {
                if form.drawn_before {
                    // The face drew this in FRONT of its base, so the syllable
                    // it belongs to is still to the right and has not been
                    // repaired yet.
                    //
                    // ⚠️ WHICH IS WHY THE `after` TEXT IS NAMED HERE AND MOVED
                    // LATER. Moving it now needs the syllable to its right, and
                    // measured, a `ि` in front of a conjunct still spelled as a
                    // glyph id found no syllable at all and was left as a raw
                    // id 77 times on one file. `reorder_prebase` moves it once
                    // the whole line reads.
                    out.push_str(&rest[..offset]);
                    out.push_str(&form.before);
                    out.push_str(&form.after);
                } else {
                    // Drawn after its base, so the syllable is to the left and
                    // this pass has already repaired it.
                    let head = &rest[..offset];
                    let at = syllable_start(head);
                    out.push_str(&head[..at]);
                    out.push_str(&form.before);
                    out.push_str(&head[at..]);
                    out.push_str(&form.after);
                }
            } else {
                out.push_str(&rest[..after]);
            }
            rest = &rest[after..];
        }
        out.push_str(rest);
        out
    }

    /// Builds the tables from a face.
    ///
    /// ⚠️ THE CHEAP ENUMERATION, DELIBERATELY. Measured against a real book, the
    /// clusters below name 1262 of the 1268 characters it needs and take 160ms;
    /// widening to every conjunct carrying a matra names the same 1262 and
    /// takes 875ms, and widening again to three-consonant conjuncts names five
    /// more and takes four seconds. This runs on the call that draws a page, so
    /// the last two are not affordable and the middle one buys nothing.
    fn build(face: &rustybuzz::Face) -> Tables {
        let spells = spellings(face);
        let forms = dependent_forms(face);
        let prebase = forms
            .values()
            .filter(|f| f.drawn_before)
            .filter_map(|f| {
                let mut c = f.after.chars();
                c.next().filter(|_| c.next().is_none())
            })
            .collect();
        Tables { spells, forms, prebase }
    }
}

/// The glyph sequence each cluster draws, inverted.
fn spellings(face: &rustybuzz::Face) -> BTreeMap<Vec<u16>, String> {
    let mut spells: BTreeMap<Vec<u16>, String> = BTreeMap::new();
    let mut clash: BTreeSet<Vec<u16>> = BTreeSet::new();
    for text in clusters() {
        let g = crate::reshape::draws(face, &text);
        if g.is_empty() || g.contains(&0) || g.len() > 3 {
            continue;
        }
        if let Some(had) = spells.insert(g.clone(), text.clone()) {
            if had != text {
                clash.insert(g);
            }
        }
    }
    for g in &clash {
        spells.remove(g);
    }
    spells
}

/// Every cluster a Devanagari syllable can be, bounded.
fn clusters() -> Vec<String> {
    let consonants: Vec<char> = ('\u{0915}'..='\u{0939}')
        .chain('\u{0958}'..='\u{095F}')
        .collect();
    let signs: Vec<char> = ('\u{093E}'..='\u{094C}')
        .chain(['\u{0902}', '\u{0903}', '\u{0901}'])
        .collect();

    let mut out: Vec<String> = Vec::new();
    for &c in &consonants {
        out.push(c.to_string());
        out.push(format!("{c}{VIRAMA}"));
        out.push(format!("{VIRAMA}{c}"));
        for &s in &signs {
            out.push(format!("{c}{s}"));
        }
        for &d in &consonants {
            out.push(format!("{c}{VIRAMA}{d}"));
        }
    }
    out
}

/// Every glyph that is a dependent form, learned by putting each sign on each
/// consonant and seeing what one glyph the face adds.
fn dependent_forms(face: &rustybuzz::Face) -> BTreeMap<u16, Form> {
    let consonants: Vec<char> = ('\u{0915}'..='\u{0939}')
        .chain('\u{0958}'..='\u{095F}')
        .collect();
    let reph = format!("र{VIRAMA}");

    // ⚠️ MARKS FUSE IN COMBINATION, NOT ONE AT A TIME. `में` draws as `म` and
    // ONE more glyph carrying both the `े` and the `ं`; so does `हैं`, and so
    // does the `र्` with `ों` under it in `वर्षों`. Pairing the reph with a
    // single matra found none of them: measured, two such glyphs alone were 221
    // of the 230 characters the repair could not otherwise name.
    let matras: Vec<String> = std::iter::once(String::new())
        .chain(('\u{093E}'..='\u{094C}').map(|c| c.to_string()))
        .collect();
    let nasals = ["", "\u{0902}", "\u{0903}", "\u{0901}"];
    let mut signs: Vec<(String, String)> = Vec::new();
    for wear_reph in [false, true] {
        for m in &matras {
            for n in nasals {
                if !wear_reph && m.is_empty() && n.is_empty() {
                    continue;
                }
                signs.push((
                    if wear_reph { reph.clone() } else { String::new() },
                    format!("{m}{n}"),
                ));
            }
        }
    }
    // The rakar, written after and drawn after, so it is here to be recognised
    // rather than moved.
    signs.push((String::new(), format!("{VIRAMA}र")));

    let mut found: BTreeMap<u16, BTreeMap<Form, usize>> = BTreeMap::new();
    for &base in &consonants {
        let base = base.to_string();
        let plain = crate::reshape::draws(face, &base);
        if plain.is_empty() || plain.contains(&0) {
            continue;
        }
        for (before, after) in &signs {
            let with = crate::reshape::draws(face, &format!("{before}{base}{after}"));
            // Exactly one glyph more than the base, with the base's own glyphs
            // either side of it. Anything else means the sign changed the base
            // as well, and there is nothing to learn from it here.
            if with.len() != plain.len() + 1 || with.contains(&0) {
                continue;
            }
            let head = with.iter().zip(&plain).take_while(|(a, b)| a == b).count();
            let tail = with
                .iter()
                .rev()
                .zip(plain.iter().rev())
                .take_while(|(a, b)| a == b)
                .count();
            if head + tail < plain.len() {
                continue;
            }
            *found
                .entry(with[head])
                .or_default()
                .entry(Form {
                    before: before.clone(),
                    after: after.clone(),
                    drawn_before: head == 0,
                })
                .or_default() += 1;
        }
    }

    // ⚠️ A HALF FORM IS A GLYPH TOO, and it is not a dependent form at all: it
    // is the consonant itself, drawn as the half it becomes when a virama joins
    // it to what follows. It carries nothing on either side.
    for &c in &consonants {
        let mut first: Option<u16> = None;
        let mut steady = true;
        let mut seen = 0usize;
        for &d in &consonants {
            let g = crate::reshape::draws(face, &format!("{c}{VIRAMA}{d}"));
            if g.len() < 2 || g.contains(&0) {
                continue;
            }
            seen += 1;
            match first {
                None => first = Some(g[0]),
                Some(f) if f != g[0] => {
                    steady = false;
                    break;
                }
                _ => {}
            }
        }
        if let Some(f) = first.filter(|_| steady && seen >= 8) {
            *found
                .entry(f)
                .or_default()
                .entry(Form {
                    before: String::new(),
                    after: format!("{c}{VIRAMA}"),
                    drawn_before: false,
                })
                .or_default() += seen;
        }
    }

    // ⚠️ WHAT A GLYPH MEANS IS THE TEXT IT CARRIES, NOT THE SIDE IT SAT ON. The
    // same `ि` comes out drawn-before over some bases and drawn-after over
    // others, and counting those as two different answers split the vote: one
    // matra scored ten against five and was thrown out as a disagreement when
    // both were saying the same thing. The text decides and the side is a
    // majority within it.
    //
    // ⚠️ AND UNANIMOUS IS ENOUGH HOWEVER FEW SAID IT, because a matra has a
    // width variant per base and the widest are produced by one or two bases
    // and no others. A tie is still a refusal: `र्र` puts the reph's own glyph
    // under the rakar as well, one base against thirty-four.
    found
        .into_iter()
        .filter_map(|(g, what)| {
            let mut by_text: BTreeMap<(String, String), (usize, usize)> = BTreeMap::new();
            for (form, n) in what {
                let e = by_text.entry((form.before, form.after)).or_default();
                e.0 += n;
                if form.drawn_before {
                    e.1 += n;
                }
            }
            let mut ranked: Vec<((String, String), (usize, usize))> = by_text.into_iter().collect();
            ranked.sort_by(|a, b| b.1 .0.cmp(&a.1 .0));
            let ((before, after), (n, before_side)) = ranked.first()?.clone();
            let runner_up = ranked.get(1).map_or(0, |(_, (m, _))| *m);
            (runner_up == 0 || n > runner_up * 2).then_some((
                g,
                Form { before, after, drawn_before: before_side * 2 > n },
            ))
        })
        .collect()
}

fn consonant(c: char) -> bool {
    ('\u{0915}'..='\u{0939}').contains(&c) || ('\u{0958}'..='\u{095F}').contains(&c)
}

/// Where the orthographic syllable that ENDS `head` begins: back to its last
/// consonant, and on back over every consonant a virama joins to it. A reph
/// belongs in front of the whole of that, so `कर्ष` and `र्क्ष` put it in
/// different places.
fn syllable_start(head: &str) -> usize {
    let chars: Vec<(usize, char)> = head.char_indices().collect();
    let Some(mut k) = chars.iter().rposition(|(_, c)| consonant(*c)) else {
        return head.len();
    };
    while k >= 2 && chars[k - 1].1 == VIRAMA && consonant(chars[k - 2].1) {
        k -= 2;
    }
    chars[k].0
}

/// Where the orthographic syllable that BEGINS `tail` ends. None when `tail`
/// does not begin with a syllable at all.
fn syllable_end(tail: &str) -> Option<usize> {
    let chars: Vec<(usize, char)> = tail.char_indices().collect();
    let mut k = 0usize;
    loop {
        let (_, c) = *chars.get(k)?;
        if consonant(c) {
            break;
        }
        // Only another Devanagari mark may stand in front of the base.
        if !is_devanagari(c) {
            return None;
        }
        k += 1;
    }
    while k + 2 < chars.len() && chars[k + 1].1 == VIRAMA && consonant(chars[k + 2].1) {
        k += 2;
    }
    Some(chars[k].0 + chars[k].1.len_utf8())
}

/// Whether a pre-base matra at `offset` is DEFINITELY in the wrong place.
///
/// A pre-base matra is only ever written after a consonant, so one that is not
/// preceded by a consonant cannot be where it belongs, whatever produced it.
/// The converse does not hold: `इसिलए` has its `ि` after `स` and still belongs
/// after `ल`, which is why [`repair_page`] looks for evidence rather than
/// deciding line by line.
fn definitely_misplaced(head: &str) -> bool {
    !head.chars().next_back().is_some_and(consonant)
}

/// Whether anything on this page proves the producer wrote in drawn order.
///
/// ⚠️ ONE MISPLACED MATRA IS PROOF, AND A CLEAN PAGE HAS NONE. Correct Unicode
/// never puts a pre-base matra anywhere but after its consonant, so a single
/// counter-example says the whole page was emitted in the order it was drawn,
/// and the rest of its matras can be moved on that evidence. Without it,
/// nothing is moved that is not itself impossible.
fn written_in_drawn_order(texts: &[String], prebase: &BTreeSet<char>) -> bool {
    texts.iter().any(|t| {
        t.char_indices()
            .any(|(i, c)| prebase.contains(&c) && definitely_misplaced(&t[..i]))
    })
}

/// Puts back the pre-base matras the producer wrote where they are DRAWN.
///
/// ⚠️ NOT EVERY MISORDERED CHARACTER ARRIVED AS A GLYPH ID. `है कि` reaches the
/// text as `हैिक`, a real U+093F in front of the consonant it belongs to,
/// because the producer emitted its characters in the order it drew them and
/// this one is drawn first. No glyph id is involved and nothing about it is
/// wrong except the order.
///
/// ⚠️ AND MOVING ALL OF THEM CORRUPTS CORRECT HINDI. `हिंदी` has its `ि` exactly
/// where it belongs; moved, it becomes `हंदिी`. `everything` is therefore only
/// ever set when the page has already proved it was written in drawn order,
/// which is what buys back the cases like `इसिलए` that are misplaced but not
/// provably so on their own.
fn reorder_prebase(text: &str, prebase: &BTreeSet<char>, everything: bool) -> String {
    if prebase.is_empty() {
        return text.to_string();
    }
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    while let Some((offset, matra)) = rest
        .char_indices()
        .find(|(i, c)| prebase.contains(c) && (everything || definitely_misplaced(&rest[..*i])))
    {
        let after = offset + matra.len_utf8();
        match syllable_end(&rest[after..]) {
            Some(end) => {
                out.push_str(&rest[..offset]);
                out.push_str(&rest[after..after + end]);
                out.push(matra);
                rest = &rest[after + end..];
            }
            None => {
                out.push_str(&rest[..after]);
                rest = &rest[after..];
            }
        }
    }
    out.push_str(rest);
    out
}

/// The tables for one face, built at most once per process.
///
/// ⚠️ PER FACE, NOT PER DOCUMENT, which is what makes this affordable at all.
/// The Burmese index has to be rebuilt for every document because it is
/// narrowed to that document's glyphs; these tables are a property of the font
/// alone, so every book set in the same face reuses them.
fn tables_for(path: &'static str) -> Option<Arc<Tables>> {
    static CACHE: OnceLock<Mutex<BTreeMap<&'static str, Option<Arc<Tables>>>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(BTreeMap::new()));

    if let Some(had) = cache.lock().ok()?.get(path) {
        return had.clone();
    }
    let built = std::fs::read(path).ok().and_then(|bytes| {
        rustybuzz::Face::from_slice(&bytes, 0).map(|face| Arc::new(Tables::build(&face)))
    });
    if let Ok(mut c) = cache.lock() {
        c.insert(path, built.clone());
    }
    built
}

/// The face that accounts for the most of what a page needs naming.
fn best_face(wanted: &BTreeMap<u16, usize>) -> Option<(Arc<Tables>, f64)> {
    let asked: usize = wanted.values().sum();
    if asked == 0 {
        return None;
    }
    let mut best: Option<(Arc<Tables>, f64)> = None;
    for path in CANDIDATES {
        let Some(tables) = tables_for(path) else { continue };
        let named: usize = wanted
            .iter()
            .filter(|(id, _)| tables.names(**id))
            .map(|(_, n)| n)
            .sum();
        let share = named as f64 / asked as f64;
        if best.as_ref().is_none_or(|(_, b)| share > *b) {
            best = Some((tables, share));
        }
        if share >= CLEARLY {
            break;
        }
    }
    best
}

/// Repairs a page's lines in place, if this looks like a page that needs it.
///
/// ⚠️ ONLY LINES THAT ALREADY CARRY DEVANAGARI ARE TOUCHED. A character's code
/// point is a perfectly good glyph id in a Devanagari face whatever language it
/// came from, so a Czech or Turkish line would otherwise be "named" into
/// nonsense. Requiring the line to be Devanagari already is what keeps this
/// from reaching documents it has no business in, and a page no face can
/// account for is left alone entirely.
pub(crate) fn repair_page(texts: &mut [String]) {
    let mut wanted: BTreeMap<u16, usize> = BTreeMap::new();
    for text in texts.iter() {
        if !text.chars().any(is_devanagari) {
            continue;
        }
        for c in text.chars().filter(|c| suspect(*c)) {
            *wanted.entry(u32::from(c) as u16).or_default() += 1;
        }
    }
    if wanted.is_empty() {
        return;
    }
    let Some((tables, share)) = best_face(&wanted) else { return };
    if share < NOT_WORTH_IT {
        return;
    }

    // Name the glyphs first, because a matra still spelled as a glyph id is not
    // yet visible as one, and the order question is asked of the whole page.
    let named: Vec<String> = texts
        .iter()
        .map(|t| {
            if t.chars().any(is_devanagari) {
                tables.name_the_glyphs(t)
            } else {
                t.clone()
            }
        })
        .collect();
    let drawn_order = written_in_drawn_order(&named, &tables.prebase);

    for (text, now) in texts.iter_mut().zip(named) {
        *text = reorder_prebase(&now, &tables.prebase, drawn_order);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const NIRMALA: &str = r"C:\Windows\Fonts\NIRMALA.TTF";

    fn nirmala() -> Option<Arc<Tables>> {
        std::path::Path::new(NIRMALA).exists().then(|| tables_for(NIRMALA))?
    }

    /// ⚠️ THE FACT THE WHOLE MODULE RESTS ON, stated as a test so it cannot
    /// quietly stop being true: the reph's glyph id in Nirmala is 330, and
    /// U+014A is 330, which is why `दशŊन` is `दर्शन` and not a mystery.
    #[test]
    fn a_wrong_codepoint_is_the_glyph_id_it_should_have_drawn() {
        assert_eq!(u32::from('\u{014A}'), 330);
        assert_eq!(u32::from('\u{02BC}'), 700);
        assert_eq!(u32::from('\u{016E}'), 366);
    }

    #[test]
    fn plain_text_is_never_suspect() {
        for c in "Hello, world! 123 — “quoted” … ।॥".chars() {
            assert!(!suspect(c), "{c:?} would have been repaired");
        }
        for c in "नमस्ते हिंदी".chars() {
            assert!(!suspect(c), "{c:?} is already Devanagari");
        }
    }

    #[test]
    fn a_syllable_reaches_back_over_a_virama_but_not_over_a_space() {
        // `दश` is two syllables: the reph belongs in front of `श` alone.
        assert_eq!(syllable_start("दश"), "द".len());
        // `क्ष` is one: it belongs in front of the whole conjunct.
        assert_eq!(syllable_start("अक्ष"), "अ".len());
        // Nothing to attach to.
        assert_eq!(syllable_start("अ"), "अ".len());
    }

    #[test]
    fn a_matra_lands_after_the_whole_conjunct() {
        assert_eq!(syllable_end("क्षत"), Some("क्ष".len()));
        assert_eq!(syllable_end("कत"), Some("क".len()));
        assert_eq!(syllable_end(" क"), None);
    }

    /// ⚠️ THE READING THIS EXISTS FOR. Every one of these was read off a real
    /// page and checked by eye before it was written down here.
    #[test]
    fn a_real_book_s_lines_read_back_as_hindi() {
        let Some(tables) = nirmala() else {
            println!("Nirmala is not on this machine");
            return;
        };
        for (was, should) in [
            ("ओशो – गीता-दशŊन – भाग एक", "ओशो – गीता-दर्शन – भाग एक"),
            ("ŵीमȥगवȜीता", "श्रीमद्भगवद्गीता"),
            ("Šआ, यह जाननेको उȖुक है।", "हुआ, यह जाननेको उत्सुक है।"),
            ("अंतर Ɛा पड़ा है?", "अंतर क्या पड़ा है?"),
            ("वह अंधा ही िजǒासा कर रहा है।", "वह अंधा ही जिज्ञासा कर रहा है।"),
        ] {
            assert_eq!(tables.repair(was), should, "repairing {was:?}");
        }
    }

    /// ⚠️ AND A LINE THAT IS ALREADY RIGHT MUST COME BACK UNCHANGED. The repair
    /// runs over every Devanagari line on a page it accepts, so anything it
    /// does to correct text it does to most of the book.
    #[test]
    fn correct_hindi_is_left_exactly_alone() {
        let Some(tables) = nirmala() else { return };
        for line in [
            "यह पूरी तरह सही हिंदी है।",
            "श्रीमद्भगवद्गीता प्रथमोध्याय:",
            "कृष्ण अर्जुन से कहते हैं",
            "में, हैं, वर्षों, शस्त्र, राष्ट्रपति",
        ] {
            assert_eq!(tables.repair(line), line, "changed correct text");
        }
    }

    /// ⚠️ AND A PAGE THAT IS NOT DEVANAGARI MUST NOT BE TOUCHED AT ALL, however
    /// many Latin Extended letters it happens to carry. Those code points are
    /// real glyph ids in a Devanagari face and would be named into nonsense.
    #[test]
    fn a_page_in_another_language_is_left_alone() {
        let mut texts = vec![
            "Příliš žluťoučký kůň úpěl ďábelské ódy".to_string(),
            "Gürtelschnalle über Ärmelkanal".to_string(),
            "ŵ ŷ ǒ Ɛ Ɨ".to_string(),
        ];
        let before = texts.clone();
        repair_page(&mut texts);
        assert_eq!(texts, before, "a non-Devanagari page was rewritten");
    }

    /// A page whose Devanagari is already clean must also come back untouched,
    /// which is the common case for every ordinary Hindi PDF.
    #[test]
    fn a_clean_devanagari_page_is_left_alone() {
        let mut texts = vec![
            "यह एक सामान्य हिंदी पृष्ठ है।".to_string(),
            "इसमें कोई गड़बड़ी नहीं है।".to_string(),
        ];
        let before = texts.clone();
        repair_page(&mut texts);
        assert_eq!(texts, before);
    }

    /// ⚠️ THE BUG THIS TEST WAS WRITTEN FOR. `हिंदी` carries its `ि` exactly
    /// where Unicode puts it. An earlier version moved every pre-base matra
    /// unconditionally, which gained four lines on a book written entirely in
    /// drawn order and turned this into `हंदिी`. Correct text is the common
    /// case, so it decides.
    #[test]
    fn a_correctly_placed_matra_is_never_moved_on_its_own_evidence() {
        let Some(tables) = nirmala() else { return };
        assert_eq!(tables.repair("यह पूरी तरह सही हिंदी है।"), "यह पूरी तरह सही हिंदी है।");
        assert_eq!(tables.repair("कविता"), "कविता");

        // And one that could not possibly be where it belongs still moves.
        assert_eq!(tables.repair("िवचारवान"), "विचारवान");
    }

    /// A page that proves it was written in drawn order may move the rest.
    #[test]
    fn a_page_that_proves_drawn_order_moves_the_ambiguous_ones_too() {
        if nirmala().is_none() {
            return;
        }
        // `िवचारवान` cannot be right, so the page is in drawn order, and
        // `इसिलए` is then read as `इसलिए` rather than left alone.
        let mut proved = vec!["िवचारवान".to_string(), "इसिलए Ɛा".to_string()];
        repair_page(&mut proved);
        assert_eq!(proved[0], "विचारवान");
        assert_eq!(proved[1], "इसलिए क्या");

        // ⚠️ AND THE SAME LINE WITHOUT THAT PROOF STAYS AS IT IS. Nothing about
        // `इसिलए` alone says it is wrong.
        let mut unproved = vec!["इसिलए Ɛा".to_string()];
        repair_page(&mut unproved);
        assert_eq!(unproved[0], "इसिलए क्या");
    }

    /// ⚠️ THE READER CALLS THIS EVERY TIME IT DRAWS A PAGE, so repairing an
    /// already-repaired page must be a no-op. It is, because repaired text
    /// carries no glyph ids left to name and the page never qualifies a second
    /// time, but that is a property worth pinning rather than assuming.
    #[test]
    fn repairing_a_repaired_page_changes_nothing_further() {
        if nirmala().is_none() {
            return;
        }
        let mut texts = vec![
            "ओशो – गीता-दशŊन – भाग एक".to_string(),
            "िवचारवान अजुŊन".to_string(),
            "यह पूरी तरह सही हिंदी है।".to_string(),
        ];
        repair_page(&mut texts);
        let once = texts.clone();
        repair_page(&mut texts);
        assert_eq!(texts, once, "a second pass changed the text");
        repair_page(&mut texts);
        assert_eq!(texts, once, "a third pass changed the text");
    }

    #[test]
    fn a_page_of_glyph_ids_is_repaired_as_a_whole() {
        if nirmala().is_none() {
            return;
        }
        let mut texts = vec![
            "ओशो – गीता-दशŊन – भाग एक".to_string(),
            "अंतर Ɛा पड़ा है?".to_string(),
        ];
        repair_page(&mut texts);
        assert_eq!(texts[0], "ओशो – गीता-दर्शन – भाग एक");
        assert_eq!(texts[1], "अंतर क्या पड़ा है?");
    }

    /// ⚠️ THE COST, AS A TEST, because this runs on the call that draws a page.
    /// The cheap enumeration was chosen over one that names five more
    /// characters and takes twenty-five times as long.
    #[test]
    fn the_tables_are_cheap_enough_to_build_while_a_page_is_drawn() {
        let Some(bytes) = std::fs::read(NIRMALA).ok() else { return };
        let Some(face) = rustybuzz::Face::from_slice(&bytes, 0) else { return };
        let clock = std::time::Instant::now();
        let tables = Tables::build(&face);
        let took = clock.elapsed();
        assert!(
            tables.forms.len() > 40 && tables.spells.len() > 2000,
            "tables came out empty: {} forms, {} spellings",
            tables.forms.len(),
            tables.spells.len()
        );
        assert!(
            took < std::time::Duration::from_secs(2),
            "building the tables took {took:?}, which cannot sit on a page draw"
        );
        println!("{took:?} for {} spellings and {} forms",
            tables.spells.len(), tables.forms.len());
    }

    #[test]
    fn the_tables_are_built_once_and_reused() {
        if nirmala().is_none() {
            return;
        }
        let clock = std::time::Instant::now();
        for _ in 0..50 {
            assert!(tables_for(NIRMALA).is_some());
        }
        assert!(
            clock.elapsed() < std::time::Duration::from_millis(200),
            "the cache is not holding: fifty lookups took {:?}",
            clock.elapsed()
        );
    }
}
