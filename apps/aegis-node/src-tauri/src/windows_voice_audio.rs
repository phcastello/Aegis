//! Windows multimedia's shared PCM devices perform conversion to the requested
//! sample rate. Buffers stay pinned until the driver returns their headers.
use crate::windows_barge_in_signal::{RenderCursor, RenderReference};
use crate::windows_voice_signal::{PlaybackSpectrum, BLOCK, SPECTRUM_BANDS, SPEECH_RATE};
use std::{
    cell::UnsafeCell,
    collections::VecDeque,
    mem::size_of,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use tokio_util::sync::CancellationToken;
use windows::{
    core::PSTR,
    Win32::{
        Foundation::{CloseHandle, HANDLE, WAIT_FAILED},
        Media::{Audio::*, MMTIME, MM_WIM_DATA, MM_WOM_DONE, TIME_BYTES, TIME_MS, TIME_SAMPLES},
        System::Threading::{CreateEventW, SetEvent, WaitForSingleObject},
    },
};

const HEADER: u32 = size_of::<WAVEHDR>() as u32;
fn format(rate: u32) -> WAVEFORMATEX {
    WAVEFORMATEX {
        wFormatTag: WAVE_FORMAT_PCM as u16,
        nChannels: 1,
        nSamplesPerSec: rate,
        nAvgBytesPerSec: rate * 2,
        nBlockAlign: 2,
        wBitsPerSample: 16,
        cbSize: 0,
    }
}
struct Event(HANDLE);
impl Event {
    fn new() -> Result<Self, &'static str> {
        unsafe { CreateEventW(None, false, false, None) }
            .map(Self)
            .map_err(|_| "audio_error")
    }
    fn wait(&self, cancel: &CancellationToken, deadline: Instant) -> Result<(), &'static str> {
        if cancel.is_cancelled() {
            return Err("cancelled");
        }
        if Instant::now() >= deadline {
            return Err("device_unavailable");
        }
        if unsafe { WaitForSingleObject(self.0, 50) } == WAIT_FAILED {
            return Err("audio_error");
        }
        Ok(())
    }
}
impl Drop for Event {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}

// WinMM calls this with WIM_DATA or WOM_DONE after it returns ownership of
// a queued buffer. Signal only, as required by the WinMM callback contract.
unsafe extern "system" fn completed(_: usize, message: u32, event: usize, header: usize, _: usize) {
    if matches!(message, MM_WIM_DATA | MM_WOM_DONE) && header != 0 {
        let flag = unsafe { (*(header as *const WAVEHDR)).dwUser as *const AtomicBool };
        unsafe {
            (*flag).store(true, Ordering::Release);
        }
        let _ = unsafe { SetEvent(HANDLE(event as *mut _)) };
    }
}
struct Buffer {
    header: Box<UnsafeCell<WAVEHDR>>,
    pcm: Box<[i16]>,
    returned: Box<AtomicBool>,
}
impl Buffer {
    fn new(mut pcm: Box<[i16]>) -> Self {
        let returned = Box::new(AtomicBool::new(false));
        let header = Box::new(UnsafeCell::new(WAVEHDR {
            lpData: PSTR(pcm.as_mut_ptr().cast()),
            dwBufferLength: (pcm.len() * 2) as u32,
            dwUser: (&*returned as *const AtomicBool) as usize,
            ..Default::default()
        }));
        Self {
            header,
            pcm,
            returned,
        }
    }
    fn done(&self) -> bool {
        self.returned.load(Ordering::Acquire)
    }
}

struct RouteWatch {
    devices: IMMDeviceEnumerator,
    identity: String,
    checked: Instant,
}
impl RouteWatch {
    fn new() -> Option<Self> {
        use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_ALL};
        let devices: IMMDeviceEnumerator =
            unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }.ok()?;
        let identity = Self::id(&devices)?;
        Some(Self {
            devices,
            identity,
            checked: Instant::now(),
        })
    }
    fn id(devices: &IMMDeviceEnumerator) -> Option<String> {
        use windows::Win32::System::Com::CoTaskMemFree;
        let value = unsafe {
            devices
                .GetDefaultAudioEndpoint(eCapture, eMultimedia)
                .ok()?
                .GetId()
                .ok()?
        };
        let result = unsafe { value.to_string() }.ok();
        unsafe { CoTaskMemFree(Some(value.0.cast())) };
        result
    }
    fn changed(&mut self) -> bool {
        if self.checked.elapsed() < Duration::from_secs(2) {
            return false;
        }
        self.checked = Instant::now();
        Self::id(&self.devices).is_some_and(|id| id != self.identity)
    }
}

