//! PCM16 mono at 16 kHz. Election quality compares the last half-second with
//! the preceding second, using the same windows as the Android node.
use sonora_agc2::vad_wrapper::VoiceActivityDetectorWrapper;
use std::{
    collections::VecDeque,
    time::{Duration, Instant},
};

pub(crate) const RATE: usize = 16_000;
pub(crate) const BLOCK: usize = 320;
const BLOCKS_PER_SECOND: usize = RATE / BLOCK;

fn power(samples: &[i16]) -> f64 {
    samples.iter().map(|&s| f64::from(s).powi(2)).sum::<f64>() / samples.len().max(1) as f64
}

// Match the manual recording composer: a linear RMS level multiplied by 8,
// one fixed history position every 60 ms. The UI interpolates the heights;
// the capture, endpoint and audio stream never depend on this presentation.
const WAVEFORM_BARS: usize = 64;
const WAVEFORM_WINDOW: Duration = Duration::from_millis(20);
const WAVEFORM_STEP: Duration = Duration::from_millis(60);
const WAVEFORM_DELAY: Duration = Duration::from_millis(40);
const WAVEFORM_JITTER_TOLERANCE: Duration = Duration::from_millis(40);
const WAVEFORM_GAP: Duration = Duration::from_millis(400);
const WAVEFORM_CAPACITY: usize = 208; // 64 × 60ms plus room for presentation delay and jitter.

fn waveform_level(mean_square: f64) -> f64 {
    (mean_square.sqrt() / 32768.0 * 8.0).min(1.0)
}

/// Retain a timestamped RMS history, not PCM. Combining two consecutive
/// 20ms blocks approximates the manual microphone's ~43ms analyser window.
/// The display receives measured levels at 60ms intervals and animates the
/// fixed positions over that same interval, like the existing text composer.
#[derive(Default)]
pub(crate) struct Waveform {
    points: VecDeque<(Instant, f64)>,
    previous_power: Option<f64>,
    last_frame: Option<Instant>,
    last_emitted: Option<Instant>,
}

impl Waveform {
    pub fn push(
        &mut self,
        samples: &[i16],
        visible: bool,
        now: Instant,
        queued_blocks: usize,
    ) -> Option<[f32; WAVEFORM_BARS]> {
        if !visible {
            if self.last_frame.is_some() {
                *self = Self::default();
            }
            return None;
        }
        if self
            .last_frame
            .is_some_and(|previous| now.saturating_duration_since(previous) >= WAVEFORM_GAP)
        {
            *self = Self::default();
        }
        self.last_frame = Some(now);
        debug_assert_eq!(samples.len(), BLOCK);
        let age = WAVEFORM_WINDOW * queued_blocks.min(125) as u32;
        let estimate = now.checked_sub(age).unwrap_or(now);
        let captured_at = if let Some(&(previous, _)) = self.points.back() {
            let next = previous + WAVEFORM_WINDOW;
            if estimate.saturating_duration_since(next) > WAVEFORM_JITTER_TOLERANCE {
                self.previous_power = None;
                estimate
            } else {
                next
            }
        } else {
            estimate
        };
        let current = power(samples);
        let level = waveform_level(
            self.previous_power
                .map_or(current, |previous| (previous + current) * 0.5),
        );
        self.previous_power = Some(current);
        if self.points.len() == WAVEFORM_CAPACITY {
            self.points.pop_front();
        }
        self.points.push_back((captured_at, level));
        if age > WAVEFORM_DELAY {
            return None;
        }
        // Synchronize emission with the measured sample clock rather than the
        // driver callback cadence. Jitter never shifts the history extra slots.
        let head = self.points.back()?.0;
        let presentation = match self.last_emitted {
            Some(previous) => {
                let count = head.saturating_duration_since(previous).as_millis()
                    / WAVEFORM_STEP.as_millis();
                if count == 0 {
                    return None;
                }
                previous + WAVEFORM_STEP * count.min(u32::MAX as u128) as u32
            }
            None => head,
        };
        self.last_emitted = Some(presentation);
        Some(self.history(presentation))
    }

    fn history(&self, now: Instant) -> [f32; WAVEFORM_BARS] {
        std::array::from_fn(|index| {
            now.checked_sub(WAVEFORM_DELAY + WAVEFORM_STEP * (WAVEFORM_BARS - 1 - index) as u32)
                .map_or(0., |at| self.sample(at) as f32)
        })
    }

    fn sample(&self, at: Instant) -> f64 {
        let mut previous = None;
        for &(time, level) in &self.points {
            if at == time {
                return level;
            }
            if at < time {
                return previous
                    .filter(|(left, _)| {
                        time.duration_since(*left) <= WAVEFORM_WINDOW + WAVEFORM_JITTER_TOLERANCE
                    })
                    .map_or(0., |(left, value)| {
                        let fraction = at.duration_since(left).as_secs_f64()
                            / time.duration_since(left).as_secs_f64();
                        value + (level - value) * fraction
                    });
            }
            previous = Some((time, level));
        }
        // Do not synthesize samples across an acquisition gap.
        0.
    }
}

// The Windows speech backend plays PCM16 mono at 24 kHz. One 1024-sample
// Hann-windowed FFT represents 42.67 ms of the actual playback timeline.
pub(crate) const SPEECH_RATE: u32 = 24_000;
const SPECTRUM_SIZE: usize = 1024;
pub(crate) const SPECTRUM_BANDS: usize = 32;
type SpectrumLevels = [f32; SPECTRUM_BANDS];

