//! Full-duplex speech monitoring. The speaker reference follows the device's
//! playback head, not the HTTP TTS stream or the desktop's visualization.
//! The portable WebRTC AEC3 / RNN VAD path runs only during an active turn.
use crate::windows_voice_signal::{BLOCK, RATE, SPEECH_RATE};
use sonora_aec3::{block::Block, block_processor::BlockProcessor, config::EchoCanceller3Config};
use sonora_agc2::vad_wrapper::VoiceActivityDetectorWrapper;
use sonora_common_audio::cascaded_biquad_filter::{BiQuadCoefficients, CascadedBiQuadFilter};
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};
#[path = "windows_double_talk.rs"]
mod double_talk;

const SOURCE_BLOCK: u64 = SPEECH_RATE as u64 / 50;
const SOURCE_HISTORY: usize = SPEECH_RATE as usize * 4;
const TAIL: Duration = Duration::from_millis(800);
const WINDOW: usize = 8;
const PREROLL: usize = 15; // 300 ms before the earliest qualifying speech.
const ECHO_HISTORY: usize = RATE * 6 / 5;
const MATCH_SAMPLES: usize = BLOCK * 2;

type Cursor = Box<dyn Fn() -> Option<u64> + Send + Sync>;

#[derive(Clone, Default)]
pub(crate) struct RenderReference(Arc<Mutex<Timeline>>);

#[derive(Default)]
struct Timeline {
    epoch: u64,
    cursor: Option<Cursor>,
    pcm: VecDeque<i16>,
    submitted: u64,
    last_head: Option<(u64, Instant)>,
    stopped: Option<(u64, Instant)>,
    failed_stop: Option<Instant>,
    last_capture_end: Option<i64>,
    // Source index at the start of the current continuous render segment.
    // WinMM's cursor counts PCM, not the wall-clock silence between writes.
    segment_start: u64,
}

enum ReferenceFrame {
    Quiet(u64),
    Known(u64, [i16; BLOCK]),
    Unavailable(u64),
}

impl RenderReference {
    /// Register a cursor query whose handle remains valid until `end` returns.
    /// Calls to the query happen only under this mutex; `end` removes it before
    /// the owner can close the handle. Epochs reject writes from an old output.
    pub fn begin(&self, cursor: impl Fn() -> Option<u64> + Send + Sync + 'static) -> u64 {
        let mut state = self.0.lock().unwrap_or_else(|e| e.into_inner());
        let epoch = state.epoch.wrapping_add(1);
        *state = Timeline {
            epoch,
            cursor: Some(Box::new(cursor)),
            ..Timeline::default()
        };
        epoch
    }

    pub fn submit(&self, epoch: u64, pcm: &[i16]) {
        let mut state = self.0.lock().unwrap_or_else(|e| e.into_inner());
        if state.epoch != epoch || state.cursor.is_none() {
            return;
        }
        // Once the old queue drained, virtual silence advanced against the
        // mic clock. New PCM resumes the source-domain cursor, which does
        // not include that silence. Re-anchor and forbid re-reading the old
        // segment as if it were newly emitted just before the next chunk.
        let old_end = state.submitted;
        let had_gap = state.last_capture_end.is_some_and(|p| p > old_end as i64)
            || state.last_head.is_some_and(|(head, at)| {
                head == old_end && at.elapsed() >= Duration::from_millis(20)
            });
        if had_gap {
            state.segment_start = old_end;
            state.last_capture_end = None;
            state.last_head = None;
        }
        state.submitted += pcm.len() as u64;
        state.pcm.extend(pcm);
        let excess = state.pcm.len().saturating_sub(SOURCE_HISTORY);
        state.pcm.drain(..excess);
    }

    pub fn end(&self, epoch: u64) {
        let mut state = self.0.lock().unwrap_or_else(|e| e.into_inner());
        if state.epoch != epoch {
            return;
        }
        let now = Instant::now();
        let result = state.cursor.take().and_then(|query| query());
        state.stopped = result.filter(|p| *p <= state.submitted).map(|p| (p, now));
        if state.stopped.is_none() && state.submitted != 0 {
            state.failed_stop = Some(now);
        }
    }

    fn frame(&self, queued_blocks: usize) -> ReferenceFrame {
        self.frame_at(queued_blocks, Instant::now())
    }

