//! AGD4 reference-conditioned detector, sharing the exact frozen Aegis
//! Android model weights and causal feature ordering. Its score is evidence,
//! not a replacement for clock alignment, echo cancellation or VAD.
use std::sync::OnceLock;

const BINS: usize = 192;
const FEATURES: usize = BINS + 1;
const BASE: usize = 64;
const EMBEDDING: usize = 48;
const HIDDEN: usize = 64;
const FUSE: usize = 96;
const HISTORY: usize = 61;
const STREAMS: usize = 4;
const MERGED: usize = EMBEDDING * 7 + 18 + 4 + 4 + 3 + 1;
const FFT: usize = 512;
const FRAME: usize = 320;

struct Model {
    means: Vec<f32>,
    scales: Vec<f32>,
    base_w: Vec<f32>,
    base_b: Vec<f32>,
    temporal_w: Vec<f32>,
    temporal_b: Vec<f32>,
    fuse_w: Vec<f32>,
    fuse_b: Vec<f32>,
    gru_iw: Vec<f32>,
    gru_ib: Vec<f32>,
    gru_hw: Vec<f32>,
    gru_hb: Vec<f32>,
    score_w: Vec<f32>,
    score_b: f32,
}

impl Model {
    fn parse(bytes: &[u8]) -> Option<Self> {
        let mut words = bytes.chunks_exact(4);
        if !words.remainder().is_empty() {
            return None;
        }
        let mut word = || {
            words
                .next()
                .map(|w| u32::from_be_bytes(w.try_into().unwrap()))
        };
        for expected in [
            0x41474434, FEATURES, BASE, EMBEDDING, HIDDEN, FUSE, HISTORY, 0, MERGED,
        ] {
            if word()? as usize != expected {
                return None;
            }
        }
        let votes = word()?;
        let window = word()?;
        if !(1..=20).contains(&window) || !(1..=window).contains(&votes) {
            return None;
        }
        // Read the floats once; the model uses Java DataInputStream's big-endian layout.
        let mut get = |n: usize| -> Option<Vec<f32>> {
            (0..n)
                .map(|_| {
                    let value = f32::from_bits(word()?);
                    value.is_finite().then_some(value)
                })
                .collect()
        };
        let threshold = get(2)?;
        if threshold.iter().any(|x| !(0.0..=1.0).contains(x)) {
            return None;
        }
        let model = Self {
            means: get(FEATURES)?,
            scales: get(FEATURES)?,
            base_w: get(BASE * (FEATURES + BINS))?,
            base_b: get(BASE)?,
            temporal_w: get(EMBEDDING * BASE * 3)?,
            temporal_b: get(EMBEDDING)?,
            fuse_w: get(FUSE * MERGED)?,
            fuse_b: get(FUSE)?,
            gru_iw: get(3 * HIDDEN * FUSE)?,
            gru_ib: get(3 * HIDDEN)?,
            gru_hw: get(3 * HIDDEN * HIDDEN)?,
            gru_hb: get(3 * HIDDEN)?,
            score_w: get(HIDDEN)?,
            score_b: get(1)?[0],
        };
        if model.scales.iter().any(|&x| x <= 0.) || word().is_some() {
            None
        } else {
            Some(model)
        }
    }
}

fn model() -> Option<&'static Model> {
    static MODEL: OnceLock<Option<Model>> = OnceLock::new();
    MODEL
        .get_or_init(|| {
            Model::parse(include_bytes!(
                "../gen/android/app/src/main/assets/hands-free-barge-in/doubletalk.bin"
            ))
        })
        .as_ref()
}

struct SpectrumTables {
    hann: [f64; FRAME],
    cos: [f64; FFT / 2],
    sin: [f64; FFT / 2],
}
fn tables() -> &'static SpectrumTables {
    static TABLES: OnceLock<SpectrumTables> = OnceLock::new();
    TABLES.get_or_init(|| SpectrumTables {
        hann: std::array::from_fn(|i| {
            0.5 - 0.5 * (std::f64::consts::TAU * i as f64 / (FRAME - 1) as f64).cos()
        }),
        cos: std::array::from_fn(|i| (-std::f64::consts::TAU * i as f64 / FFT as f64).cos()),
        sin: std::array::from_fn(|i| (-std::f64::consts::TAU * i as f64 / FFT as f64).sin()),
    })
}

fn spectrum(pcm: &[i16; FRAME]) -> [f32; FEATURES] {
    let t = tables();
    let mut re = [0.; FFT];
    let mut im = [0.; FFT];
    let mut energy = 0.;
    for (i, &x) in pcm.iter().enumerate() {
        let x = f64::from(x);
        energy += x * x;
        re[i.reverse_bits() >> (usize::BITS - 9)] = x * t.hann[i];
    }
    let mut width = 2;
    while width <= FFT {
        let half = width / 2;
        let step = FFT / width;
        for base in (0..FFT).step_by(width) {
            for j in 0..half {
                let i = base + j;
                let k = i + half;
                let c = t.cos[j * step];
                let s = t.sin[j * step];
                let r = re[k] * c - im[k] * s;
                let m = re[k] * s + im[k] * c;
                re[k] = re[i] - r;
                im[k] = im[i] - m;
                re[i] += r;
                im[i] += m;
            }
        }
        width *= 2;
    }
    std::array::from_fn(|i| {
        if i == BINS {
            ((energy / FRAME as f64).sqrt() / 32.).ln_1p() as f32
        } else {
            ((re[i + 1] * re[i + 1] + im[i + 1] * im[i + 1]).sqrt() / 256.).ln_1p() as f32
        }
    })
}