pub(crate) struct Input {
    handle: HWAVEIN,
    route: Option<RouteWatch>,
    event: Event,
    buffers: VecDeque<Buffer>,
}
impl Input {
    pub fn open() -> Result<Self, &'static str> {
        let event = Event::new()?;
        let mut handle = HWAVEIN::default();
        if unsafe {
            waveInOpen(
                Some(&mut handle),
                WAVE_MAPPER,
                &format(16000),
                Some(completed as *const () as usize),
                Some(event.0 .0 as usize),
                CALLBACK_FUNCTION,
            )
        } != 0
        {
            return Err("microphone_unavailable");
        }
        let mut input = Self {
            handle,
            route: RouteWatch::new(),
            event,
            buffers: VecDeque::new(),
        };
        // The claim can take up to two seconds. Keep listening locally until
        // it returns so an inline command survives the election.
        for _ in 0..125 {
            let buf = Buffer::new(vec![0; BLOCK].into_boxed_slice());
            if unsafe { waveInPrepareHeader(handle, buf.header.get(), HEADER) } != 0 {
                return Err("microphone_unavailable");
            }
            input.buffers.push_back(buf);
            let hdr = input.buffers.back_mut().unwrap().header.get();
            if unsafe { waveInAddBuffer(handle, hdr, HEADER) } != 0 {
                return Err("microphone_unavailable");
            }
        }
        if unsafe { waveInStart(handle) } != 0 {
            return Err("microphone_unavailable");
        }
        Ok(input)
    }
    pub fn pending(&self) -> usize {
        self.buffers.iter().take_while(|b| b.done()).count()
    }
    pub fn read(&mut self, cancel: &CancellationToken) -> Result<[i16; BLOCK], &'static str> {
        if self.route.as_mut().is_some_and(RouteWatch::changed) {
            return Err("input_route_changed");
        }
        let deadline = Instant::now() + Duration::from_secs(3);
        if self.buffers.back().is_some_and(Buffer::done) {
            return Err("capture_overrun");
        }
        let buf = self.buffers.front().ok_or("audio_error")?;
        while !buf.done() {
            self.event.wait(cancel, deadline)?;
        }
        if cancel.is_cancelled() {
            return Err("cancelled");
        }
        let buf = self.buffers.front_mut().unwrap();
        if unsafe { (*buf.header.get()).dwBytesRecorded } as usize != BLOCK * 2 {
            return Err("microphone_disconnected");
        }
        let mut frame = [0; BLOCK];
        frame.copy_from_slice(&buf.pcm);
        buf.returned.store(false, Ordering::Release);
        if unsafe { waveInAddBuffer(self.handle, buf.header.get(), HEADER) } != 0 {
            return Err("microphone_disconnected");
        }
        self.buffers.rotate_left(1);
        Ok(frame)
    }
}
impl Drop for Input {
    fn drop(&mut self) {
        unsafe { waveInReset(self.handle) };
        for buf in self.buffers.drain(..) {
            if unsafe { waveInUnprepareHeader(self.handle, buf.header.get(), HEADER) } != 0 {
                std::mem::forget(buf)
            }
        }
        unsafe { waveInClose(self.handle) };
    }
}

fn playback_position(handle: HWAVEOUT) -> Option<u64> {
    let mut position = MMTIME {
        wType: TIME_SAMPLES,
        ..Default::default()
    };
    if unsafe { waveOutGetPosition(handle, &mut position, size_of::<MMTIME>() as u32) } != 0 {
        return None;
    }
    // WinMM may choose a different supported unit, even when asked for samples.
    match position.wType {
        TIME_SAMPLES => Some(u64::from(unsafe { position.u.sample })),
        TIME_BYTES => Some(u64::from(unsafe { position.u.cb }) / 2),
        TIME_MS => Some(u64::from(unsafe { position.u.ms }) * u64::from(SPEECH_RATE) / 1000),
        _ => None,
    }
}

/// Control belongs to one voice turn. Only the Output owner attaches the
/// device; any clone may request pause/resume before or during playback.
/// All raw-handle access via the control shares the detach lock.
#[derive(Clone, Default)]
pub(crate) struct PlaybackPause(Arc<Mutex<PauseState>>);

#[derive(Default)]
struct PauseState {
    handle: Option<usize>,
    retired: bool,
    paused_at: Option<Instant>,
    paused_frames: Option<u64>,
    paused_total: Duration,
    committed_frames: u64,
}

