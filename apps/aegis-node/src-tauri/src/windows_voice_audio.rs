//! Windows multimedia's shared PCM devices perform conversion to the requested
//! sample rate. Buffers stay pinned until the driver returns their headers.
use crate::windows_barge_in_signal::RenderReference;
use crate::windows_voice_signal::{PlaybackSpectrum, BLOCK, SPECTRUM_BANDS, SPEECH_RATE};
use std::{
    cell::UnsafeCell,
    collections::VecDeque,
    mem::size_of,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Condvar, Mutex,
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
const SPEECH_RESERVE_FRAMES: usize = SPEECH_RATE as usize * 160 / 1000;
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
        handle: HWAVEOUT,
        visible: impl Fn() -> bool + Send + Sync + 'static,
        publish: impl Fn([f32; SPECTRUM_BANDS]) + Send + 'static,
    ) -> Option<Self> {
        let timeline = Arc::new(Mutex::new(PlaybackSpectrum::default()));
        let visible: Arc<dyn Fn() -> bool + Send + Sync> = Arc::new(visible);
        let stop = Arc::new(AtomicBool::new(false));
        let observed = timeline.clone();
        let display = visible.clone();
        let stopped = stop.clone();
        // The raw handle stays valid until Drop joins this observer, before
        // resetting or closing the output device. No PCM or driver callback
        // state crosses the UI boundary.
        let raw_handle = handle.0 as usize;
        let worker = thread::Builder::new()
            .name("AegisSpectrum".into())
            .spawn(move || {
                let handle = HWAVEOUT(raw_handle as *mut _);
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
                    let levels = if let Some(position) = playback_position(handle) {
                        if previous != Some(position) {
                            previous = Some(position);
                            last_movement = Instant::now();
                        }
                        if last_movement.elapsed() >= Duration::from_millis(100) {
                            quiet
                        } else {
                            observed
                                .lock()
                                .map(|mut timeline| timeline.at(position))
                                .unwrap_or(quiet)
                        }
                    } else {
                        quiet
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

#[derive(Default)]
struct PlaybackState {
    // Access to the owner's handle ends under this lock before waveOutClose.
    handle: Option<usize>,
    submitted: u64,
    paused_at: Option<Instant>,
    paused_duration: Duration,
}

#[derive(Clone, Default)]
pub(crate) struct PlaybackControl {
    state: Arc<(Mutex<PlaybackState>, Condvar)>,
    stopped: CancellationToken,
}

impl PlaybackControl {
    pub fn pause(&self) -> Result<(), &'static str> {
        let mut state = self.state.0.lock().unwrap();
        if self.stopped.is_cancelled() || state.paused_at.is_some() {
            return Ok(());
        }
        if let Some(raw) = state.handle {
            if unsafe { waveOutPause(HWAVEOUT(raw as *mut _)) } != 0 {
                return Err("audio_error");
            }
        }
        state.paused_at = Some(Instant::now());
        Ok(())
    }

    pub fn resume(&self) -> Result<(), &'static str> {
        let mut state = self.state.0.lock().unwrap();
        if self.stopped.is_cancelled() {
            return Ok(());
        }
        if let Some(paused_at) = state.paused_at {
            if let Some(raw) = state.handle {
                if unsafe { waveOutRestart(HWAVEOUT(raw as *mut _)) } != 0 {
                    return Err("audio_error");
                }
            }
            state.paused_duration += paused_at.elapsed();
            state.paused_at = None;
            self.state.1.notify_all();
        }
        Ok(())
    }

    pub fn stop(&self) {
        let state = self.state.0.lock().unwrap();
        if !self.stopped.is_cancelled() {
            self.stopped.cancel();
            if let Some(raw) = state.handle {
                // Output keeps ownership of every buffer and frees it only on
                // the playback worker once WinMM returns the headers.
                unsafe { waveOutReset(HWAVEOUT(raw as *mut _)) };
            }
            self.state.1.notify_all();
        }
    }

    pub fn stopped(&self) -> &CancellationToken {
        &self.stopped
    }

    pub fn is_paused(&self) -> bool {
        self.state.0.lock().unwrap().paused_at.is_some()
    }

    pub fn has_output(&self) -> bool {
        !self.stopped.is_cancelled() && self.state.0.lock().unwrap().handle.is_some()
    }

    pub fn played_frames(&self) -> Option<u64> {
        let state = self.state.0.lock().unwrap();
        state
            .handle
            .and_then(|raw| playback_position(HWAVEOUT(raw as *mut _)))
            .filter(|position| *position <= state.submitted)
    }

    fn wait_running(&self, cancel: &CancellationToken) -> Result<Duration, &'static str> {
        let mut state = self.state.0.lock().unwrap();
        loop {
            if cancel.is_cancelled() {
                return Err("cancelled");
            }
            if self.stopped.is_cancelled() {
                return Err("playback_stopped");
            }
            if state.paused_at.is_none() {
                return Ok(state.paused_duration);
            }
            state = self
                .state
                .1
                .wait_timeout(state, Duration::from_millis(50))
                .unwrap()
                .0;
        }
    }
}

pub(crate) struct Output {
    handle: HWAVEOUT,
    event: Event,
    buffers: VecDeque<Buffer>,
    speech_reserve: Option<Vec<i16>>,
    control: PlaybackControl,
    spectrum: Option<SpectrumObservation>,
    reference: Option<(RenderReference, u64)>,
}
impl Output {
    pub fn open() -> Result<Self, &'static str> {
        Self::open_device(PlaybackControl::default(), None)
    }
    pub fn controlled(control: PlaybackControl) -> Result<Self, &'static str> {
        Self::open_device(control, Some(Vec::with_capacity(SPEECH_RESERVE_FRAMES)))
    }
    fn open_device(
        control: PlaybackControl,
        speech_reserve: Option<Vec<i16>>,
    ) -> Result<Self, &'static str> {
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
        let output = Self {
            handle,
            event,
            buffers: VecDeque::new(),
            speech_reserve,
            control,
            spectrum: None,
            reference: None,
        };
        {
            let mut state = output.control.state.0.lock().unwrap();
            if output.control.stopped.is_cancelled() {
                return Err("playback_stopped");
            }
            if state.paused_at.is_some() && unsafe { waveOutPause(handle) } != 0 {
                return Err("audio_error");
            }
            state.handle = Some(handle.0 as usize);
        }
        Ok(output)
    }
    /// Exact audio actually submitted to the device, independent of UI
    /// visibility. The reference removes its query before the handle closes.
    pub fn observe_reference(&mut self, reference: RenderReference) {
        if self.reference.is_none() {
            let raw = self.handle.0 as usize;
            let epoch = reference.begin(move || playback_position(HWAVEOUT(raw as *mut _)));
            self.reference = Some((reference, epoch));
        }
    }
    pub fn played_frames(&self) -> Option<u64> {
        self.control.played_frames()
    }
    pub fn observe_spectrum(
        &mut self,
        visible: impl Fn() -> bool + Send + Sync + 'static,
        publish: impl Fn([f32; SPECTRUM_BANDS]) + Send + 'static,
    ) {
        // A visualization failure must never prevent the actual reply.
        if self.spectrum.is_none() {
            self.spectrum = SpectrumObservation::start(self.handle, visible, publish);
        }
    }
    fn reclaim(&mut self, cancel: &CancellationToken) -> Result<(), &'static str> {
        let initial_pause = self.control.wait_running(cancel)?;
        let deadline = Instant::now() + Duration::from_secs(3);
        let buf = self.buffers.front().ok_or("audio_error")?;
        while !buf.done() {
            let paused = self
                .control
                .wait_running(cancel)?
                .saturating_sub(initial_pause);
            self.event.wait(cancel, deadline + paused)?;
        }
        let hdr = self.buffers.front_mut().unwrap().header.get();
        if unsafe { waveOutUnprepareHeader(self.handle, hdr, HEADER) } != 0 {
            return Err("audio_error");
        }
        self.buffers.pop_front();
        Ok(())
    }
    pub fn write(
        &mut self,
        samples: &[i16],
        cancel: &CancellationToken,
    ) -> Result<(), &'static str> {
        self.control.wait_running(cancel)?;
        let mut offset = 0;
        // Collect a fresh reserve after the last hardware buffer runs out.
        // Output::open keeps the short activation cue on the immediate path.
        if self.buffers.back().is_none_or(Buffer::done) {
            if let Some(reserve) = &mut self.speech_reserve {
                offset = (SPEECH_RESERVE_FRAMES - reserve.len()).min(samples.len());
                reserve.extend_from_slice(&samples[..offset]);
                if reserve.len() < SPEECH_RESERVE_FRAMES {
                    return Ok(());
                }
                self.submit_reserve(cancel)?;
            }
        }
        self.write_device(&samples[offset..], cancel)
    }
    fn submit_reserve(&mut self, cancel: &CancellationToken) -> Result<(), &'static str> {
        let Some(pending) = self
            .speech_reserve
            .as_mut()
            .filter(|samples| !samples.is_empty())
        else {
            return Ok(());
        };
        let mut pending = std::mem::take(pending);
        let result = self.write_device(&pending, cancel);
        pending.clear();
        self.speech_reserve = Some(pending);
        result
    }
    fn write_device(
        &mut self,
        samples: &[i16],
        cancel: &CancellationToken,
    ) -> Result<(), &'static str> {
        for chunk in samples.chunks(2400) {
            self.control.wait_running(cancel)?;
            while self.buffers.len() >= 4 {
                self.reclaim(cancel)?;
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
            {
                // Stop cannot reset the device between this check and the
                // submission. A concurrently paused submission stays queued.
                let mut state = self.control.state.0.lock().unwrap();
                if self.control.stopped.is_cancelled() {
                    return Err("playback_stopped");
                }
                if unsafe { waveOutWrite(self.handle, hdr, HEADER) } != 0 {
                    return Err("audio_error");
                }
                state.submitted += chunk.len() as u64;
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
        self.submit_reserve(cancel)?;
        while !self.buffers.is_empty() {
            self.reclaim(cancel)?;
        }
        Ok(())
    }
}
impl Drop for Output {
    fn drop(&mut self) {
        self.control.state.0.lock().unwrap().handle = None;
        if let Some(observer) = self.spectrum.take() {
            observer.finish();
        }
        // Sample the final played cursor immediately before reset. The
        // reference's locked end removes its raw-handle query before close.
        if let Some((reference, epoch)) = self.reference.take() {
            reference.end(epoch);
        }
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
    fn pause_before_first_audio_holds_worker_and_stop_wakes_it() {
        let playback = PlaybackControl::default();
        playback.pause().unwrap();
        let cancel = CancellationToken::new();
        thread::scope(|scope| {
            let (send, receive) = std::sync::mpsc::channel();
            let control = &playback;
            let cancel = &cancel;
            let worker = scope.spawn(move || send.send(control.wait_running(cancel)).unwrap());
            assert!(receive.recv_timeout(Duration::from_millis(30)).is_err());
            playback.resume().unwrap();
            assert!(receive
                .recv_timeout(Duration::from_secs(1))
                .unwrap()
                .is_ok());
            worker.join().unwrap();
        });
        playback.pause().unwrap();
        thread::scope(|scope| {
            let (send, receive) = std::sync::mpsc::channel();
            let control = &playback;
            let cancel = &cancel;
            let worker = scope.spawn(move || send.send(control.wait_running(cancel)).unwrap());
            playback.stop();
            assert_eq!(
                receive.recv_timeout(Duration::from_secs(1)).unwrap(),
                Err("playback_stopped")
            );
            worker.join().unwrap();
        });
    }

    #[test]
    #[ignore = "requires a Windows PCM output device; plays digital silence"]
    fn paused_device_resumes_its_original_queued_frames() {
        let cancel = CancellationToken::new();
        let playback = PlaybackControl::default();
        let mut output = Output::controlled(playback.clone()).unwrap();
        output.write(&[0; 2400 * 4], &cancel).unwrap();
        playback.pause().unwrap();
        thread::sleep(Duration::from_millis(50));
        let paused = playback.played_frames().unwrap();
        thread::sleep(Duration::from_millis(120));
        assert_eq!(playback.played_frames(), Some(paused));
        playback.resume().unwrap();
        output.finish(&cancel).unwrap();
        assert_eq!(output.played_frames(), Some(2400 * 4));
    }

    #[test]
    #[ignore = "requires a Windows PCM output device; plays digital silence"]
    fn speech_reserves_frames_after_a_gap_and_plays_short_tail_at_eof() {
        let cancel = CancellationToken::new();
        let control = PlaybackControl::default();
        let mut output = Output::controlled(control.clone()).unwrap();

        output.write(&[0; 1920], &cancel).unwrap();
        assert_eq!(control.state.0.lock().unwrap().submitted, 0);
        output.write(&[0; 1920], &cancel).unwrap();
        assert_eq!(control.state.0.lock().unwrap().submitted, 3840);
        while !output.buffers.is_empty() {
            output.reclaim(&cancel).unwrap();
        }

        output.write(&[0; 960], &cancel).unwrap();
        assert_eq!(control.state.0.lock().unwrap().submitted, 3840);
        output.finish(&cancel).unwrap();
        assert_eq!(output.played_frames(), Some(4800));
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