fn dense<const N: usize>(input: &[f32], weights: &[f32], bias: &[f32]) -> [f32; N] {
    std::array::from_fn(|i| {
        input
            .iter()
            .zip(&weights[i * input.len()..(i + 1) * input.len()])
            .fold(bias[i], |v, (a, b)| v + a * b)
    })
}
fn sigmoid(x: f32) -> f32 {
    (1. / (1. + f64::from(-x.clamp(-50., 50.)).exp())) as f32
}
fn normalized<const N: usize>(input: &[f32; N]) -> [f32; N] {
    let norm = input.iter().map(|x| x * x).sum::<f32>().sqrt().max(1e-6);
    input.map(|x| x / norm)
}
fn whiten(input: &[f32; FEATURES]) -> [f32; BINS] {
    normalized(&std::array::from_fn(|i| {
        let a = i.saturating_sub(4);
        let b = (i + 4).min(BINS - 1);
        input[i] - input[a..=b].iter().sum::<f32>() / (b - a + 1) as f32
    }))
}
fn dot(a: &[f32], b: &[f32]) -> f32 {
    a.iter().zip(b).map(|(a, b)| a * b).sum()
}
fn summarize(values: &[f32; HISTORY]) -> [f32; 4] {
    let (at, peak) =
        values.iter().enumerate().fold(
            (0, -f32::MAX),
            |(at, peak), (i, &x)| if x > peak { (i, x) } else { (at, peak) },
        );
    [
        peak,
        values[HISTORY - 21..]
            .iter()
            .copied()
            .fold(-f32::MAX, f32::max),
        values.iter().sum::<f32>() / HISTORY as f32,
        (HISTORY - 1 - at) as f32 / (HISTORY - 1) as f32,
    ]
}

pub(super) struct Detector {
    model: &'static Model,
    older: [[f32; BASE]; STREAMS],
    previous: [[f32; BASE]; STREAMS],
    reference: Box<[[f32; EMBEDDING]; HISTORY]>,
    reference_unit: Box<[[f32; EMBEDDING]; HISTORY]>,
    reference_harmonic: Box<[[f32; BINS]; HISTORY]>,
    reference_level: [f32; HISTORY],
    index: usize,
    hidden: [f32; HIDDEN],
    frames: usize,
}

impl Detector {
    pub(super) fn new() -> Option<Self> {
        Some(Self {
            model: model()?,
            older: [[0.; BASE]; STREAMS],
            previous: [[0.; BASE]; STREAMS],
            reference: Box::new([[0.; EMBEDDING]; HISTORY]),
            reference_unit: Box::new([[0.; EMBEDDING]; HISTORY]),
            reference_harmonic: Box::new([[0.; BINS]; HISTORY]),
            reference_level: [0.; HISTORY],
            index: HISTORY - 1,
            hidden: [0.; HIDDEN],
            frames: 0,
        })
    }

