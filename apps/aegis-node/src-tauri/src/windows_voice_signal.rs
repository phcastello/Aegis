//! PCM16 mono at 16 kHz. Election quality compares the last half-second with
//! the preceding second, using the same windows as the Android node.
use std::collections::VecDeque;

pub(crate) const RATE: usize = 16_000;
pub(crate) const BLOCK: usize = 320;
const BLOCKS_PER_SECOND: usize = RATE / BLOCK;

fn power(samples: &[i16]) -> f64 {
    samples.iter().map(|&s| f64::from(s).powi(2)).sum::<f64>() / samples.len().max(1) as f64
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