impl PauseState {
    fn paused_duration(&self, now: Instant) -> Duration {
        self.paused_total
            + self
                .paused_at
                .map_or(Duration::ZERO, |at| now.saturating_duration_since(at))
    }

    fn raw_handle(&self) -> Option<HWAVEOUT> {
        self.handle.map(|raw| HWAVEOUT(raw as *mut _))
    }

    fn cursor(&self) -> Option<RenderCursor> {
        let frames = if self.paused_at.is_some() {
            self.paused_frames?
        } else {
            playback_position(self.raw_handle()?)?
        };
        (frames <= self.committed_frames).then_some(RenderCursor {
            frames,
            paused_at: self.paused_at,
        })
    }
}

impl PlaybackPause {
    /// Freeze the real WinMM cursor, without reset or audio buffer changes.
    /// Before the output exists, arm the pause so no PCM starts playing.
    pub fn pause(&self) -> Result<(), &'static str> {
        let mut state = self.0.lock().map_err(|_| "audio_error")?;
        if state.retired {
            return Err("cancelled");
        }
        if state.paused_at.is_some() {
            return Ok(());
        }
        state.paused_frames = if let Some(handle) = state.raw_handle() {
            if unsafe { waveOutPause(handle) } != 0 {
                return Err("audio_error");
            }
            playback_position(handle).filter(|&frames| frames <= state.committed_frames)
        } else {
            Some(0)
        };
        state.paused_at = Some(Instant::now());
        Ok(())
    }

    /// Resume precisely the same queued PCM, including a partially played
    /// buffer. An old controller can never resume a newly opened device.
    pub fn resume(&self) -> Result<(), &'static str> {
        let mut state = self.0.lock().map_err(|_| "audio_error")?;
        if state.retired {
            return Err("cancelled");
        }
        let Some(at) = state.paused_at else {
            return Ok(());
        };
        if let Some(handle) = state.raw_handle() {
            if unsafe { waveOutRestart(handle) } != 0 {
                return Err("audio_error");
            }
        }
        state.paused_total += at.elapsed();
        state.paused_at = None;
        state.paused_frames = None;
        Ok(())
    }

    pub fn is_paused(&self) -> bool {
        self.0
            .lock()
            .map(|state| !state.retired && state.paused_at.is_some())
            .unwrap_or(false)
    }

    pub fn paused_frames(&self) -> Option<u64> {
        let state = self.0.lock().ok()?;
        (!state.retired && state.paused_at.is_some())
            .then_some(state.paused_frames)
            .flatten()
    }

    fn cursor(&self) -> Option<RenderCursor> {
        self.0.lock().ok()?.cursor()
    }

    fn attach(&self, handle: HWAVEOUT) -> Result<(), &'static str> {
        let mut state = self.0.lock().map_err(|_| "audio_error")?;
        if state.retired || state.handle.is_some() {
            return Err("audio_error");
        }
        if state.paused_at.is_some() && unsafe { waveOutPause(handle) } != 0 {
            return Err("audio_error");
        }
        state.handle = Some(handle.0 as usize);
        Ok(())
    }

    fn retire(&self) {
        let mut state = self.0.lock().unwrap_or_else(|error| error.into_inner());
        state.handle = None;
        state.retired = true;
        state.paused_at = None;
        state.paused_frames = None;
    }

    fn wait_clock(&self, now: Instant) -> Result<(Instant, Duration), &'static str> {
        let state = self.0.lock().map_err(|_| "audio_error")?;
        Ok((now, state.paused_duration(now)))
    }

    fn active_elapsed(
        &self,
        start: (Instant, Duration),
        now: Instant,
    ) -> Result<Duration, &'static str> {
        let state = self.0.lock().map_err(|_| "audio_error")?;
        Ok(now
            .saturating_duration_since(start.0)
            .saturating_sub(state.paused_duration(now).saturating_sub(start.1)))
    }
}