    fn frame_at(&self, queued_blocks: usize, now: Instant) -> ReferenceFrame {
        let mut state = self.0.lock().unwrap_or_else(|e| e.into_inner());
        let epoch = state.epoch;
        let age = SOURCE_BLOCK * queued_blocks.min(125) as u64;
        let (head, virtual_head) = if let Some(cursor) = &state.cursor {
            if state.submitted == 0 {
                return ReferenceFrame::Quiet(epoch);
            }
            let Some(head) = cursor().filter(|p| *p <= state.submitted) else {
                return ReferenceFrame::Unavailable(epoch);
            };
            if state.segment_start > 0 && head == state.segment_start && head < state.submitted {
                return ReferenceFrame::Known(epoch, [0; BLOCK]);
            }
            if state.last_head.is_some_and(|(old, _)| head < old) {
                return ReferenceFrame::Unavailable(epoch);
            }
            if state.last_head.is_none_or(|(old, _)| old != head) {
                state.last_head = Some((head, now));
            }
            // Once the queue is consumed, real time contains silence, not a
            // repeated copy of its last frame. Unknown/stalled active heads
            // cannot be substituted with a guessed render frame.
            let elapsed = state
                .last_head
                .map_or(Duration::ZERO, |(_, at)| now.saturating_duration_since(at));
            if head == state.submitted {
                (head, head.saturating_add(duration_samples(elapsed)))
            } else if head == 0 || elapsed >= Duration::from_millis(40) {
                return ReferenceFrame::Unavailable(epoch);
            } else {
                (head, head)
            }
        } else if let Some((head, at)) = state.stopped {
            let elapsed = now.saturating_duration_since(at);
            if elapsed > TAIL + Duration::from_millis(20 * queued_blocks.min(125) as u64) {
                state.pcm.clear();
                return ReferenceFrame::Quiet(epoch);
            }
            (head, head.saturating_add(duration_samples(elapsed)))
        } else if state
            .failed_stop
            .is_some_and(|at| now.saturating_duration_since(at) < TAIL)
        {
            return ReferenceFrame::Unavailable(epoch);
        } else {
            return ReferenceFrame::Quiet(epoch);
        };
        let observed_end = virtual_head as i64 - age as i64;
        // Stabilize small device-counter jitter without projecting any audio
        // beyond the measured presentation head. Clock jumps are handled by
        // the AEC's bounded discontinuity path, not a repeated render block.
        let end = state.last_capture_end.map_or(observed_end, |previous| {
            let expected = previous + SOURCE_BLOCK as i64;
            if observed_end >= expected && observed_end - expected <= SOURCE_BLOCK as i64 / 4 {
                expected
            } else {
                observed_end
            }
        });
        // Drivers may deliver completed capture blocks in bursts. Do not
        // vote repeatedly against an unchanged playback head; an omitted
        // frame is safer than pretending audio played before it did.
        if state
            .last_capture_end
            .is_some_and(|previous| end <= previous)
        {
            return ReferenceFrame::Unavailable(epoch);
        }
        state.last_capture_end = Some(end);
        let start = end - SOURCE_BLOCK as i64;
        let retained_start = state.submitted.saturating_sub(state.pcm.len() as u64);
        if start.max(0) < retained_start as i64 && end > 0 {
            return ReferenceFrame::Unavailable(epoch);
        }
        // 24 -> 16 kHz, duration-preserving 3:2 conversion. Integrate the
        // source over each 1.5-sample interval to attenuate aliasing. Never
        // read PCM past the observed head (queued but not yet heard).
        let sample = |index: i64| -> i32 {
            if index < 0 || index as u64 >= head || (index as u64) < state.segment_start {
                return 0;
            }
            state
                .pcm
                .get((index as u64 - retained_start) as usize)
                .copied()
                .unwrap_or(0) as i32
        };
        let frame = std::array::from_fn(|i| {
            let n = start + (i * 3 / 2) as i64;
            let a = sample(n);
            let b = sample(n + 1);
            (if i % 2 == 0 {
                (2 * a + b) / 3
            } else {
                (a + 2 * b) / 3
            }) as i16
        });
        ReferenceFrame::Known(epoch, frame)
    }
}

fn duration_samples(duration: Duration) -> u64 {
    (duration.as_nanos() * u128::from(SPEECH_RATE) / 1_000_000_000) as u64
}

fn rms(frame: &[i16]) -> f32 {
    let n = frame.len().max(1) as f32;
    let mean = frame.iter().map(|&s| f32::from(s)).sum::<f32>() / n;
    (frame
        .iter()
        .map(|&s| (f32::from(s) - mean).powi(2))
        .sum::<f32>()
        / n)
        .sqrt()
}

struct EchoCanceller {
    processor: BlockProcessor,
    render: Block,
    capture: Block,
    linear: Block,
    linear_delay: [i16; 64],
    high_pass: CascadedBiQuadFilter,
}

impl EchoCanceller {
    fn new() -> Self {
        let mut config = EchoCanceller3Config::default();
        config.filter.export_linear_aec_output = true;
        // Desktop routes (USB, Bluetooth, driver buffering) may have much
        // larger delays than a phone handset. Extend the AEC3 matched-filter
        // search to cover approximately one second and the room filter to
        // cover 96 ms instead of relying on handset defaults.
        config.delay.num_filters = 10;
        config.filter.refined.length_blocks = 24;
        config.filter.coarse.length_blocks = 24;
        config.filter.refined_initial.length_blocks = 24;
        config.filter.coarse_initial.length_blocks = 24;
        Self {
            processor: BlockProcessor::new(&config, RATE, 1, 1),
            render: Block::new(1, 1),
            capture: Block::new(1, 1),
            linear: Block::new(1, 1),
            linear_delay: [0; 64],
            // Same 16 kHz capture high-pass sections as upstream WebRTC
            // AudioProcessing, ported by Sonora (BSD-3-Clause). AEC3's
            // block API does not automatically instantiate the APM filter.
            high_pass: CascadedBiQuadFilter::new(&[
                BiQuadCoefficients {
                    b: [0.87735394, -1.7546839, 0.87735394],
                    a: [-1.8816873, 0.8880585],
                },
                BiQuadCoefficients {
                    b: [1., -1.9998101, 1.],
                    a: [-1.9760354, 0.9779709],
                },
                BiQuadCoefficients {
                    b: [1., -1.9996692, 1.],
                    a: [-1.9942658, 0.99548614],
                },
            ]),
        }
    }

    fn process(
        &mut self,
        mic: &[i16; BLOCK],
        render: &[i16; BLOCK],
    ) -> ([i16; BLOCK], [i16; BLOCK]) {
        let mut cleaned = [0; BLOCK];
        let mut linear = [0; BLOCK];
        let mut filtered = [0f32; BLOCK];
        self.high_pass.process(&mic.map(f32::from), &mut filtered);
        // AEC3's public block processor uses PCM16-scaled floats and 4 ms
        // blocks. No added AGC, playback gain change, or speech suppression.
        for i in (0..BLOCK).step_by(64) {
            let end = i + 64;
            for j in 0..64 {
                self.render.view_mut(0, 0)[j] = f32::from(render[i + j]);
                self.capture.view_mut(0, 0)[j] = filtered[i + j];
                self.linear.view_mut(0, 0)[j] = filtered[i + j];
            }
            let saturated = mic[i..end].iter().any(|&s| s <= -32700 || s >= 32700);
            self.processor.buffer_render(&self.render);
            self.processor.process_capture(
                false,
                saturated,
                Some(&mut self.linear),
                &mut self.capture,
            );
            for j in 0..64 {
                cleaned[i + j] = self.capture.view(0, 0)[j].round() as i16;
                linear[i + j] = self.linear.view(0, 0)[j].round() as i16;
            }
        }
        // Match AEC3's 4ms nonlinear suppression delay: the Android AGD4
        // detector was trained on the same temporally aligned outputs.
        let end = linear[BLOCK - 64..].try_into().unwrap();
        linear.copy_within(..BLOCK - 64, 64);
        linear[..64].copy_from_slice(&self.linear_delay);
        self.linear_delay = end;
        (cleaned, linear)
    }
}