fn spectrum_edges() -> [usize; SPECTRUM_BANDS + 1] {
    let bin_hz = f64::from(SPEECH_RATE) / SPECTRUM_SIZE as f64;
    let mut edges = [0; SPECTRUM_BANDS + 1];
    edges[0] = (80.0 / bin_hz).ceil() as usize;
    for index in 1..SPECTRUM_BANDS {
        let edge = (80.0 * (12_000.0_f64 / 80.0).powf(index as f64 / SPECTRUM_BANDS as f64)
            / bin_hz)
            .round() as usize;
        edges[index] = edge.clamp(
            edges[index - 1] + 1,
            SPECTRUM_SIZE / 2 - (SPECTRUM_BANDS - index),
        );
    }
    edges[SPECTRUM_BANDS] = SPECTRUM_SIZE / 2;
    edges
}

fn spectrum(samples: &[i16]) -> SpectrumLevels {
    debug_assert_eq!(samples.len(), SPECTRUM_SIZE);
    let mut real = [0.0_f64; SPECTRUM_SIZE];
    let mut imaginary = [0.0_f64; SPECTRUM_SIZE];
    for (index, sample) in samples.iter().enumerate() {
        let phase = std::f64::consts::TAU * index as f64 / SPECTRUM_SIZE as f64;
        let window = 0.5 * (1.0 - phase.cos());
        let reversed = index.reverse_bits() >> (usize::BITS - SPECTRUM_SIZE.trailing_zeros());
        real[reversed] = f64::from(*sample) * window / 32768.0;
    }
    // Iterative radix-2 FFT, with a precomputed rotation for each stage.
    let mut width = 2;
    while width <= SPECTRUM_SIZE {
        let (sine, cosine) = (-std::f64::consts::TAU / width as f64).sin_cos();
        for offset in (0..SPECTRUM_SIZE).step_by(width) {
            let (mut wr, mut wi) = (1.0, 0.0);
            for half in offset..offset + width / 2 {
                let other = half + width / 2;
                let tr = wr * real[other] - wi * imaginary[other];
                let ti = wr * imaginary[other] + wi * real[other];
                real[other] = real[half] - tr;
                imaginary[other] = imaginary[half] - ti;
                real[half] += tr;
                imaginary[half] += ti;
                (wr, wi) = (wr * cosine - wi * sine, wr * sine + wi * cosine);
            }
        }
        width *= 2;
    }
    let edges = spectrum_edges();
    let mut levels = [0.0; SPECTRUM_BANDS];
    for band in 0..SPECTRUM_BANDS {
        let peak = (edges[band]..edges[band + 1])
            .map(|bin| real[bin].hypot(imaginary[bin]))
            .fold(0.0_f64, f64::max);
        // Compensate for the Hann coherent gain and the one-sided spectrum.
        let amplitude = peak * 4.0 / SPECTRUM_SIZE as f64;
        if amplitude > 0.0012589254 {
            levels[band] = ((20.0 * amplitude.log10() + 58.0) / 46.0).clamp(0.0, 1.0) as f32;
        }
    }
    levels
}

/// The output queue may contain ~400 ms of samples whose device position
/// has not reached the speaker yet. Store only bounded band summaries of
/// submitted windows plus one incomplete FFT window. The display can look
/// up a complete window by the actual device cursor, never by HTTP arrival.
#[derive(Default)]
pub(crate) struct PlaybackSpectrum {
    submitted: u64,
    pending: Vec<i16>,
    frames: VecDeque<(u64, SpectrumLevels)>,
    last_played: Option<u64>,
    head_invalid: bool,
}
impl PlaybackSpectrum {
    pub fn submit(&mut self, mut pcm: &[i16], visible: bool) {
        if !visible || self.head_invalid {
            self.submitted += pcm.len() as u64;
            self.pending.clear();
            self.frames.clear();
            return;
        }
        while !pcm.is_empty() {
            let count = pcm.len().min(SPECTRUM_SIZE - self.pending.len());
            self.pending.extend_from_slice(&pcm[..count]);
            pcm = &pcm[count..];
            self.submitted += count as u64;
            if self.pending.len() == SPECTRUM_SIZE {
                let levels = spectrum(&self.pending);
                self.pending.clear();
                // 24 windows cover slightly over a second, including all
                // queued output and the most recent played windows.
                if self.frames.len() == 24 {
                    self.frames.pop_front();
                }
                self.frames.push_back((self.submitted, levels));
            }
        }
    }
    pub fn at(&mut self, played: u64) -> SpectrumLevels {
        if self.last_played.is_some_and(|previous| played < previous) {
            // A reset position no longer indexes this stream's submitted PCM.
            self.head_invalid = true;
            self.pending.clear();
            self.frames.clear();
        }
        if self.head_invalid {
            return [0.0; SPECTRUM_BANDS];
        }
        self.last_played = Some(played);
        let position = played.min(self.submitted);
        self.frames
            .iter()
            .rev()
            .find(|(end, _)| *end <= position && position - *end <= SPECTRUM_SIZE as u64)
            .map(|(_, levels)| *levels)
            .unwrap_or([0.0; SPECTRUM_BANDS])
    }
}

#[derive(Default)]
pub(crate) struct Background(VecDeque<f64>);
impl Background {
    pub fn push(&mut self, samples: &[i16]) {
        debug_assert_eq!(samples.len(), BLOCK);
        if self.0.len() == 75 {
            self.0.pop_front();
        }
        self.0.push_back(power(samples));
    }
    pub fn snr(&self) -> Option<f64> {
        if self.0.len() < 75 {
            return None;
        }
        let background = self.0.iter().take(50).sum::<f64>() / 50.0;
        let wake = self.0.iter().skip(50).sum::<f64>() / 25.0;
        Some((10.0 * (wake.max(1.0) / background.max(1.0)).log10()).clamp(-20.0, 60.0))
    }
    pub fn noise(&self) -> f64 {
        let mut frames = self.0.iter().take(50).copied().collect::<Vec<_>>();
        frames.sort_by(f64::total_cmp);
        frames.get(frames.len() / 4).copied().unwrap_or(0.0).sqrt()
    }