/// TTS can continue playing its existing buffers while the HTTP worker awaits
/// the next chunk. Observe the device cursor on a bounded, playback-only thread
/// instead of reporting samples as they arrive from the response.
struct SpectrumObservation {
    timeline: Arc<Mutex<PlaybackSpectrum>>,
    visible: Arc<dyn Fn() -> bool + Send + Sync>,
    stop: Arc<AtomicBool>,
    worker: JoinHandle<()>,
}
impl SpectrumObservation {
    fn start(
        control: PlaybackPause,
        visible: impl Fn() -> bool + Send + Sync + 'static,
        publish: impl Fn([f32; SPECTRUM_BANDS]) + Send + 'static,
    ) -> Option<Self> {
        let timeline = Arc::new(Mutex::new(PlaybackSpectrum::default()));
        let visible: Arc<dyn Fn() -> bool + Send + Sync> = Arc::new(visible);
        let stop = Arc::new(AtomicBool::new(false));
        let observed = timeline.clone();
        let display = visible.clone();
        let stopped = stop.clone();
        // The cursor query uses the per-turn lock and never sees a handle
        // already detached by its owner.
        let worker = thread::Builder::new()
            .name("AegisSpectrum".into())
            .spawn(move || {
                let quiet = [0.0; SPECTRUM_BANDS];
                let mut previous = None;
                let mut last_movement = Instant::now();
                while !stopped.load(Ordering::Acquire) {
                    if !display() {
                        if let Ok(mut timeline) = observed.lock() {
                            timeline.submit(&[], false);
                        }
                        previous = None;
                        last_movement = Instant::now();
                        thread::park_timeout(Duration::from_millis(160));
                        continue;
                    }
                    let levels = match control.cursor() {
                        Some(cursor) if cursor.paused_at.is_none() => {
                            if previous != Some(cursor.frames) {
                                previous = Some(cursor.frames);
                                last_movement = Instant::now();
                            }
                            if last_movement.elapsed() >= Duration::from_millis(100) {
                                quiet
                            } else {
                                observed
                                    .lock()
                                    .map(|mut timeline| timeline.at(cursor.frames))
                                    .unwrap_or(quiet)
                            }
                        }
                        _ => {
                            previous = None;
                            quiet
                        }
                    };
                    if !stopped.load(Ordering::Acquire) {
                        publish(levels);
                    }
                    thread::park_timeout(Duration::from_millis(40));
                }
            })
            .ok()?;
        Some(Self {
            timeline,
            visible,
            stop,
            worker,
        })
    }
    fn submit(&self, samples: &[i16]) {
        if let Ok(mut timeline) = self.timeline.lock() {
            timeline.submit(samples, (self.visible)());
        }
    }
    fn finish(self) {
        self.stop.store(true, Ordering::Release);
        self.worker.thread().unpark();
        let _ = self.worker.join();
    }
}