/// Maximum absolute normalized waveform correlation over recent render at
/// plausible room/device delays. This independently rejects recognizable echo
/// even while the adaptive filter is converging. A pure energy gate cannot
/// distinguish a loud speaker from the person talking over it.
fn reference_match(mic: &[i16], render: &[i16]) -> f32 {
    let energy = rms(mic);
    if energy < 25.0 {
        return 0.0;
    }
    let n_samples = mic.len();
    let emphasized_mic: Vec<_> = mic
        .windows(2)
        .map(|w| f32::from(w[1]) - 0.8 * f32::from(w[0]))
        .collect();
    let emphasized_far: Vec<_> = render
        .windows(2)
        .map(|w| f32::from(w[1]) - 0.8 * f32::from(w[0]))
        .collect();
    let mut best = 0.0f32;
    let mut best_at = 0;
    let matches = |start: usize, step: usize| -> f32 {
        let mut dot = 0f32;
        let mut x2 = 0f32;
        let mut y2 = 0f32;
        let mut sy = 0f32;
        let mut sx = 0f32;
        let mut n = 0f32;
        // Pre-emphasis makes the match less sensitive to a coincident pitch
        // between two different voices and to DC / microphone coloration.
        for (&x, &y) in emphasized_mic.iter().step_by(step).zip(
            emphasized_far[start..start + n_samples - 1]
                .iter()
                .step_by(step),
        ) {
            dot += x * y;
            x2 += x * x;
            y2 += y * y;
            sy += y;
            sx += x;
            n += 1.0;
        }
        let denom = ((x2 - sx * sx / n) * (y2 - sy * sy / n)).max(0.0).sqrt();
        if denom < 1.0 {
            0.0
        } else {
            ((dot - sx * sy / n).abs() / denom).min(1.0)
        }
    };
    // Search coarsely, then refine the best candidate at native resolution;
    // a DC offset or a change in output volume does not change this score.
    for start in 0..=render.len() - n_samples {
        let score = matches(start, 16);
        if score > best {
            best = score;
            best_at = start;
            if score > 0.95 {
                let exact = matches(start, 1);
                if exact > 0.985 {
                    return exact;
                }
            }
        }
    }
    best = 0.0;
    for start in best_at.saturating_sub(3)..=(best_at + 3).min(render.len() - n_samples) {
        best = best.max(matches(start, 1));
    }
    best
}

fn probability(vad: &mut VoiceActivityDetectorWrapper, frame: &[i16; BLOCK]) -> f32 {
    let floats = frame.map(f32::from);
    let a = vad.analyze(&floats[..BLOCK / 2]);
    let b = vad.analyze(&floats[BLOCK / 2..]);
    (a + b) * 0.5
}

pub(crate) struct BargeInSignal {
    epoch: Option<u64>,
    echo: EchoCanceller,
    vad_raw: VoiceActivityDetectorWrapper,
    vad_linear: VoiceActivityDetectorWrapper,
    vad_clean: VoiceActivityDetectorWrapper,
    render: Vec<i16>,
    far_recent: usize,
    noise: f32,
    sustained_speech_floor: Option<f32>,
    votes: u8,
    prefix: VecDeque<[i16; BLOCK]>,
    latched: bool,
    unavailable_frames: usize,
    raw_history: [i16; MATCH_SAMPLES],
    linear_history: [i16; MATCH_SAMPLES],
    clean_history: [i16; MATCH_SAMPLES],
    quiet_far: u64,
    coupling_evidence: u64,
    attenuation_evidence: u64,
    recognized_echo: u64,
    route_confidence: u8,
    score_history: VecDeque<f32>,
    learned: Option<double_talk::Detector>,
    #[cfg(test)]
    last_debug: (f32, bool, f32, f32, f32, f32, f32, f32),
}

fn append_history(history: &mut [i16; MATCH_SAMPLES], frame: &[i16; BLOCK]) {
    history.rotate_left(BLOCK);
    history[MATCH_SAMPLES - BLOCK..].copy_from_slice(frame);
}

impl Default for BargeInSignal {
    fn default() -> Self {
        Self::new(25.0)
    }
}

impl BargeInSignal {
    pub fn new(noise_rms: f64) -> Self {
        let backend = sonora_simd::detect_backend();
        let vad = || VoiceActivityDetectorWrapper::new(backend, RATE as i32);
        Self {
            epoch: None,
            echo: EchoCanceller::new(),
            vad_raw: vad(),
            vad_linear: vad(),
            vad_clean: vad(),
            render: vec![0; ECHO_HISTORY],
            far_recent: 0,
            noise: (noise_rms as f32).clamp(8.0, 600.0),
            sustained_speech_floor: None,
            votes: 0,
            prefix: VecDeque::with_capacity(PREROLL + WINDOW),
            latched: false,
            unavailable_frames: 0,
            raw_history: [0; MATCH_SAMPLES],
            linear_history: [0; MATCH_SAMPLES],
            clean_history: [0; MATCH_SAMPLES],
            quiet_far: 0,
            coupling_evidence: 0,
            attenuation_evidence: 0,
            recognized_echo: 0,
            route_confidence: 0,
            score_history: VecDeque::with_capacity(60),
            learned: double_talk::Detector::new(),
            #[cfg(test)]
            last_debug: (0., false, 0., 0., 0., 0., 0., 0.),
        }
    }

    pub fn push(
        &mut self,
        mic: &[i16; BLOCK],
        reference: &RenderReference,
        queued_blocks: usize,
    ) -> Option<Vec<i16>> {
        self.observe(mic, reference.frame(queued_blocks))
    }

    pub fn rearm(&mut self) {
        self.latched = false;
        self.votes = 0;
        self.prefix.clear();
    }

    pub fn prefer_near_speech(mut self, floor: Option<f64>) -> Self {
        self.sustained_speech_floor = floor.map(|level| level as f32);
        self
    }