    /// A sustained backdrop differs from a quiet room with a short spike.
    /// Use only the pre-keyword second so the user's own wake does not set
    /// the foreground threshold. This is an endpoint cue, not voice ID.
    pub fn sustained_speech_floor(&self) -> Option<f64> {
        if self.0.len() < 75 {
            return None;
        }
        let mut history: Vec<_> = self.0.iter().take(50).copied().collect();
        history.sort_by(f64::total_cmp);
        let low = history[12].sqrt();
        let high = history[37].sqrt();
        let peaks = history[44].sqrt();
        (low >= 120.0 && high <= low * 4.0).then_some((high * 1.4).max(peaks * 1.25))
    }
}

/// Keep 320 ms of leading context and use model-based speech evidence with a
/// slowly adapting noise floor, rather than ending whenever RMS dips. A 1.4s
/// hangover permits ordinary pauses but no recording can exceed the old limit.
const ENDPOINT_LEAD_FRAMES: usize = 16;
const ENDPOINT_ONSET_FRAMES: usize = 3; // 60 ms; the cue alone is shorter.
const ENDPOINT_SILENCE_FRAMES: usize = 70; // 1.4 seconds after the last speech.
pub(crate) struct Endpoint {
    vad: VoiceActivityDetectorWrapper,
    noise: f64,
    sustained_speech_floor: Option<f64>,
    wait_frames: usize,
    frames: usize,
    earliest_end: usize,
    voiced_run: usize,
    trailing: usize,
    started: bool,
    lead: VecDeque<i16>,
    audio: Vec<i16>,
}
pub(crate) enum Capture {
    More,
    Empty,
    Complete(Vec<i16>),
}
impl Endpoint {
    pub fn new(noise_rms: f64, wait_seconds: usize) -> Self {
        Self {
            vad: VoiceActivityDetectorWrapper::new(sonora_simd::detect_backend(), RATE as i32),
            noise: noise_rms.clamp(8.0, 400.0),
            sustained_speech_floor: None,
            wait_frames: wait_seconds * BLOCKS_PER_SECOND,
            frames: 0,
            earliest_end: 0,
            voiced_run: 0,
            trailing: 0,
            started: false,
            lead: VecDeque::with_capacity(BLOCK * ENDPOINT_LEAD_FRAMES),
            audio: Vec::new(),
        }
    }
    pub fn after_cue(&mut self, buffered_frames: usize) {
        self.wait_frames += buffered_frames;
        self.earliest_end = buffered_frames + 75;
    }
    pub fn prefer_near_speech(mut self, floor: Option<f64>) -> Self {
        self.sustained_speech_floor = floor;
        self
    }
    // The barge-in classifier has already qualified this onset. Do not require
    // a second voiced run, nor discard the beginning of a very short command.
    pub fn interrupted(noise_rms: f64, prefix: Vec<i16>) -> Self {
        debug_assert!(!prefix.is_empty() && prefix.len() % BLOCK == 0);
        let mut endpoint = Self::new(noise_rms, 2);
        for block in prefix.chunks_exact(BLOCK) {
            let _ = endpoint.voice_probability(block);
        }
        endpoint.frames = prefix.len() / BLOCK;
        endpoint.started = true;
        endpoint.audio = prefix;
        endpoint
    }
    pub fn push(&mut self, samples: &[i16]) -> Capture {
        let probability = self.voice_probability(samples);
        self.push_classified(samples, probability)
    }
    fn voice_probability(&mut self, samples: &[i16]) -> f32 {
        debug_assert_eq!(samples.len(), BLOCK);
        let mut floats = [0.; BLOCK];
        for (value, sample) in floats.iter_mut().zip(samples) {
            *value = f32::from(*sample);
        }
        // WebRTC RNN expects 10 ms frames. Prefer the speech-rich half of the
        // 20 ms block so a short consonant is not averaged away.
        let first = self.vad.analyze(&floats[..BLOCK / 2]);
        let second = self.vad.analyze(&floats[BLOCK / 2..]);
        first.max(second)
    }
    fn push_classified(&mut self, samples: &[i16], probability: f32) -> Capture {
        self.frames += 1;
        // Ignore DC/digital silence even while the RNN has speech hangover.
        let mean = samples.iter().map(|&value| f64::from(value)).sum::<f64>() / BLOCK as f64;
        let rms = (power(samples) - mean * mean).max(0.0).sqrt();
        let standard_floor = (self.noise * 1.25).clamp(12.0, 220.0);
        let confident_floor = (self.noise * 0.75).clamp(8.0, 45.0);
        let floor = if probability >= 0.8 {
            confident_floor
        } else {
            standard_floor
        };
        // The RNN alone may report voice on a rising white-noise floor. A
        // nearly flat broadband block lacks short-lag correlation; don't let
        // it confirm an onset or continually reset the trailing timer. This
        // deliberately narrow veto only classifies timing, never filters PCM.
        // Unvoiced consonants remain in the pre-roll/active command and the
        // 1.4s trailing allowance protects short gaps in voiced evidence.
        let structured = rms >= 8.0
            && (1..=4).any(|lag| {
                let correlation = samples[lag..]
                    .iter()
                    .zip(&samples[..BLOCK - lag])
                    .map(|(&a, &b)| (f64::from(a) - mean) * (f64::from(b) - mean))
                    .sum::<f64>();
                correlation.abs() > 0.16 * rms * rms * (BLOCK - lag) as f64
            });
        let voiced = probability >= (if self.started { 0.45 } else { 0.65 })
            && rms >= floor
            && structured
            && self.sustained_speech_floor.is_none_or(|near| rms >= near);
        // Noise can change after speech began. Learn only from non-speech,
        // never from a voiced word, and keep the floor from disqualifying a
        // quieter utterance solely because pre-wake noise was loud.
        if !voiced && (probability < 0.35 || !structured) {
            self.noise += (rms.clamp(8.0, 1200.0) - self.noise) * 0.04;
        }
        self.voiced_run = if voiced { self.voiced_run + 1 } else { 0 };
        if !self.started {
            if self.lead.len() == BLOCK * ENDPOINT_LEAD_FRAMES {
                self.lead.drain(..BLOCK);
            }
            self.lead.extend(samples);
            if self.voiced_run >= ENDPOINT_ONSET_FRAMES {
                self.started = true;
                if self.sustained_speech_floor.is_some() {
                    let retain = BLOCK * (6 + ENDPOINT_ONSET_FRAMES);
                    self.lead.drain(..self.lead.len().saturating_sub(retain));
                }
                self.audio.extend(self.lead.drain(..));
            } else if self.frames >= self.wait_frames
                // A candidate starting within the wait may confirm after the
                // boundary, but a late pulse cannot extend it indefinitely.
                && !(self.voiced_run > 0 && self.frames + 1 - self.voiced_run <= self.wait_frames)
            {
                return Capture::Empty;
            }
        } else {
            self.audio.extend_from_slice(samples);
            // A single loud background syllable must not reset the whole
            // hangover in the measured sustained-speech case.
            let near_voice =
                voiced && (self.sustained_speech_floor.is_none() || self.voiced_run >= 2);
            self.trailing = if near_voice { 0 } else { self.trailing + 1 };
            let ended = self.trailing >= ENDPOINT_SILENCE_FRAMES
                && self.frames >= self.earliest_end
                && !voiced;
            if ended || self.audio.len() >= RATE * 30 {
                if ended && self.sustained_speech_floor.is_some() {
                    // Keep 240 ms of post-roll for quieter consonants, without
                    // transcribing the room for the rest of the hangover.
                    self.audio.truncate(
                        self.audio
                            .len()
                            .saturating_sub(BLOCK * self.trailing.saturating_sub(12)),
                    );
                }
                return Capture::Complete(std::mem::take(&mut self.audio));
            }
        }
        Capture::More
    }
}

