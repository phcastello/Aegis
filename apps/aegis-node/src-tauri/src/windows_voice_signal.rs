//! PCM16 mono at 16 kHz. Election quality compares the last half-second with
//! the preceding second, using the same windows as the Android node.
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

// Same fixed display scale as the Android interaction panel: -58 to -12
// dBFS, 40 ms attack, 150 ms release. Display only; never affects detection,
// the transmitted command, or the microphone's gain.
const WAVEFORM_BARS: usize = 32;
const WAVEFORM_WINDOW: Duration = Duration::from_millis(20);
const WAVEFORM_STEP: Duration = Duration::from_millis(40);
const WAVEFORM_DELAY: Duration = WAVEFORM_STEP;
const WAVEFORM_GAP: Duration = Duration::from_millis(400);
const WAVEFORM_CAPACITY: usize = 80; // 1.6 s: 1.28 s history plus presentation delay.
const WAVEFORM_QUIET: f64 = 0.002;

fn waveform_level(samples: &[i16]) -> f64 {
    let rms = power(samples).sqrt() / 32768.0;
    if rms <= 0.0012589254 {
        0.0
    } else {
        ((20.0 * rms.log10() + 58.0) / 46.0).clamp(0.0, 1.0)
    }
}

/// Capture delivers fixed 20 ms blocks. Retain only normalized envelopes and
/// their estimated acquisition times, even when queued blocks arrive together.
/// Presentation resamples the real history (32 bars spaced 40 ms apart),
/// delayed by 40 ms to absorb normal recording/delivery jitter. No PCM is
/// retained, and no audio or capture decisions depend on the visual timeline.
#[derive(Default)]
pub(crate) struct Waveform {
    points: VecDeque<(Instant, f64)>,
    envelope: f64,
    silence_start: f64,
    silent_frames: u32,
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
            // Reset once when the app is hidden. Avoid even computing RMS
            // while no visualization can be displayed.
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

        // The driver returns complete, ordered 20 ms blocks; the completed
        // buffers still queued behind this block represent its age. Anchor
        // their timeline once and retain sample cadence, not read cadence.
        let age = WAVEFORM_WINDOW * queued_blocks.min(125) as u32;
        let estimate = now.checked_sub(age).unwrap_or(now);
        let captured_at = if let Some(&(previous, _)) = self.points.back() {
            let next = previous + WAVEFORM_WINDOW;
            // Real acquisition/delivery gaps decay rather than extending the
            // previous measurement through missing time; smaller variations
            // in the driver's return time don't distort the sample spacing.
            if estimate.saturating_duration_since(next) > WAVEFORM_STEP {
                let gap_ms = estimate.saturating_duration_since(next).as_secs_f64() * 1000.;
                self.envelope *= (-gap_ms / 150.).exp();
                self.silent_frames = 0;
                estimate
            } else {
                next
            }
        } else {
            estimate
        };
        let level = waveform_level(samples);
        if level == 0.0 {
            if self.silent_frames == 0 {
                self.silence_start = self.envelope;
            }
            self.silent_frames = (self.silent_frames + 1).min(8);
            self.envelope =
                self.silence_start * (1.0 - f64::from(self.silent_frames) * 20.0 / 150.0).max(0.0);
        } else {
            self.silent_frames = 0;
            let time_constant = if level > self.envelope { 40.0 } else { 150.0 };
            self.envelope = level + (self.envelope - level) * (-20.0 / time_constant as f64).exp();
        }
        if self.envelope < WAVEFORM_QUIET {
            self.envelope = 0.0;
        }
        if self.points.len() == WAVEFORM_CAPACITY {
            self.points.pop_front();
        }
        self.points.push_back((captured_at, self.envelope));

        // Do not send a partially drained pre-roll as a current frame. Once
        // the delayed head is covered, publish at most once per 20 ms source
        // window: a half-bar interpolation at the intermediate times moves
        // the wider history smoothly without creating another timer or input.
        if age > WAVEFORM_DELAY {
            return None;
        }
        if self
            .last_emitted
            .is_some_and(|previous| now.saturating_duration_since(previous) < WAVEFORM_WINDOW)
        {
            return None;
        }
        self.last_emitted = Some(now);
        Some(self.history(now))
    }

    fn history(&self, now: Instant) -> [f32; WAVEFORM_BARS] {
        std::array::from_fn(|index| {
            now.checked_sub(WAVEFORM_DELAY + WAVEFORM_STEP * (WAVEFORM_BARS - 1 - index) as u32)
                .map_or(0., |at| {
                    let level = self.sample(at);
                    if level < WAVEFORM_QUIET {
                        0.
                    } else {
                        level as f32
                    }
                })
        })
    }

    fn sample(&self, at: Instant) -> f64 {
        let Some(&(first_time, first_level)) = self.points.front() else {
            return 0.;
        };
        if at < first_time {
            let fraction =
                1. - first_time.duration_since(at).as_secs_f64() / WAVEFORM_WINDOW.as_secs_f64();
            return first_level * fraction.clamp(0., 1.);
        }
        let decay = |level: f64, age: Duration| level * (-age.as_secs_f64() / 0.150).exp();
        let (mut left_time, mut left_level) = (first_time, first_level);
        for &(right_time, right_level) in self.points.iter().skip(1) {
            if at <= right_time {
                let span = right_time.duration_since(left_time);
                if span > WAVEFORM_WINDOW + WAVEFORM_STEP {
                    let start = right_time - WAVEFORM_WINDOW;
                    if at < start {
                        return decay(left_level, at.duration_since(left_time));
                    }
                    left_level = decay(left_level, start.duration_since(left_time));
                    left_time = start;
                }
                let fraction = at.duration_since(left_time).as_secs_f64()
                    / right_time.duration_since(left_time).as_secs_f64();
                return left_level + (right_level - left_level) * fraction.clamp(0., 1.);
            }
            (left_time, left_level) = (right_time, right_level);
        }
        decay(left_level, at.duration_since(left_time))
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
}