    fn observe(&mut self, mic: &[i16; BLOCK], reference: ReferenceFrame) -> Option<Vec<i16>> {
        if self.latched {
            return None;
        }
        let (epoch, render, playback) = match reference {
            ReferenceFrame::Quiet(epoch) => (epoch, Some([0; BLOCK]), false),
            ReferenceFrame::Known(epoch, frame) => (epoch, Some(frame), true),
            ReferenceFrame::Unavailable(epoch) => (epoch, None, true),
        };
        if self.epoch.is_some_and(|old| old != epoch) {
            // A new playback device/cursor starts a new acoustic timeline.
            // Never allow votes based on a different render to qualify a cut.
            self.echo = EchoCanceller::new();
            self.render.fill(0);
            self.far_recent = 0;
            self.votes = 0;
            self.quiet_far = 0;
            self.coupling_evidence = 0;
            self.attenuation_evidence = 0;
            self.recognized_echo = 0;
            self.route_confidence = 0;
            self.score_history.clear();
            self.raw_history.fill(0);
            self.linear_history.fill(0);
            self.clean_history.fill(0);
            self.learned = double_talk::Detector::new();
        }
        self.epoch = Some(epoch);
        let Some(render) = render else {
            self.votes <<= 1;
            self.unavailable_frames += 1;
            if self.prefix.len() == PREROLL + WINDOW {
                self.prefix.pop_front();
            }
            self.prefix.push_back([0; BLOCK]);
            if self.unavailable_frames == 3 {
                self.votes = 0;
                self.prefix.clear();
                self.echo = EchoCanceller::new();
                self.render.fill(0);
                self.far_recent = 0;
                self.quiet_far = 0;
                self.coupling_evidence = 0;
                self.attenuation_evidence = 0;
                self.recognized_echo = 0;
                self.route_confidence = 0;
                self.score_history.clear();
                self.raw_history.fill(0);
                self.linear_history.fill(0);
                self.clean_history.fill(0);
                self.learned = double_talk::Detector::new();
            }
            if self.unavailable_frames < 3 {
                // A missing reference is not evidence. Keep DSP and learned
                // acoustic histories moving at the actual capture cadence.
                self.echo.process(&[0; BLOCK], &[0; BLOCK]);
                if let Some(learned) = self.learned.as_mut() {
                    learned.observe(&[0; BLOCK], &[0; BLOCK], &[0; BLOCK], &[0; BLOCK]);
                }
                self.render.rotate_left(BLOCK);
                self.render[ECHO_HISTORY - BLOCK..].fill(0);
                append_history(&mut self.raw_history, &[0; BLOCK]);
                append_history(&mut self.clean_history, &[0; BLOCK]);
                append_history(&mut self.linear_history, &[0; BLOCK]);
            }
            return None;
        };
        self.unavailable_frames = 0;
        self.render.rotate_left(BLOCK);
        self.render[ECHO_HISTORY - BLOCK..].copy_from_slice(&render);
        let render_level = rms(&render);
        if render_level > 70.0 {
            self.far_recent = ECHO_HISTORY / BLOCK;
        } else {
            self.far_recent = self.far_recent.saturating_sub(1);
        }
        let raw_level = rms(mic);
        let raw_voice = probability(&mut self.vad_raw, mic);
        let floor = (self.noise * 2.5).max(55.0);
        append_history(&mut self.raw_history, mic);
        let (independent, retained) = if self.far_recent == 0 && !playback {
            // Thinking or a sufficiently long playback gap: no far-end PCM
            // can reach the microphone. No echo processing or extra delay.
            self.noise += (raw_level - self.noise)
                * if raw_voice < 0.25 || raw_level < self.noise {
                    0.03
                } else {
                    0.0
                };
            (
                raw_voice >= 0.65 && raw_level >= floor,
                if raw_level >= floor * 0.65 {
                    *mic
                } else {
                    [0; BLOCK]
                },
            )
        } else {
            let (clean, linear) = self.echo.process(mic, &render);
            let learned = self
                .learned
                .as_mut()
                .and_then(|d| d.observe(mic, &render, &clean, &linear))
                .unwrap_or(0.);
            let clean_level = rms(&clean);
            let linear_level = rms(&linear);
            let clean_voice = probability(&mut self.vad_clean, &clean);
            let linear_voice = probability(&mut self.vad_linear, &linear);
            append_history(&mut self.linear_history, &linear);
            append_history(&mut self.clean_history, &clean);
            let raw_match = reference_match(&self.raw_history, &self.render);
            // Echo-vetoed / quiet residuals need no expensive second match.
            let linear_match = if raw_match < 0.90 && linear_level >= floor * 0.6 {
                reference_match(&self.linear_history, &self.render)
            } else {
                1.0
            };
            let clean_match = if raw_match < 0.90 && clean_level >= floor * 0.6 {
                reference_match(&self.clean_history, &self.render)
            } else {
                1.0
            };
            // Qualify an acoustically uncoupled route from observed quiet
            // microphone blocks during substantive far-end playback (e.g.
            // headphones or muted system volume). Do not infer it merely
            // from the existence of submitted digital PCM.
            let quiet = render_level >= 500.0 && raw_level < (render_level * 0.01).max(35.0);
            self.quiet_far = ((self.quiet_far << 1) | u64::from(quiet)) & ((1u64 << 40) - 1);
            if raw_match > 0.82 && raw_level > floor && raw_voice >= 0.40 {
                // Recover from unmuting / unpluggng a headset on the same route.
                self.quiet_far = 0;
            }
            let uncoupled = self.quiet_far.count_ones() >= 8
                && (self.quiet_far & ((1u64 << 20) - 1)).count_ones() >= 4;
            // Lowering the learned threshold is justified only after showing
            // that active echo reached this route and AEC3 removed it. An
            // unproven/moving path keeps the stricter fallback threshold.
            let coupling = raw_match >= 0.48 && raw_level > floor && render_level > 100.;
            let attenuation = coupling && clean_level < raw_level * 0.30;
            self.coupling_evidence =
                ((self.coupling_evidence << 1) | u64::from(coupling)) & ((1u64 << 40) - 1);
            self.attenuation_evidence =
                ((self.attenuation_evidence << 1) | u64::from(attenuation)) & ((1u64 << 40) - 1);
            let measured_aec = self.attenuation_evidence.count_ones() >= 8
                && self.attenuation_evidence.count_ones() * 2
                    >= self.coupling_evidence.count_ones();
            // Confirm that this actual route supplied recognizable echo
            // which the reference-conditioned classifier rejected. This is
            // separate from mere digital playback activity and from an
            // improbable lucky score on a single frame.
            let known_echo = raw_match >= 0.20 && raw_level >= floor && learned < 0.45;
            self.recognized_echo =
                ((self.recognized_echo << 1) | u64::from(known_echo)) & ((1u64 << 50) - 1);
            if self.recognized_echo.count_ones() >= 20 {
                self.route_confidence = 30;
            } else {
                self.route_confidence = self.route_confidence.saturating_sub(1);
            }
            let known_route = self.route_confidence > 0;
            // A high score already seen during prior playback is not new
            // evidence of the user's speech. Compare against a delayed
            // distribution, excluding the latest 200 ms (the likely onset).
            let baseline = if self.score_history.len() >= 50 {
                let mut oldest: Vec<_> = self
                    .score_history
                    .iter()
                    .take(self.score_history.len() - 10)
                    .copied()
                    .collect();
                oldest.sort_by(f32::total_cmp);
                oldest[oldest.len() * 9 / 10]
            } else {
                0.60
            };
            if self.score_history.len() == 60 {
                self.score_history.pop_front();
            }
            self.score_history.push_back(learned);
            // Strong waveform evidence of echo vetoes the raw path even at
            // filter startup. AEC3 can make an echo look like a voiced
            // residual, so residual decorrelation and VAD must both agree.
            let raw_independent = (raw_match < 0.52 || (uncoupled && raw_match < 0.82))
                && raw_voice >= 0.65
                && raw_level >= floor;
            let residual_independent = raw_match < 0.90
                && ((linear_match < 0.63
                    && linear_voice >= 0.68
                    && linear_level >= floor
                    && linear_level >= raw_level * 0.16)
                    || (clean_match < 0.58
                        && clean_voice >= 0.68
                        && clean_level >= floor
                        && clean_level >= raw_level * 0.16));
            let retained = if (raw_match < 0.50 || (uncoupled && raw_match < 0.82))
                && raw_level >= floor * 0.60
            {
                // Headphones, mute, no coupling: preserve the actual onset
                // instead of a cold AEC3 suppressor attenuating its syllable.
                *mic
            } else if raw_match < 0.90 && linear_match < 0.65 && linear_level >= floor * 0.60 {
                // AEC3's linear output preserves speech that the nonlinear
                // suppressor may remove on double talk. It is still screened.
                linear
            } else if raw_match < 0.90 && clean_match < 0.62 && clean_level >= floor * 0.60 {
                clean
            } else {
                [0; BLOCK]
            };
            let score_gate = if uncoupled {
                0.56
            } else {
                let minimum = if measured_aec && known_route {
                    0.54
                } else if known_route {
                    0.58
                } else {
                    0.60
                };
                (baseline + 0.18).max(minimum).min(0.82)
            };
            #[cfg(test)]
            {
                self.last_debug = (
                    learned,
                    raw_independent || residual_independent,
                    raw_match,
                    linear_match,
                    raw_level,
                    linear_level,
                    raw_voice,
                    clean_level,
                );
            }
            (
                (raw_independent || residual_independent) && learned >= score_gate,
                if learned >= 0.25 {
                    retained
                } else {
                    [0; BLOCK]
                },
            )
        };
        let uncoupled = self.quiet_far.count_ones() >= 8
            && (self.quiet_far & ((1u64 << 20) - 1)).count_ones() >= 4;
        let required = if self.far_recent > 0 && !uncoupled {
            5
        } else {
            4
        };
        let near = self
            .sustained_speech_floor
            .is_none_or(|floor| rms(&retained) >= floor);
        self.onset(independent && near, retained, required)
    }

