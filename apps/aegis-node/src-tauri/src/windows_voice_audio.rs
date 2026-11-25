//! Windows multimedia's shared PCM devices perform conversion to the requested
//! sample rate. Buffers stay pinned until the driver returns their headers.
use crate::windows_voice_signal::BLOCK;
use std::{
    cell::UnsafeCell,
    collections::VecDeque,
    mem::size_of,
    sync::atomic::{AtomicBool, Ordering},
    time::{Duration, Instant},
};
use tokio_util::sync::CancellationToken;
use windows::{
    core::PSTR,
    Win32::{
        Foundation::{CloseHandle, HANDLE, WAIT_FAILED},
        Media::{Audio::*, MM_WIM_DATA, MM_WOM_DONE},
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

pub(crate) struct Output {
    handle: HWAVEOUT,
    event: Event,
    buffers: VecDeque<Buffer>,
    deadline: Instant,
}
impl Output {
    pub fn open() -> Result<Self, &'static str> {
        let event = Event::new()?;
        let mut handle = HWAVEOUT::default();
        if unsafe {
            waveOutOpen(
                Some(&mut handle),
                WAVE_MAPPER,
                &format(24000),
                Some(completed as *const () as usize),
                Some(event.0 .0 as usize),
                CALLBACK_FUNCTION,
            )
        } != 0
        {
            return Err("speaker_unavailable");
        }
        Ok(Self {
            handle,
            event,
            buffers: VecDeque::new(),
            deadline: Instant::now() + Duration::from_secs(300),
        })
    }
    fn reclaim(&mut self, cancel: &CancellationToken) -> Result<(), &'static str> {
        let deadline = self.deadline.min(Instant::now() + Duration::from_secs(3));
        let buf = self.buffers.front().ok_or("audio_error")?;
        while !buf.done() {
            self.event.wait(cancel, deadline)?;
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
        for chunk in samples.chunks(2400) {
            if cancel.is_cancelled() {
                return Err("cancelled");
            }
            while self.buffers.len() >= 4 {
                self.reclaim(cancel)?;
            }
            let buf = Buffer::new(chunk.to_vec().into_boxed_slice());
            if unsafe { waveOutPrepareHeader(self.handle, buf.header.get(), HEADER) } != 0 {
                return Err("audio_error");
            }
            self.buffers.push_back(buf);
            let hdr = self.buffers.back_mut().unwrap().header.get();
            if unsafe { waveOutWrite(self.handle, hdr, HEADER) } != 0 {
                return Err("audio_error");
            }
        }
        Ok(())
    }
    pub fn finish(&mut self, cancel: &CancellationToken) -> Result<(), &'static str> {
        while !self.buffers.is_empty() {
            self.reclaim(cancel)?;
        }
        Ok(())
    }
}
impl Drop for Output {
    fn drop(&mut self) {
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