/// Keep 240 ms of leading context, allow a natural pause after the cue, and
/// bound both first-speech latency and total audio without sending silence.
pub(crate) struct Endpoint {
    threshold: f64,
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
            threshold: (noise_rms * 2.3).clamp(130.0, 3500.0),
            wait_frames: wait_seconds * BLOCKS_PER_SECOND,
            frames: 0,
            earliest_end: 0,
            voiced_run: 0,
            trailing: 0,
            started: false,
            lead: VecDeque::with_capacity(BLOCK * 12),
            audio: Vec::new(),
        }
    }
    pub fn after_cue(&mut self, buffered_frames: usize) {
        self.wait_frames += buffered_frames;
        self.earliest_end = buffered_frames + 75;
    }
    pub fn push(&mut self, samples: &[i16]) -> Capture {
        self.frames += 1;
        let voiced = power(samples).sqrt() > self.threshold;
        if !self.started {
            self.voiced_run = if voiced { self.voiced_run + 1 } else { 0 };
            if self.lead.len() == BLOCK * 12 {
                self.lead.drain(..BLOCK);
            }
            self.lead.extend(samples);
            if self.voiced_run >= 4 {
                self.started = true;
                self.audio.extend(self.lead.drain(..));
            } else if self.frames >= self.wait_frames {
                return Capture::Empty;
            }
        } else {
            self.audio.extend_from_slice(samples);
            self.trailing = if voiced { 0 } else { self.trailing + 1 };
            if (self.trailing >= 40 && self.frames >= self.earliest_end)
                || self.audio.len() >= RATE * 30
            {
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
    fn waveform_scale_matches_android_and_never_amplifies_noise() {
        assert_eq!(waveform_level(&[0; BLOCK]), 0.);
        assert_eq!(waveform_level(&[30; BLOCK]), 0.);
        assert_eq!(waveform_level(&[i16::MIN; BLOCK]), 1.);
        assert!(waveform_level(&[500; BLOCK]) > 0.);
        assert!(waveform_level(&[3000; BLOCK]) > waveform_level(&[500; BLOCK]));
    }

    #[test]
    fn waveform_is_ordered_smoothed_and_cleared_after_silence() {
        let start = Instant::now();
        let mut meter = Waveform::default();
        for index in 0..20 {
            let now = start + Duration::from_millis(index * 20);
            if let Some(levels) = meter.push(&[0; BLOCK], true, now, 0) {
                assert_eq!(levels, [0.; WAVEFORM_BARS]);
            }
        }
        // A fixed 40 ms presentation delay; the capture data itself isn't delayed.
        assert_eq!(
            meter.push(&[10000; BLOCK], true, start + Duration::from_millis(400), 0),
            Some([0.; WAVEFORM_BARS])
        );
        assert_eq!(
            meter.push(&[10000; BLOCK], true, start + Duration::from_millis(420), 0),
            Some([0.; WAVEFORM_BARS])
        );
        let first = meter
            .push(&[10000; BLOCK], true, start + Duration::from_millis(440), 0)
            .unwrap();
        assert!(first[..31].iter().all(|level| *level == 0.));
        assert!(first[31] > 0. && first[31] < 1.);
        let intermediate = meter
            .push(&[10000; BLOCK], true, start + Duration::from_millis(460), 0)
            .unwrap();
        let second = meter
            .push(&[10000; BLOCK], true, start + Duration::from_millis(480), 0)
            .unwrap();
        assert_eq!(second[30], first[31]);
        assert!(second[31] > intermediate[31]);
        assert!(intermediate[31] > first[31]);
        for index in 25..110 {
            meter.push(
                &[0; BLOCK],
                true,
                start + Duration::from_millis(index * 20),
                0,
            );
        }
        assert_eq!(
            meter.history(start + Duration::from_millis(2180)),
            [0.; WAVEFORM_BARS]
        );
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
        assert_eq!(emitted[0], live.history(end));
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
        assert!(emitted.len() >= 32);
        assert!(emitted.windows(2).all(|pair| {
            let span = pair[1].duration_since(pair[0]);
            span >= WAVEFORM_WINDOW && span <= Duration::from_millis(47)
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
        assert!(
            gap > 0. && gap < before * 0.55,
            "a gap isn't sustained speech"
        );
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
        for _ in 0..8 {
            assert!(matches!(ep.push(&[420; BLOCK]), Capture::More));
        }
        for _ in 0..39 {
            assert!(matches!(ep.push(&[0; BLOCK]), Capture::More));
        }
        let Capture::Complete(s) = ep.push(&[0; BLOCK]) else {
            panic!("no endpoint")
        };
        assert_eq!(s.iter().filter(|&&x| x == 420).count(), BLOCK * 8);
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
}