pub(crate) struct Output {
    handle: HWAVEOUT,
    event: Event,
    buffers: VecDeque<Buffer>,
    deadline: Instant,
    spectrum: Option<SpectrumObservation>,
    reference: Option<(RenderReference, u64)>,
    control: PlaybackPause,
}
impl Output {
    pub fn open() -> Result<Self, &'static str> {
        Self::open_with_pause(PlaybackPause::default())
    }

    pub fn open_with_pause(control: PlaybackPause) -> Result<Self, &'static str> {
        let event = Event::new()?;
        let mut handle = HWAVEOUT::default();
        if unsafe {
            waveOutOpen(
                Some(&mut handle),
                WAVE_MAPPER,
                &format(SPEECH_RATE),
                Some(completed as *const () as usize),
                Some(event.0 .0 as usize),
                CALLBACK_FUNCTION,
            )
        } != 0
        {
            return Err("speaker_unavailable");
        }
        if let Err(error) = control.attach(handle) {
            unsafe { waveOutClose(handle) };
            return Err(error);
        }
        Ok(Self {
            handle,
            event,
            buffers: VecDeque::new(),
            deadline: Instant::now() + Duration::from_secs(300),
            spectrum: None,
            reference: None,
            control,
        })
    }
    /// Exact audio actually submitted to the device, independent of UI
    /// visibility. The reference removes its query before the handle closes.
    pub fn observe_reference(&mut self, reference: RenderReference) {
        if self.reference.is_none() {
            let control = self.control.clone();
            let epoch = reference.begin_observed(move || control.cursor());
            self.reference = Some((reference, epoch));
        }
    }
    pub fn played_frames(&self) -> Option<u64> {
        self.control.cursor().map(|cursor| cursor.frames)
    }
    pub fn pause_control(&self) -> PlaybackPause {
        self.control.clone()
    }
    pub fn observe_spectrum(
        &mut self,
        visible: impl Fn() -> bool + Send + Sync + 'static,
        publish: impl Fn([f32; SPECTRUM_BANDS]) + Send + 'static,
    ) {
        // A visualization failure must never prevent the actual reply.
        if self.spectrum.is_none() {
            self.spectrum = SpectrumObservation::start(self.control.clone(), visible, publish);
        }
    }
    fn reclaim(&mut self, cancel: &CancellationToken) -> Result<(), &'static str> {
        let start = self.control.wait_clock(Instant::now())?;
        let buf = self.buffers.front().ok_or("audio_error")?;
        while !buf.done() {
            if cancel.is_cancelled() {
                return Err("cancelled");
            }
            if self.control.active_elapsed(start, Instant::now())? >= Duration::from_secs(3) {
                return Err("device_unavailable");
            }
            self.event.wait(cancel, self.deadline)?;
        }
        let hdr = self.buffers.front_mut().unwrap().header.get();
        let _control = self.control.0.lock().map_err(|_| "audio_error")?;
        if unsafe { waveOutUnprepareHeader(self.handle, hdr, HEADER) } != 0 {
            return Err("audio_error");
        }
        self.buffers.pop_front();
        Ok(())
    }

    fn wait_playing(&self, cancel: &CancellationToken) -> Result<(), &'static str> {
        loop {
            if cancel.is_cancelled() {
                return Err("cancelled");
            }
            if Instant::now() >= self.deadline {
                return Err("device_unavailable");
            }
            if !self.control.is_paused() {
                return Ok(());
            }
            self.event.wait(cancel, self.deadline)?;
        }
    }
    pub fn write(
        &mut self,
        samples: &[i16],
        cancel: &CancellationToken,
    ) -> Result<(), &'static str> {
        for chunk in samples.chunks(2400) {
            if cancel.is_cancelled() {
                return Err("cancelled");
            }
            while self.buffers.len() >= 4 {
                self.reclaim(cancel)?;
            }
            // The lock orders pause/restart with each submission. The owner
            // retains all four queued buffers and waits before making new ones.
            loop {
                self.wait_playing(cancel)?;
                let mut state = self.control.0.lock().map_err(|_| "audio_error")?;
                if state.paused_at.is_some() {
                    continue;
                }
                if cancel.is_cancelled() {
                    return Err("cancelled");
                }
                let buf = Buffer::new(chunk.to_vec().into_boxed_slice());
                if unsafe { waveOutPrepareHeader(self.handle, buf.header.get(), HEADER) } != 0 {
                    return Err("audio_error");
                }
                self.buffers.push_back(buf);
                let hdr = self.buffers.back_mut().unwrap().header.get();
                if cancel.is_cancelled() {
                    return Err("cancelled");
                }
                if unsafe { waveOutWrite(self.handle, hdr, HEADER) } != 0 {
                    return Err("audio_error");
                }
                state.committed_frames += chunk.len() as u64;
                break;
            }
            if let Some((reference, epoch)) = &self.reference {
                reference.submit(*epoch, chunk);
            }
            if let Some(observer) = &self.spectrum {
                observer.submit(chunk);
            }
        }
        Ok(())
    }
    pub fn finish(&mut self, cancel: &CancellationToken) -> Result<(), &'static str> {
        while !self.buffers.is_empty() {
            self.reclaim(cancel)?;
        }
        self.wait_playing(cancel)
    }
}
impl Drop for Output {
    fn drop(&mut self) {
        if let Some(observer) = self.spectrum.take() {
            observer.finish();
        }
        // Sample the final played cursor immediately before reset. The
        // reference's locked end removes its raw-handle query before close.
        if let Some((reference, epoch)) = self.reference.take() {
            reference.end(epoch);
        }
        self.control.retire();
        unsafe { waveOutReset(self.handle) };
        for buf in self.buffers.drain(..) {
            if unsafe { waveOutUnprepareHeader(self.handle, buf.header.get(), HEADER) } != 0 {
                std::mem::forget(buf);
            }
        }
        unsafe { waveOutClose(self.handle) };
    }
}