    pub(super) fn observe(
        &mut self,
        mic: &[i16; FRAME],
        render: &[i16; FRAME],
        clean: &[i16; FRAME],
        linear: &[i16; FRAME],
    ) -> Option<f32> {
        let spectra = [
            spectrum(mic),
            spectrum(render),
            spectrum(clean),
            spectrum(linear),
        ];
        let mut embeds = [[0.; EMBEDDING]; STREAMS];
        let mut levels = [0.; STREAMS];
        for stream in 0..STREAMS {
            let s = &spectra[stream];
            let mean = s[..BINS].iter().sum::<f32>() / BINS as f32;
            let input: [f32; FEATURES + BINS] = std::array::from_fn(|i| {
                if i < FEATURES {
                    ((s[i] - self.model.means[i]) * self.model.scales[i]).clamp(-10., 10.)
                } else {
                    s[i - FEATURES] - mean
                }
            });
            levels[stream] = input[BINS];
            let base =
                dense::<BASE>(&input, &self.model.base_w, &self.model.base_b).map(|x| x.max(0.));
            let temporal: [f32; BASE * 3] = std::array::from_fn(|i| match i / BASE {
                0 => self.older[stream][i % BASE],
                1 => self.previous[stream][i % BASE],
                _ => base[i % BASE],
            });
            embeds[stream] = dense(&temporal, &self.model.temporal_w, &self.model.temporal_b)
                .map(|x| f64::from(x).tanh() as f32);
            self.older[stream] = self.previous[stream];
            self.previous[stream] = base;
        }
        self.index = (self.index + 1) % HISTORY;
        self.reference[self.index] = embeds[1];
        self.reference_unit[self.index] = normalized(&embeds[1]);
        self.reference_harmonic[self.index] = whiten(&spectra[1]);
        self.reference_level[self.index] = spectra[1][BINS];
        let at = |i| (self.index + 1 + i) % HISTORY;
        let mut context = [[0.; EMBEDDING]; 3];
        let mut stats = [[0.; 6]; 3];
        for (k, stream) in [0, 2, 3].into_iter().enumerate() {
            let q = normalized(&embeds[stream]);
            let similarity = std::array::from_fn(|i| dot(&q, &self.reference_unit[at(i)]));
            let summary = summarize(&similarity);
            let weights = similarity.map(|x| f64::from((x - summary[0]) * 8.).exp() as f32);
            let sum = weights.iter().sum::<f32>();
            stats[k] = [
                summary[0],
                summary[1],
                summary[2],
                similarity[HISTORY - 1],
                summary[3],
                1. / sum,
            ];
            for (i, &w) in weights.iter().enumerate() {
                for j in 0..EMBEDDING {
                    context[k][j] += self.reference[at(i)][j] * w / sum;
                }
            }
        }
        let harmonic = whiten(&spectra[0]);
        let harmonic_stats = summarize(&std::array::from_fn(|i| {
            dot(&harmonic, &self.reference_harmonic[at(i)])
        }));
        let references: [f32; HISTORY] = std::array::from_fn(|i| self.reference_level[at(i)]);
        let reference_stats = [
            references.iter().copied().fold(0., f32::max),
            references[HISTORY - 21..]
                .iter()
                .copied()
                .fold(0., f32::max),
            references.iter().sum::<f32>() / HISTORY as f32,
            1.,
        ];
        let mut merged = [0.; MERGED];
        let mut offset = 0;
        let mut append = |v: &[f32]| {
            merged[offset..offset + v.len()].copy_from_slice(v);
            offset += v.len();
        };
        for stream in [0, 2, 3, 1] {
            append(&embeds[stream]);
        }
        for v in &context {
            append(v);
        }
        for v in &stats {
            append(v);
        }
        append(&harmonic_stats);
        append(&levels);
        append(&reference_stats);
        debug_assert_eq!(offset, MERGED);
        let fused =
            dense::<FUSE>(&merged, &self.model.fuse_w, &self.model.fuse_b).map(|x| x.max(0.));
        let gates_i = dense::<{ HIDDEN * 3 }>(&fused, &self.model.gru_iw, &self.model.gru_ib);
        let gates_h = dense::<{ HIDDEN * 3 }>(&self.hidden, &self.model.gru_hw, &self.model.gru_hb);
        self.hidden = std::array::from_fn(|i| {
            let reset = sigmoid(gates_i[i] + gates_h[i]);
            let update = sigmoid(gates_i[HIDDEN + i] + gates_h[HIDDEN + i]);
            let candidate =
                f64::from(gates_i[2 * HIDDEN + i] + reset * gates_h[2 * HIDDEN + i]).tanh() as f32;
            (1. - update) * candidate + update * self.hidden[i]
        });
        self.frames += 1;
        (self.frames >= 3)
            .then(|| sigmoid((self.model.score_b + dot(&self.hidden, &self.model.score_w)) / 3.))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn embedded_model_and_causal_warmup() {
        let mut d = Detector::new().expect("frozen Android AGD4 model");
        let zero = [0; FRAME];
        assert!(d.observe(&zero, &zero, &zero, &zero).is_none());
        assert!(d.observe(&zero, &zero, &zero, &zero).is_none());
        assert!((0.0..=1.0).contains(&d.observe(&zero, &zero, &zero, &zero).unwrap()));
    }

    // Golden scores from the existing Kotlin classifier, compiled directly
    // with Kotlin 2.3.20, same frozen big-endian AGD4 binary, integer PCM
    // (no recordings, no platform floats in fixture generation).
    #[test]
    fn matches_android_agd4_causal_scores() {
        let expected = [
            0.6522066, 0.56810963, 0.5304817, 0.50807387, 0.4794543, 0.44375488, 0.42867714,
            0.40142334, 0.37559664, 0.3405537, 0.34842882, 0.35261315, 0.32046315, 0.29523203,
            0.32195675, 0.28240055,
        ];
        let mut detector = Detector::new().unwrap();
        for (frame, expected) in expected.into_iter().enumerate() {
            let r: [i16; FRAME] = std::array::from_fn(|i| {
                if frame % 4 == 0 {
                    0
                } else {
                    (((i + frame * FRAME) * 97 + 123) % 16001) as i16 - 8000
                }
            });
            let m: [i16; FRAME] = std::array::from_fn(|i| {
                r[i] / 3 + (((i + frame * FRAME) * 53 + 17) % 4001) as i16 - 2000
            });
            let c = m.map(|x| x / 4);
            let l: [i16; FRAME] = std::array::from_fn(|i| m[i] / 2 - r[i] / 6);
            let score = detector.observe(&m, &r, &c, &l);
            if frame < 2 {
                assert!(score.is_none());
            } else {
                let score = score.unwrap();
                assert!(
                    (score - expected).abs() < 0.0001,
                    "frame {frame}: {score} != {expected}"
                );
            }
        }
    }
}