pub(crate) fn wav(samples: &[i16]) -> Vec<u8> {
    let bytes = (samples.len() * 2) as u32;
    let mut out = Vec::with_capacity(44 + bytes as usize);
    out.extend_from_slice(b"RIFF");
    out.extend_from_slice(&(bytes + 36).to_le_bytes());
    out.extend_from_slice(b"WAVEfmt ");
    out.extend_from_slice(&16u32.to_le_bytes());
    out.extend_from_slice(&1u16.to_le_bytes());
    out.extend_from_slice(&1u16.to_le_bytes());
    out.extend_from_slice(&(RATE as u32).to_le_bytes());
    out.extend_from_slice(&(RATE as u32 * 2).to_le_bytes());
    out.extend_from_slice(&2u16.to_le_bytes());
    out.extend_from_slice(&16u16.to_le_bytes());
    out.extend_from_slice(b"data");
    out.extend_from_slice(&bytes.to_le_bytes());
    for sample in samples {
        out.extend_from_slice(&sample.to_le_bytes());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn interrupted_capture_keeps_the_qualified_prefix_without_a_second_vad_gate() {
        let prefix = vec![120i16; BLOCK * 3]; // below the normal endpoint threshold
        let mut endpoint = Endpoint::interrupted(1000.0, prefix.clone());
        for _ in 0..ENDPOINT_SILENCE_FRAMES - 1 {
            assert!(matches!(endpoint.push(&[0; BLOCK]), Capture::More));
        }
        let Capture::Complete(command) = endpoint.push(&[0; BLOCK]) else {
            panic!("qualified short speech must complete");
        };
        assert_eq!(&command[..prefix.len()], prefix.as_slice());
        assert_eq!(
            command.len(),
            prefix.len() + BLOCK * ENDPOINT_SILENCE_FRAMES
        );
    }
    fn tone(bin: usize) -> [i16; SPECTRUM_SIZE] {
        std::array::from_fn(|index| {
            (6_000.0
                * (std::f64::consts::TAU * bin as f64 * index as f64 / SPECTRUM_SIZE as f64).sin())
                as i16
        })
    }

    #[test]
    fn speech_spectrum_tracks_frequency_and_quiet_without_pcm_hallucination() {
        assert_eq!(spectrum(&[0; SPECTRUM_SIZE]), [0.; SPECTRUM_BANDS]);
        let edges = spectrum_edges();
        assert_eq!(edges[0], 4);
        assert_eq!(edges[SPECTRUM_BANDS], SPECTRUM_SIZE / 2);
        for bin in [8, 24, 95, 250, 420] {
            let expected = edges
                .windows(2)
                .position(|edge| (edge[0]..edge[1]).contains(&bin))
                .unwrap();
            let bands = spectrum(&tone(bin));
            let (peak, &level) = bands
                .iter()
                .enumerate()
                .max_by(|a, b| a.1.total_cmp(b.1))
                .unwrap();
            assert_eq!(peak, expected, "bin {bin}");
            assert!(level > 0.8 && level <= 1.);
            assert!(bands.iter().all(|v| (0.0..=1.0).contains(v)));
        }
    }

    #[test]
    fn speech_spectrum_waits_for_the_played_cursor_and_resets_when_hidden() {
        let low = tone(12);
        let high = tone(250);
        let mut playback = PlaybackSpectrum::default();
        playback.submit(&low[..500], true);
        playback.submit(&low[500..], true);
        playback.submit(&high, true);
        assert_eq!(playback.at(0), [0.; SPECTRUM_BANDS]);
        assert_eq!(playback.at(1023), [0.; SPECTRUM_BANDS]);
        assert_eq!(playback.at(1024), spectrum(&low));
        assert_eq!(playback.at(2000), spectrum(&low));
        assert_eq!(playback.at(2048), spectrum(&high));
        playback.submit(&high, false);
        assert_eq!(playback.at(2048), [0.; SPECTRUM_BANDS]);
        playback.submit(&low, true);
        assert_eq!(playback.at(4095), [0.; SPECTRUM_BANDS]);
        assert_eq!(playback.at(4096), spectrum(&low));
        for _ in 0..30 {
            playback.submit(&low, true);
        }
        assert_eq!(playback.frames.len(), 24);
        assert_eq!(playback.at(4096), [0.; SPECTRUM_BANDS]);
    }

    #[test]
    fn speech_spectrum_head_reset_neutralizes_the_rest_of_the_stream() {
        let low = tone(12);
        let high = tone(250);
        let mut playback = PlaybackSpectrum::default();
        playback.submit(&low, true);
        playback.submit(&high, true);
        assert_eq!(playback.at(1024), spectrum(&low));
        assert_eq!(playback.at(2048), spectrum(&high));
        assert_eq!(playback.at(1024), [0.; SPECTRUM_BANDS]);
        playback.submit(&high, true);
        assert_eq!(playback.at(3072), [0.; SPECTRUM_BANDS]);
        let mut fresh = PlaybackSpectrum::default();
        fresh.submit(&high, true);
        assert_eq!(fresh.at(1024), spectrum(&high));
    }

    #[test]
    fn waveform_uses_manual_composer_rms_scale_and_saturation() {
        assert_eq!(waveform_level(0.), 0.);
        assert_eq!(waveform_level(32768.0_f64.powi(2)), 1.);
        assert!((waveform_level(1024.0_f64.powi(2)) - 0.25).abs() < 1e-9);
        assert!(waveform_level(30.0_f64.powi(2)) < 0.01);
    }

    #[test]
    fn waveform_shifts_one_position_per_60ms_with_real_linear_levels() {
        let start = Instant::now();
        let mut meter = Waveform::default();
        let mut emitted = Vec::new();
        for index in 0..280 {
            let value = if (10..=15).contains(&index) { 1024 } else { 0 };
            if let Some(frame) =
                meter.push(&[value; BLOCK], true, start + WAVEFORM_WINDOW * index, 0)
            {
                emitted.push((meter.last_emitted.unwrap(), frame));
            }
        }
        assert_eq!(emitted.len(), 1 + (279 * 20 / 60));
        for pair in emitted.windows(2) {
            assert_eq!(pair[1].0.duration_since(pair[0].0), WAVEFORM_STEP);
            // A fixed position inherits its adjacent slot's previous measurement.
            assert_eq!(pair[1].1[..63], pair[0].1[1..]);
        }
        assert!(emitted.iter().any(|(_, frame)| frame[63] > 0.2));
        assert_eq!(emitted.last().unwrap().1, [0.; WAVEFORM_BARS]);
    }

    #[test]
    fn waveform_uses_all_64_distinct_positions_and_the_original_manual_cadence() {
        let start = Instant::now();
        let mut meter = Waveform::default();
        let mut latest = [0.; WAVEFORM_BARS];
        let mut emitted_at = start;
        for index in 0..(WAVEFORM_CAPACITY + 13) {
            let when = start + WAVEFORM_WINDOW * index as u32;
            let amplitude = (300 + 8 * index) as i16;
            if let Some(frame) = meter.push(&[amplitude; BLOCK], true, when, 0) {
                latest = frame;
                emitted_at = meter.last_emitted.unwrap();
            }
        }
        assert_eq!(WAVEFORM_BARS, 64);
        assert_eq!(
            WAVEFORM_STEP * WAVEFORM_BARS as u32,
            Duration::from_millis(3840)
        );
        assert_eq!(WAVEFORM_DELAY, Duration::from_millis(40));
        assert_eq!(meter.points.len(), WAVEFORM_CAPACITY);
        for (index, level) in latest.iter().enumerate() {
            let when = emitted_at - WAVEFORM_DELAY - WAVEFORM_STEP * (63 - index) as u32;
            assert!((*level - meter.sample(when) as f32).abs() < 1e-6);
        }
        assert!(latest.windows(2).all(|pair| pair[0] < pair[1]));
    }

    #[test]
    fn waveform_queued_pre_roll_matches_real_time_history_without_emission_burst() {
        let start = Instant::now() + Duration::from_secs(3);
        let count = 48;
        let end = start + WAVEFORM_WINDOW * (count - 1) as u32;
        let mut live = Waveform::default();
        let mut queued = Waveform::default();
        let mut emitted = vec![];
        // Two synthetic syllables at different levels with silence between.
        for index in 0..count {
            let block = [match index {
                10..=15 => 1200,
                28..=33 => 6000,
                _ => 0,
            }; BLOCK];
            live.push(&block, true, start + WAVEFORM_WINDOW * index as u32, 0);
            if let Some(frame) = queued.push(&block, true, end, count - index - 1) {
                assert!(
                    count - index - 1 <= 2,
                    "never publish a partially drained pre-roll"
                );
                emitted.push(frame);
            }
        }
        assert_eq!(emitted.len(), 1);
        assert_eq!(queued.points.len(), count);
        assert_eq!(queued.points, live.points);
        assert_eq!(emitted[0], live.history(queued.last_emitted.unwrap()));
        assert!(emitted[0].iter().any(|level| *level > 0.));
    }

    #[test]
    fn waveform_read_jitter_preserves_measurements_and_bounded_update_cadence() {
        let start = Instant::now();
        let jitter = [0, 5, 1, 6, 2, 7, 3, 0];
        let mut meter = Waveform::default();
        let mut emitted = vec![];
        for index in 0..64 {
            let now =
                start + Duration::from_millis(index as u64 * 20 + jitter[index % jitter.len()]);
            if meter
                .push(&[if index % 8 < 3 { 3000 } else { 0 }; BLOCK], true, now, 0)
                .is_some()
            {
                emitted.push(now);
            }
        }
        assert_eq!(meter.points.len(), 64);
        assert!(meter
            .points
            .iter()
            .enumerate()
            .all(|(i, (at, _))| *at == start + WAVEFORM_WINDOW * i as u32));
        assert_eq!(emitted.len(), 1 + (63 * 20 / 60));
        assert!(emitted.windows(2).all(|pair| {
            let span = pair[1].duration_since(pair[0]);
            span >= Duration::from_millis(53) && span <= Duration::from_millis(67)
        }));
    }

    #[test]
    fn waveform_gap_decays_and_hidden_or_stalled_capture_clears_history() {
        let start = Instant::now();
        let mut meter = Waveform::default();
        for index in 0..5 {
            meter.push(&[10000; BLOCK], true, start + WAVEFORM_WINDOW * index, 0);
        }
        let before = meter.sample(start + Duration::from_millis(80));
        meter.push(&[10000; BLOCK], true, start + Duration::from_millis(280), 0);
        let gap = meter.sample(start + Duration::from_millis(180));
        assert!(before > 0.);
        assert_eq!(gap, 0., "a gap is not invented speech");
        assert!(meter
            .push(
                &[10000; BLOCK],
                false,
                start + Duration::from_millis(300),
                0
            )
            .is_none());
        assert!(meter.points.is_empty());
        assert!(meter.last_frame.is_none());
        assert_eq!(
            meter.push(&[0; BLOCK], true, start + Duration::from_millis(320), 0),
            Some([0.; WAVEFORM_BARS])
        );
        meter.push(&[10000; BLOCK], true, start + Duration::from_millis(340), 0);
        assert_eq!(
            meter.push(&[0; BLOCK], true, start + Duration::from_millis(800), 0),
            Some([0.; WAVEFORM_BARS])
        );
        assert_eq!(meter.points.len(), 1);
    }

    #[test]
    fn quality_matches_android_windows_and_has_no_startup_estimate() {
        let mut bg = Background::default();
        for _ in 0..50 {
            bg.push(&[100; BLOCK]);
        }
        assert!(bg.snr().is_none());
        for _ in 0..25 {
            bg.push(&[1000; BLOCK]);
        }
        assert!((bg.snr().unwrap() - 20.0).abs() < 0.001);
        assert_eq!(bg.noise(), 100.);
    }
    #[test]
    fn delayed_short_command_keeps_onset_and_finishes_after_silence() {
        let mut ep = Endpoint::new(30., 6);
        for _ in 0..250 {
            assert!(matches!(ep.push(&[0; BLOCK]), Capture::More));
        }
        let speech = std::array::from_fn::<_, BLOCK, _>(|i| if i % 2 == 0 { 420 } else { -420 });
        for _ in 0..8 {
            assert!(matches!(ep.push_classified(&speech, 0.9), Capture::More));
        }
        for _ in 0..ENDPOINT_SILENCE_FRAMES - 1 {
            assert!(matches!(
                ep.push_classified(&[0; BLOCK], 0.0),
                Capture::More
            ));
        }
        let Capture::Complete(s) = ep.push_classified(&[0; BLOCK], 0.0) else {
            panic!("no endpoint")
        };
        assert_eq!(s.iter().filter(|&&x| x.abs() == 420).count(), BLOCK * 8);
        assert_eq!(&wav(&s)[..4], b"RIFF");
    }
    #[test]
    fn silent_continuation_never_yields_audio() {
        let mut ep = Endpoint::new(0., 2);
        for _ in 0..99 {
            assert!(matches!(ep.push(&[0; BLOCK]), Capture::More));
        }
        assert!(matches!(ep.push(&[0; BLOCK]), Capture::Empty));
    }

    fn alternate(level: i16) -> [i16; BLOCK] {
        std::array::from_fn(|i| if i % 2 == 0 { level } else { -level })
    }

    #[test]
    fn classroom_endpoint_selects_louder_request_over_continuous_voices() {
        let room = |frame: usize| -> [i16; BLOCK] {
            std::array::from_fn(|index| {
                let t = (frame * BLOCK + index) as f64 / RATE as f64;
                let a = (t * 180. * std::f64::consts::TAU).sin() * (280. + 80. * (t * 7.).sin());
                let b = (t * 235. * std::f64::consts::TAU).sin() * (240. + 90. * (t * 11.).sin());
                (a + b) as i16
            })
        };
        let mut ambient = Background::default();
        for frame in 0..75 {
            ambient.push(&room(frame));
        }
        let floor = ambient
            .sustained_speech_floor()
            .expect("persistent speech floor");
        let mut endpoint = Endpoint::new(ambient.noise(), 6).prefer_near_speech(Some(floor));
        // Exercise onset and endpoint with explicit speech confidence for
        // both signals. Speech VAD alone would record the entire room.
        for frame in 75..125 {
            assert!(matches!(
                endpoint.push_classified(&room(frame), 0.95),
                Capture::More
            ));
        }
        assert!(!endpoint.started);
        let spoken: Vec<_> = (125..156)
            .map(|frame| {
                let mut block = room(frame);
                for (sample, loud) in block.iter_mut().zip(alternate(1500)) {
                    *sample += loud;
                }
                assert!(matches!(
                    endpoint.push_classified(&block, 0.95),
                    Capture::More
                ));
                block
            })
            .flatten()
            .collect();
        assert!(endpoint.started);
        for frame in 156..156 + ENDPOINT_SILENCE_FRAMES - 1 {
            assert!(matches!(
                endpoint.push_classified(&room(frame), 0.95),
                Capture::More
            ));
        }
        let Capture::Complete(captured) = endpoint.push_classified(&room(225), 0.95) else {
            panic!("the room kept the owner's request open");
        };
        assert!(captured
            .windows(spoken.len())
            .any(|window| window == spoken));
        assert_eq!(captured.len(), spoken.len() + BLOCK * (6 + 12));
    }

    #[test]
    #[ignore = "requires mixed classroom speech fixtures (AEGIS_CLASSROOM_PCM_DIR)"]
    fn replay_classroom_voices_with_the_real_endpoint_vad() {
        // 16 kHz mono PCM16LE, two background speakers and one separate
        // close-mic command. The backgrounds stay active throughout the test.
        let dir = std::path::PathBuf::from(std::env::var("AEGIS_CLASSROOM_PCM_DIR").unwrap());
        let load = |name| {
            std::fs::read(dir.join(name))
                .unwrap()
                .chunks_exact(2)
                .map(|bytes| i16::from_le_bytes(bytes.try_into().unwrap()))
                .collect::<Vec<_>>()
        };
        let left = load("background-1.pcm");
        let right = load("background-2.pcm");
        let owner = load("command.pcm");
        let owner_blocks: Vec<_> = owner.chunks_exact(BLOCK).collect();
        let first = owner_blocks
            .iter()
            .position(|block| power(block).sqrt() > 250.)
            .unwrap();
        let last = owner_blocks
            .iter()
            .rposition(|block| power(block).sqrt() > 250.)
            .unwrap();
        let owner = &owner_blocks[first..=last];
        let mix = |frame: usize| -> [i16; BLOCK] {
            let samples = frame.checked_sub(125).and_then(|index| owner.get(index));
            std::array::from_fn(|index| {
                let t = frame * BLOCK + index;
                let mixed = f64::from(left[t % left.len()]) * 0.16
                    + f64::from(right[t % right.len()]) * 0.16
                    + samples.map_or(0., |sample| f64::from(sample[index]));
                mixed.clamp(f64::from(i16::MIN), f64::from(i16::MAX)) as i16
            })
        };
        let mut ambient = Background::default();
        for frame in 0..75 {
            ambient.push(&mix(frame));
        }
        let floor = ambient
            .sustained_speech_floor()
            .expect("fixtures have persistent background");
        let mut endpoint = Endpoint::new(ambient.noise(), 6).prefer_near_speech(Some(floor));
        for frame in 75..125 + owner.len() {
            assert!(
                matches!(endpoint.push(&mix(frame)), Capture::More),
                "frame {frame}"
            );
        }
        assert!(endpoint.started);
        let ending = 125 + owner.len();
        for frame in ending..ending + ENDPOINT_SILENCE_FRAMES + 12 {
            if let Capture::Complete(captured) = endpoint.push(&mix(frame)) {
                assert!(captured.len() >= owner.len() * BLOCK);
                return;
            }
        }
        panic!("the background prevented the mixed command from ending");
    }

    #[test]
    fn natural_pauses_and_quieter_following_words_do_not_end_a_command() {
        for pause in [40, 50, 65] {
            // .8s, 1s, 1.3s
            let mut ep = Endpoint::new(400., 6);
            for _ in 0..3 {
                assert!(matches!(
                    ep.push_classified(&alternate(500), 0.96),
                    Capture::More
                ));
            }
            for _ in 0..pause {
                assert!(matches!(ep.push_classified(&[0; BLOCK], 0.), Capture::More));
            }
            // A much quieter word after the pause still resets endpoint timing.
            for _ in 0..3 {
                assert!(matches!(
                    ep.push_classified(&alternate(50), 0.96),
                    Capture::More
                ));
            }
            for _ in 0..ENDPOINT_SILENCE_FRAMES - 1 {
                assert!(matches!(ep.push_classified(&[0; BLOCK], 0.), Capture::More));
            }
            let Capture::Complete(audio) = ep.push_classified(&[0; BLOCK], 0.) else {
                panic!("expected endpoint")
            };
            assert_eq!(audio.iter().filter(|&&x| x.abs() == 500).count(), BLOCK * 3);
            assert_eq!(audio.iter().filter(|&&x| x.abs() == 50).count(), BLOCK * 3);
        }
    }

    #[test]
    fn rising_non_speech_noise_does_not_extend_capture_and_floor_adapts() {
        let mut ep = Endpoint::new(30., 6);
        for _ in 0..3 {
            assert!(matches!(
                ep.push_classified(&alternate(700), 0.95),
                Capture::More
            ));
        }
        for i in 0..ENDPOINT_SILENCE_FRAMES - 1 {
            assert!(matches!(
                ep.push_classified(&alternate((50 + i * 20) as i16), 0.1),
                Capture::More
            ));
        }
        assert!(ep.noise > 100.);
        assert!(matches!(
            ep.push_classified(&alternate(1400), 0.1),
            Capture::Complete(_)
        ));
    }

    #[test]
    fn actual_rnn_rejects_a_rising_broadband_noise_floor() {
        let mut ep = Endpoint::new(30., 2);
        let mut seed = 0x7b31a1b5u32;
        let mut reached_timeout = false;
        let mut max_probability = 0f32;
        for frame in 0..100 {
            let amplitude = 20 + frame * 15;
            let noise: [i16; BLOCK] = std::array::from_fn(|_| {
                seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
                (((seed >> 16) as i32 - 32768) * amplitude / 32768) as i16
            });
            let probability = ep.voice_probability(&noise);
            max_probability = max_probability.max(probability);
            let started = ep.started;
            let capture = ep.push_classified(&noise, probability);
            if !started && ep.started {
                eprintln!(
                    "broadband onset frame={frame} prob={probability} floor={} rms={}",
                    ep.noise,
                    power(&noise).sqrt()
                );
            }
            match capture {
                Capture::More => {}
                Capture::Empty => {
                    reached_timeout = true;
                    break;
                }
                Capture::Complete(_) => panic!("rising stationary noise accepted as command"),
            }
        }
        eprintln!("broadband max probability={max_probability}");
        assert!(
            reached_timeout,
            "rising stationary noise must not prolong no-speech timeout"
        );
    }

    #[test]
    fn quiet_short_onset_near_wait_limit_confirms_without_allowing_isolated_click_or_dc() {
        let mut ep = Endpoint::new(400., 2);
        for _ in 0..99 {
            assert!(matches!(ep.push_classified(&[0; BLOCK], 0.), Capture::More));
        }
        // Start inside the wait, confirm immediately after it, including all PCM.
        for _ in 0..3 {
            assert!(matches!(
                ep.push_classified(&alternate(50), 0.99),
                Capture::More
            ));
        }
        for _ in 0..ENDPOINT_SILENCE_FRAMES - 1 {
            assert!(matches!(ep.push_classified(&[0; BLOCK], 0.), Capture::More));
        }
        let Capture::Complete(audio) = ep.push_classified(&[0; BLOCK], 0.) else {
            panic!("short command lost")
        };
        assert_eq!(audio.iter().filter(|&&x| x.abs() == 50).count(), BLOCK * 3);
        let mut isolated = Endpoint::new(10., 2);
        for _ in 0..99 {
            assert!(matches!(
                isolated.push_classified(&[500; BLOCK], 0.99),
                Capture::More
            ));
        }
        assert!(matches!(
            isolated.push_classified(&alternate(900), 0.99),
            Capture::More
        ));
        assert!(matches!(
            isolated.push_classified(&[0; BLOCK], 0.),
            Capture::Empty
        ));
    }

    #[test]
    fn hard_limit_is_preserved_despite_continuous_speech_evidence() {
        let mut ep = Endpoint::new(10., 6);
        for _ in 0..(RATE * 30 / BLOCK - 1) {
            assert!(matches!(
                ep.push_classified(&alternate(500), 0.99),
                Capture::More
            ));
        }
        let Capture::Complete(audio) = ep.push_classified(&alternate(500), 0.99) else {
            panic!("30s limit not applied")
        };
        assert_eq!(audio.len(), RATE * 30);
    }

    #[test]
    #[ignore = "requires existing synthetic speech fixtures (AEGIS_BARGE_IN_PCM_DIR)"]
    fn replay_endpoint_with_real_rnn_vad_and_natural_pause() {
        let dir = std::path::PathBuf::from(std::env::var("AEGIS_BARGE_IN_PCM_DIR").unwrap());
        let mut total = 0;
        for name in ["near-1.pcm", "near-2.pcm"] {
            let bytes = std::fs::read(dir.join(name)).unwrap();
            let pcm: Vec<i16> = bytes
                .chunks_exact(2)
                .map(|pair| i16::from_le_bytes(pair.try_into().unwrap()))
                .collect();
            let blocks: Vec<&[i16]> = pcm.chunks_exact(BLOCK).collect();
            let first = blocks.iter().position(|b| power(b).sqrt() > 100.).unwrap();
            let last = blocks.iter().rposition(|b| power(b).sqrt() > 100.).unwrap();
            for gain in [0.08, 0.2, 1.0] {
                let speech: Vec<[i16; BLOCK]> = blocks[first..=last]
                    .iter()
                    .map(|b| std::array::from_fn(|i| (b[i] as f64 * gain) as i16))
                    .collect();
                let mut ep = Endpoint::new(30., 6);
                for _ in 0..10 {
                    assert!(matches!(ep.push(&[0; BLOCK]), Capture::More));
                }
                for block in &speech {
                    assert!(
                        matches!(ep.push(block), Capture::More),
                        "{name} gain {gain}: speech cut"
                    );
                }
                assert!(ep.started, "{name} gain {gain}: no onset");
                for _ in 0..50 {
                    assert!(
                        matches!(ep.push(&[0; BLOCK]), Capture::More),
                        "{name} gain {gain}: pause cut"
                    );
                }
                for block in &speech {
                    assert!(
                        matches!(ep.push(block), Capture::More),
                        "{name} gain {gain}: second phrase cut"
                    );
                }
                let mut completed = None;
                for i in 0..100 {
                    if let Capture::Complete(audio) = ep.push(&[0; BLOCK]) {
                        completed = Some((i, audio));
                        break;
                    }
                }
                let (silence, audio) =
                    completed.unwrap_or_else(|| panic!("{name} gain {gain}: did not end"));
                let phrase: Vec<i16> = speech
                    .iter()
                    .flat_map(|block| block.iter().copied())
                    .collect();
                let mut expected = phrase.clone();
                expected.resize(expected.len() + BLOCK * 50, 0);
                expected.extend_from_slice(&phrase);
                assert!(audio.windows(expected.len()).any(|window| window == expected),
                    "{name} gain {gain}: capture did not preserve both complete phrases and the intervening pause");
                println!(
                    "endpoint replay {name} gain={gain} silence_ms={} captured_ms={}",
                    (silence + 1) * 20,
                    audio.len() * 1000 / RATE
                );
                total += 1;
            }
        }
        assert_eq!(total, 6);
    }
}