    fn onset(
        &mut self,
        independent: bool,
        retained: [i16; BLOCK],
        required: u32,
    ) -> Option<Vec<i16>> {
        if self.latched {
            return None;
        }
        self.votes = (self.votes << 1) | u8::from(independent);
        if self.prefix.len() == PREROLL + WINDOW {
            self.prefix.pop_front();
        }
        self.prefix.push_back(retained);
        if independent && self.votes.count_ones() >= required {
            self.latched = true;
            let onset = (u8::BITS - self.votes.leading_zeros()) as usize;
            let skip = self.prefix.len().saturating_sub(PREROLL + onset);
            Some(
                self.prefix
                    .iter()
                    .skip(skip)
                    .flat_map(|f| f.iter().copied())
                    .collect(),
            )
        } else {
            None
        }
    }

    /// After interruption, continue consuming the already-heard reference's
    /// acoustic tail. Never forward PCM still queued at the instant of reset.
    pub fn clean_tail(
        &mut self,
        mic: &mut [i16; BLOCK],
        reference: &RenderReference,
        queued_blocks: usize,
    ) {
        let render = match reference.frame(queued_blocks) {
            ReferenceFrame::Quiet(_) => return,
            ReferenceFrame::Unavailable(_) => [0; BLOCK],
            ReferenceFrame::Known(_, render) => render,
        };
        self.render.rotate_left(BLOCK);
        self.render[ECHO_HISTORY - BLOCK..].copy_from_slice(&render);
        let (clean, linear) = self.echo.process(mic, &render);
        let raw_match = reference_match(mic, &self.render);
        // The command already qualified. Keep its continuation even if one
        // cursor reading is lost; prefer screened linear output to dropping
        // a syllable. No future queued audio enters the acoustic tail.
        if raw_match >= 0.55 {
            *mic = if reference_match(&linear, &self.render) < 0.58 {
                linear
            } else {
                clean
            };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};

    #[test]
    fn timeline_only_references_played_pcm_and_queued_capture_age() {
        let position = Arc::new(AtomicU64::new(480));
        let cursor = position.clone();
        let reference = RenderReference::default();
        let epoch = reference.begin(move || Some(cursor.load(Ordering::Relaxed)));
        let mut samples = vec![1000; 480];
        samples.extend([2000; 480]);
        samples.extend([3000; 480]);
        reference.submit(epoch, &samples);
        let now = Instant::now();
        assert!(
            matches!(reference.frame_at(0, now), ReferenceFrame::Known(_, frame) if frame == [1000; BLOCK])
        );
        assert!(matches!(
            reference.frame_at(0, now + Duration::from_millis(20)),
            ReferenceFrame::Unavailable(_)
        ));
        position.store(1440, Ordering::Relaxed);
        assert!(
            matches!(reference.frame_at(1, now + Duration::from_millis(40)), ReferenceFrame::Known(_, frame) if frame == [2000; BLOCK])
        );
        assert!(
            matches!(reference.frame_at(0, now + Duration::from_millis(40)), ReferenceFrame::Known(_, frame) if frame == [3000; BLOCK])
        );
        assert!(
            matches!(reference.frame_at(0, now + Duration::from_millis(60)), ReferenceFrame::Known(_, frame) if frame == [0; BLOCK])
        );
    }

    #[test]
    fn stopped_tail_never_contains_future_queued_audio() {
        let reference = RenderReference::default();
        let epoch = reference.begin(|| Some(480));
        let mut samples = vec![1000; 480];
        samples.extend([25000; 960]);
        reference.submit(epoch, &samples);
        reference.end(epoch);
        let stopped_at = reference.0.lock().unwrap().stopped.unwrap().1;
        assert!(
            matches!(reference.frame_at(0, stopped_at), ReferenceFrame::Known(_, frame) if frame == [1000; BLOCK])
        );
        assert!(
            matches!(reference.frame_at(0, stopped_at + Duration::from_millis(20)), ReferenceFrame::Known(_, frame) if frame == [0; BLOCK])
        );
        assert!(matches!(
            reference.frame_at(0, stopped_at + TAIL + Duration::from_millis(20)),
            ReferenceFrame::Quiet(_)
        ));
    }

    #[test]
    fn resumed_tts_after_stream_gap_reanchors_source_without_replaying_old_pcm() {
        let pos = Arc::new(AtomicU64::new(480));
        let cursor = pos.clone();
        let reference = RenderReference::default();
        let epoch = reference.begin(move || Some(cursor.load(Ordering::Relaxed)));
        reference.submit(epoch, &[1000; 480]);
        let now = Instant::now();
        assert!(matches!(reference.frame_at(0,now),ReferenceFrame::Known(_,f) if f==[1000;BLOCK]));
        assert!(
            matches!(reference.frame_at(0,now+Duration::from_millis(500)),ReferenceFrame::Known(_,f) if f==[0;BLOCK])
        );
        reference.submit(epoch, &[2000; 480]);
        // Queued new PCM is not yet played; neither old PCM nor new queued PCM may be repeated.
        assert!(
            matches!(reference.frame_at(0,now+Duration::from_millis(520)),ReferenceFrame::Known(_,f) if f==[0;BLOCK])
        );
        pos.store(960, Ordering::Relaxed);
        assert!(
            matches!(reference.frame_at(0,now+Duration::from_millis(540)),ReferenceFrame::Known(_,f) if f==[2000;BLOCK])
        );
    }

    #[test]
    fn old_output_cannot_destroy_new_cursor() {
        let reference = RenderReference::default();
        let old = reference.begin(|| Some(480));
        let active = reference.begin(|| Some(480));
        reference.submit(old, &[25000; 480]);
        reference.end(old);
        reference.submit(active, &[1000; 480]);
        assert!(
            matches!(reference.frame(0), ReferenceFrame::Known(g, frame) if g == active && frame == [1000; BLOCK])
        );
    }

    fn pseudo_pcm(index: usize) -> i16 {
        let x = (index as u32)
            .wrapping_mul(32452843)
            .rotate_left(13)
            .wrapping_mul(3266489917);
        ((x >> 16) as i16) / 3
    }

    #[test]
    fn reference_match_detects_scaled_inverted_delay() {
        let recent: Vec<_> = (0..ECHO_HISTORY).map(pseudo_pcm).collect();
        let start = ECHO_HISTORY - 2721 - BLOCK;
        let mic: [i16; BLOCK] = std::array::from_fn(|i| -recent[start + i] / 4 + 120);
        assert!(reference_match(&mic, &recent) > 0.97);
        let unrelated: [i16; BLOCK] = std::array::from_fn(|i| pseudo_pcm(i + ECHO_HISTORY * 2));
        assert!(reference_match(&unrelated, &recent) < 0.75);
    }

    #[test]
    fn no_trigger_on_silence_repeated_reference_or_scaled_delayed_echo() {
        let far: Vec<_> = (0..BLOCK * 110).map(pseudo_pcm).collect();
        let mut signal = BargeInSignal::default();
        for block in 0..80 {
            let index = block * BLOCK;
            let render = std::array::from_fn(|i| far[index + i]);
            let mic =
                std::array::from_fn(|i| (index + i).checked_sub(550).map_or(0, |at| far[at] / 2));
            assert!(signal
                .observe(&mic, ReferenceFrame::Known(1, render))
                .is_none());
        }
        for _ in 0..WINDOW * 2 {
            assert!(signal
                .observe(&[0; BLOCK], ReferenceFrame::Unavailable(1))
                .is_none());
            assert!(signal
                .observe(&[0; BLOCK], ReferenceFrame::Quiet(1))
                .is_none());
        }
    }

    #[test]
    fn qualification_retains_syllable_start_and_current_frame_once() {
        let mut signal = BargeInSignal::default();
        for id in 1..=20i16 {
            assert!(signal.onset(false, [id; BLOCK], 4).is_none());
        }
        for id in 21..=23i16 {
            assert!(signal.onset(true, [id; BLOCK], 4).is_none());
        }
        let prefix = signal.onset(true, [24; BLOCK], 4).unwrap();
        assert_eq!(prefix.len(), (PREROLL + 4) * BLOCK);
        assert_eq!(&prefix[..BLOCK], &[6; BLOCK]);
        assert_eq!(&prefix[prefix.len() - BLOCK..], &[24; BLOCK]);
        assert!(signal.onset(true, [25; BLOCK], 4).is_none());
        signal.rearm();
        for id in 40..=42 {
            assert!(signal.onset(true, [id; BLOCK], 4).is_none());
        }
        assert_eq!(
            signal.onset(true, [43; BLOCK], 4).unwrap()[..BLOCK],
            [40; BLOCK]
        );
    }

    /// Optional, offline real-speech bench. Supply already synthesized mono
    /// PCM16 16 kHz `far-*.pcm` / `near-*.pcm`; no device or API is used.
    #[test]
    #[ignore = "requires offline speech fixtures (AEGIS_BARGE_IN_PCM_DIR)"]
    fn replay_mixed_speech() {
        use std::{fs, path::PathBuf};
        let folder =
            PathBuf::from(std::env::var_os("AEGIS_BARGE_IN_PCM_DIR").expect("fixtures dir"));
        let load = |prefix: &str| {
            let mut files: Vec<_> = fs::read_dir(&folder)
                .unwrap()
                .map(|v| v.unwrap().path())
                .filter(|p| p.file_name().unwrap().to_string_lossy().starts_with(prefix))
                .collect();
            files.sort();
            files
                .iter()
                .map(|p| {
                    fs::read(p)
                        .unwrap()
                        .chunks_exact(2)
                        .map(|c| i16::from_le_bytes([c[0], c[1]]))
                        .collect::<Vec<_>>()
                })
                .collect::<Vec<_>>()
        };
        let far = load("far-");
        let near = load("near-");
        assert!(far.len() >= 2 && near.len() >= 2);
        let mut false_cuts = 0;
        let mut control_trials = 0;
        let mut early = 0;
        let mut hits = [0; 5];
        let mut trials = [0; 5];
        let mut latency = Vec::new();
        let mut cpu = Vec::new();
        for (i, far) in far.iter().enumerate() {
            for (j, near) in near.iter().enumerate() {
                let start = near
                    .chunks_exact(BLOCK)
                    .position(|f| rms(f) > 100.0)
                    .unwrap_or(0)
                    * BLOCK;
                let near = &near[start..];
                let onset = BLOCK * 55;
                let len = onset + near.len() + BLOCK * 12;
                let ref_sample = |at: usize| f32::from(far[at % far.len()]) * 0.70;
                let near_sample =
                    |at: usize| f32::from(near.get(at.saturating_sub(onset)).copied().unwrap_or(0));
                for (case, (gain, delay, speak, reverb)) in [
                    (0.0, 80, true, false),
                    (0.12, 143, true, false),
                    (0.6, 911, true, false),
                    (0.8, 3000, true, true),
                    (0.0, 80, true, false),
                    (0.12, 143, false, false),
                    (0.6, 911, false, false),
                    (0.8, 3000, false, true),
                ]
                .into_iter()
                .enumerate()
                {
                    let mut signal = BargeInSignal::new(25.);
                    let mut cut = None;
                    let start = Instant::now();
                    for index in (0..len).step_by(BLOCK) {
                        let render = std::array::from_fn(|n| {
                            if case == 4 {
                                0
                            } else {
                                ref_sample(index + n) as i16
                            }
                        });
                        let mic = std::array::from_fn(|n| {
                            let at = index + n;
                            let direct = at.checked_sub(delay).map_or(0., ref_sample);
                            let reflections = if reverb {
                                at.checked_sub(delay + 53).map_or(0., ref_sample) * 0.42
                                    + at.checked_sub(delay + 267).map_or(0., ref_sample) * 0.28
                            } else {
                                0.
                            };
                            let echo = (direct + reflections) * gain;
                            let voice = if speak && at >= onset {
                                near_sample(at) * 0.55
                            } else {
                                0.
                            };
                            (echo + voice + f32::from(pseudo_pcm(at + 999)) / 360.)
                                .clamp(-32768., 32767.) as i16
                        });
                        let now = Instant::now();
                        let result = signal.observe(
                            &mic,
                            if case == 4 {
                                ReferenceFrame::Quiet(1)
                            } else {
                                ReferenceFrame::Known(1, render)
                            },
                        );
                        cpu.push(now.elapsed().as_micros());
                        if let Some(prefix) = result {
                            assert_eq!(prefix.len() % BLOCK, 0);
                            assert!(prefix.len() <= (PREROLL + WINDOW) * BLOCK);
                            assert!(prefix.iter().any(|&x| x != 0));
                            cut = Some(index + BLOCK);
                            break;
                        }
                    }
                    let _elapsed = start.elapsed();
                    if speak {
                        trials[case] += 1;
                        if let Some(at) = cut {
                            if at < onset {
                                early += 1;
                            } else if at <= onset + near.len() + BLOCK * 6 {
                                hits[case] += 1;
                                latency.push((at - onset) / 16);
                            }
                        }
                    } else {
                        control_trials += 1;
                        false_cuts += usize::from(cut.is_some());
                    }
                    eprintln!(
                        "case={case} far={i} near={j} cut_ms={:?}",
                        cut.map(|v| v / 16)
                    );
                }
            }
        }
        latency.sort_unstable();
        cpu.sort_unstable();
        eprintln!("hits={hits:?}/{trials:?} echo_controls={false_cuts}/{control_trials} early={early} latency_ms={latency:?} cpu_p95_us={}", cpu[cpu.len() * 95 / 100]);
        assert_eq!(false_cuts, 0, "echo-only cuts");
        assert_eq!(early, 0, "pre-speech cuts");
        assert_eq!(hits[0], trials[0], "muted reference must not block speech");
        assert_eq!(hits[4], trials[4], "thinking must accept dry speech");
        assert!(
            hits[1..4]
                .iter()
                .zip(&trials[1..4])
                .all(|(h, t)| *h * 2 >= *t),
            "overlap sensitivity"
        );
    }

    /// Real stationary / changing room echo from public AEC Challenge WAV
    /// pairs, re-encoded externally to 16 kHz little-endian signed PCM.
    #[test]
    #[ignore = "requires downloaded public AEC Challenge room pairs and synthesized near voice"]
    fn replay_public_room_echo() {
        use std::{fs, path::PathBuf};
        let dir =
            PathBuf::from(std::env::var_os("AEGIS_BARGE_ROOM_DIR").expect("room fixtures dir"));
        let voice_dir =
            PathBuf::from(std::env::var_os("AEGIS_BARGE_IN_PCM_DIR").expect("near voice dir"));
        let read = |p: PathBuf| {
            fs::read(p)
                .unwrap()
                .chunks_exact(2)
                .map(|c| i16::from_le_bytes([c[0], c[1]]))
                .collect::<Vec<_>>()
        };
        let split =
            std::env::var("AEGIS_BARGE_ROOM_SPLIT").unwrap_or_else(|_| "development".into());
        let mut false_cuts = 0;
        let mut early = 0;
        let mut controls = 0;
        let mut hits = [0; 2];
        let mut trials = [0; 2];
        let mut latency = Vec::new();
        for id in 1..=30 {
            let selected = match split.as_str() {
                "development" => id <= 3 || (10..=12).contains(&id),
                "holdout" => (4..=9).contains(&id) || (13..=18).contains(&id),
                "final" => (19..=30).contains(&id),
                _ => panic!("unknown room split"),
            };
            if !selected {
                continue;
            }
            let echo = read(dir.join(format!("room-{id:02}-mic.pcm")));
            let render = read(dir.join(format!("room-{id:02}-render.pcm")));
            let samples = echo.len().min(render.len()).min(RATE * 8) / BLOCK * BLOCK;
            let onset = RATE * 3;
            if samples < onset + RATE {
                continue;
            }
            let voice = read(voice_dir.join(format!("near-{}.pcm", id % 2 + 1)));
            let from = voice
                .chunks_exact(BLOCK)
                .position(|f| rms(f) > 100.)
                .unwrap_or(0)
                * BLOCK;
            let voiced = &voice[from..(from + BLOCK * 23).min(voice.len())]; // 460 ms phrase.
            for case in 0..3 {
                let near_scale = if case == 0 {
                    0.
                } else {
                    let target = rms(&echo[onset..onset + voiced.len()]).max(70.)
                        * if case == 1 { 1.4 } else { 2.8 };
                    target / rms(voiced).max(1.)
                };
                let mut signal = BargeInSignal::new(25.);
                let mut cut = None;
                let mut score_max = 0f32;
                let mut eligible_scores = Vec::new();
                let mut all_scores = Vec::new();
                let mut delays = Vec::new();
                let mut raw_power = 0f64;
                let mut linear_power = 0f64;
                let mut clean_power = 0f64;
                for offset in (0..samples).step_by(BLOCK) {
                    let far: [i16; BLOCK] = render[offset..offset + BLOCK].try_into().unwrap();
                    let mic = std::array::from_fn(|n| {
                        let at = offset + n;
                        let voice = at
                            .checked_sub(onset)
                            .and_then(|k| voiced.get(k))
                            .copied()
                            .unwrap_or(0);
                        (f32::from(echo[at]) + f32::from(voice) * near_scale).clamp(-32768., 32767.)
                            as i16
                    });
                    let result = signal.observe(&mic, ReferenceFrame::Known(1, far));
                    if case == 0 && offset >= 2 * RATE {
                        let (_, _, _, _, raw, linear, _, clean) = signal.last_debug;
                        raw_power += f64::from(raw).powi(2);
                        linear_power += f64::from(linear).powi(2);
                        clean_power += f64::from(clean).powi(2);
                        delays.push(signal.echo.processor.get_metrics().delay_ms);
                    }
                    if case == 0 || (offset >= onset && offset < onset + voiced.len() + BLOCK * 6) {
                        let (score, eligible, ..) = signal.last_debug;
                        score_max = score_max.max(score);
                        all_scores.push(score);
                        if eligible {
                            eligible_scores.push(score);
                        }
                    }
                    if result.is_some() {
                        cut = Some(offset + BLOCK);
                        break;
                    }
                }
                if case == 0 {
                    controls += 1;
                    false_cuts += usize::from(cut.is_some());
                } else {
                    trials[case - 1] += 1;
                    if let Some(at) = cut {
                        if at <= onset {
                            early += 1;
                        } else if at <= onset + voiced.len() + BLOCK * 6 {
                            hits[case - 1] += 1;
                            latency.push((at - onset) / 16);
                        }
                    }
                }
                eligible_scores.sort_by(f32::total_cmp);
                all_scores.sort_by(f32::total_cmp);
                eprintln!("room={id:02} case={case} cut_ms={:?} max={score_max:.3} eligible_n={} eligible_high={:.3} eligible_p80={:.3} score_p80={:.3}",cut.map(|x|x/16),eligible_scores.len(),eligible_scores.last().copied().unwrap_or(0.),eligible_scores.get(eligible_scores.len()*4/5).copied().unwrap_or(0.),all_scores.get(all_scores.len()*4/5).copied().unwrap_or(0.));
                if case == 0 && !delays.is_empty() {
                    delays.sort_unstable();
                    eprintln!("room={id:02} AEC delay median={}ms linear_attenuation={:.1}dB nonlinear_attenuation={:.1}dB",delays[delays.len()/2],10.*(raw_power/linear_power.max(1.)).log10(),10.*(raw_power/clean_power.max(1.)).log10());
                }
            }
        }
        latency.sort_unstable();
        eprintln!("room split={split} false_cuts={false_cuts}/{controls} early={early} hits={hits:?}/{trials:?} latency_ms={latency:?}");
        assert!(controls >= 6);
        assert_eq!(false_cuts, 0);
        assert_eq!(early, 0);
        assert!(hits.iter().zip(trials).all(|(h, t)| *h * 2 >= t));
    }
}