pub fn cue(cancel: &CancellationToken) -> Result<(), &'static str> {
    let mut output = Output::open()?;
    let samples: Vec<i16> = (0..720)
        .map(|i| {
            let phase = i as f32 / 720.;
            (2800.
                * (phase * std::f32::consts::PI).sin()
                * (phase * std::f32::consts::TAU * 24.).sin()) as i16
        })
        .collect();
    output.write(&samples, cancel)?;
    output.finish(cancel)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pending_pause_is_idempotent_and_retired_controller_cannot_restart() {
        let control = PlaybackPause::default();
        let clone = control.clone();
        assert!(!control.is_paused());
        clone.pause().unwrap();
        control.pause().unwrap();
        assert!(control.is_paused());
        assert_eq!(clone.paused_frames(), Some(0));
        clone.resume().unwrap();
        control.resume().unwrap();
        assert!(!control.is_paused());
        assert_eq!(clone.paused_frames(), None);
        control.retire();
        assert_eq!(clone.pause(), Err("cancelled"));
        assert_eq!(clone.resume(), Err("cancelled"));
    }

    #[test]
    fn progress_wait_excludes_pauses_without_changing_wall_time() {
        let control = PlaybackPause::default();
        let start = Instant::now();
        let before = control.wait_clock(start).unwrap();
        {
            let mut state = control.0.lock().unwrap();
            state.paused_at = Some(start + Duration::from_secs(1));
        }
        assert_eq!(
            control
                .active_elapsed(before, start + Duration::from_secs(9))
                .unwrap(),
            Duration::from_secs(1)
        );
        {
            let mut state = control.0.lock().unwrap();
            state.paused_total = Duration::from_secs(8);
            state.paused_at = None;
        }
        assert_eq!(
            control
                .active_elapsed(before, start + Duration::from_secs(10))
                .unwrap(),
            Duration::from_secs(2)
        );
        let later = control.wait_clock(start + Duration::from_secs(11)).unwrap();
        {
            let mut state = control.0.lock().unwrap();
            state.paused_at = Some(start + Duration::from_secs(12));
        }
        assert_eq!(
            control
                .active_elapsed(later, start + Duration::from_secs(20))
                .unwrap(),
            Duration::from_secs(1)
        );
    }

    #[test]
    #[ignore = "requires an installed Windows playback device and a readable PCM cursor"]
    fn pending_pause_blocks_output_and_restart_drains_original_buffers() {
        let cancel = CancellationToken::new();
        let control = PlaybackPause::default();
        control.pause().unwrap();
        let mut output = Output::open_with_pause(control.clone()).unwrap();
        let resume = control.clone();
        thread::scope(|scope| {
            scope.spawn(move || {
                thread::sleep(Duration::from_millis(100));
                assert_eq!(resume.paused_frames(), Some(0));
                resume.resume().unwrap();
            });
            output.write(&[0; 2400 * 4], &cancel).unwrap();
        });
        control.pause().unwrap();
        let position = control.paused_frames().expect("readable paused cursor");
        assert!(position <= 2400 * 4);
        thread::sleep(Duration::from_millis(130));
        assert_eq!(output.played_frames(), Some(position));
        assert_eq!(output.buffers.len(), 4);
        let resume = control.clone();
        thread::scope(|scope| {
            scope.spawn(move || {
                thread::sleep(Duration::from_millis(100));
                resume.resume().unwrap();
            });
            output.finish(&cancel).unwrap();
        });
        assert!(output.buffers.is_empty());
        assert_eq!(output.played_frames(), Some(2400 * 4));
        drop(output);
        assert_eq!(control.resume(), Err("cancelled"));
    }

    #[test]
    #[ignore = "requires an installed Windows playback device"]
    fn cancel_while_paused_releases_the_same_output_and_revokes_resume() {
        let cancel = CancellationToken::new();
        let control = PlaybackPause::default();
        let mut output = Output::open_with_pause(control.clone()).unwrap();
        output.write(&[0; 2400 * 4], &cancel).unwrap();
        control.pause().unwrap();
        let stop = cancel.clone();
        thread::scope(|scope| {
            scope.spawn(move || {
                thread::sleep(Duration::from_millis(100));
                stop.cancel();
            });
            assert_eq!(output.finish(&cancel), Err("cancelled"));
        });
        drop(output);
        assert_eq!(control.resume(), Err("cancelled"));
    }

    // Explicit bench invocation only. Discard two 20 ms blocks in memory and
    // play digital silence. No network requests, recording files or metrics.
    #[test]
    #[ignore = "requires a Windows PC with a microphone and a playback device"]
    fn default_devices_deliver_pcm_and_release_on_cancel() {
        let cancel = CancellationToken::new();
        let mut input = Input::open().expect("default Windows microphone");
        assert_eq!(input.read(&cancel).unwrap().len(), BLOCK);
        assert_eq!(input.read(&cancel).unwrap().len(), BLOCK);
        cancel.cancel();
        assert!(matches!(input.read(&cancel), Err("cancelled")));
        drop(input);
        let cancel = CancellationToken::new();
        let mut out = Output::open().expect("default Windows speaker");
        out.write(&[0; 2400], &cancel).unwrap();
        out.finish(&cancel).unwrap();
    }
}
