//! Stream Open v2 using the Windows CPU inference engine and a pinned private model.
use sha2::{Digest, Sha256};
use std::{
    ffi::OsString,
    fs::{self, File},
    io::{Cursor, Read, Write},
    os::windows::ffi::OsStringExt,
    path::{Path, PathBuf},
};
use windows::{
    core::{Interface, HSTRING, PCWSTR, PWSTR},
    Storage::Streams::{DataWriter, InMemoryRandomAccessStream, RandomAccessStreamReference},
    Win32::{
        Foundation::RPC_E_CHANGED_MODE,
        System::WinRT::{
            RoInitialize, RoUninitialize,
            ML::{ILearningModelSessionOptionsNative, ILearningModelSessionOptionsNative1},
            RO_INIT_MULTITHREADED,
        },
        UI::Controls::Dialogs::*,
    },
    AI::MachineLearning::{
        LearningModel, LearningModelBinding, LearningModelDevice, LearningModelDeviceKind,
        LearningModelSession, LearningModelSessionOptions, TensorFloat,
    },
};

const MODEL_FILE: &str = "aegis-open-v2.zip";
const BUNDLE_DIGEST: &str = "3502c2bf98200daaec17b772f273942064ba3fd7431d62b21fdd77fb9f33c719";
const LIMIT: u64 = 4 * 1024 * 1024;
const BLOCK: usize = 1280;

fn verified_bytes(path: &Path) -> Result<Vec<u8>, &'static str> {
    let mut data = Vec::new();
    File::open(path)
        .map_err(|_| "model_missing")?
        .take(LIMIT + 1)
        .read_to_end(&mut data)
        .map_err(|_| "model_read_failed")?;
    if data.len() as u64 > LIMIT || format!("{:x}", Sha256::digest(&data)) != BUNDLE_DIGEST {
        return Err("model_invalid");
    }
    Ok(data)
}

pub fn installed(dir: &Path) -> bool {
    dir.join(MODEL_FILE).is_file()
}

fn pick_bundle() -> Result<Option<PathBuf>, &'static str> {
    let mut path = vec![0u16; 32768];
    let filter: Vec<u16> = "Modelo privado da Aegis (*.zip)\0*.zip\0\0"
        .encode_utf16()
        .collect();
    let mut dialog = OPENFILENAMEW {
        lStructSize: std::mem::size_of::<OPENFILENAMEW>() as u32,
        lpstrFilter: PCWSTR(filter.as_ptr()),
        lpstrFile: PWSTR(path.as_mut_ptr()),
        nMaxFile: path.len() as u32,
        Flags: OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER | OFN_DONTADDTORECENT,
        ..Default::default()
    };
    // Both buffers outlive this synchronous call.
    if !unsafe { GetOpenFileNameW(&mut dialog) }.as_bool() {
        return if unsafe { CommDlgExtendedError() }.0 == 0 {
            Ok(None)
        } else {
            Err("picker_failed")
        };
    }
    let end = path.iter().position(|n| *n == 0).ok_or("picker_failed")?;
    Ok(Some(OsString::from_wide(&path[..end]).into()))
}

struct ImportApartment(bool);
impl ImportApartment {
    fn enter() -> Result<Self, &'static str> {
        match unsafe { RoInitialize(RO_INIT_MULTITHREADED) } {
            Ok(()) => Ok(Self(true)),
            Err(e) if e.code() == RPC_E_CHANGED_MODE => Ok(Self(false)),
            Err(_) => Err("model_runtime_unavailable"),
        }
    }
}
impl Drop for ImportApartment {
    fn drop(&mut self) {
        if self.0 {
            unsafe { RoUninitialize() };
        }
    }
}

pub fn import(dir: &Path) -> Result<(), &'static str> {
    let Some(path) = pick_bundle()? else {
        return Ok(());
    };
    let data = verified_bytes(&path)?;
    let _apartment = ImportApartment::enter()?;
    let mut candidate = Detector::from_bytes(&data)?;
    candidate.accept(&[0; BLOCK])?;
    fs::create_dir_all(dir).map_err(|_| "model_save_failed")?;
    let mut staging = tempfile::NamedTempFile::new_in(dir).map_err(|_| "model_save_failed")?;
    staging
        .write_all(&data)
        .and_then(|_| staging.as_file().sync_all())
        .map_err(|_| "model_save_failed")?;
    staging
        .persist(dir.join(MODEL_FILE))
        .map_err(|_| "model_save_failed")?;
    Ok(())
}

struct Graph {
    session: LearningModelSession,
    binding: LearningModelBinding,
    input: HSTRING,
    output: HSTRING,
}
impl Graph {
    fn load(data: &[u8]) -> windows::core::Result<Self> {
        let stream = InMemoryRandomAccessStream::new()?;
        let writer = DataWriter::CreateDataWriter(&stream.GetOutputStreamAt(0)?)?;
        writer.WriteBytes(data)?;
        writer.StoreAsync()?.join()?;
        writer.DetachStream()?;
        stream.Seek(0)?;
        let model = LearningModel::LoadFromStream(&RandomAccessStreamReference::CreateFromStream(
            &stream,
        )?)?;
        let input = model.InputFeatures()?.GetAt(0)?.Name()?;
        let output = model.OutputFeatures()?.GetAt(0)?.Name()?;
        let device = LearningModelDevice::Create(LearningModelDeviceKind::Cpu)?;
        // One lightweight streaming inference every 80 ms. Avoid spinning pools on each graph.
        let options = LearningModelSessionOptions::new()?;
        if let Ok(native) = options.cast::<ILearningModelSessionOptionsNative>() {
            unsafe {
                native.SetIntraOpNumThreadsOverride(1)?;
            }
        }
        if let Ok(native) = options.cast::<ILearningModelSessionOptionsNative1>() {
            unsafe {
                native.SetIntraOpThreadSpinning(0)?;
            }
        }
        let session = LearningModelSession::CreateFromModelOnDeviceWithSessionOptions(
            &model, &device, &options,
        )?;
        let binding = LearningModelBinding::CreateFromSession(&session)?;
        Ok(Self {
            session,
            binding,
            input,
            output,
        })
    }

    fn run(
        &self,
        shape: &[i64],
        samples: &[f32],
        max_output: usize,
    ) -> Result<Vec<f32>, &'static str> {
        let evaluate = || -> windows::core::Result<_> {
            // Clear the previous output allocation: the first mel block has five rows, later blocks have eight.
            self.binding.Clear()?;
            let tensor = TensorFloat::CreateFromShapeArrayAndDataArray(shape, samples)?;
            self.binding.Bind(&self.input, &tensor)?;
            let result = self.session.Evaluate(&self.binding, &HSTRING::new())?;
            let output: TensorFloat = result.Outputs()?.Lookup(&self.output)?.cast()?;
            let values = output.GetAsVectorView()?;
            let size = values.Size()? as usize;
            if size == 0 || size > max_output {
                return Ok(Vec::new());
            }
            let mut data = vec![0.0; size];
            let read = values.GetMany(0, &mut data)? as usize;
            data.truncate(read);
            Ok(data)
        };
        let values = evaluate().map_err(|_| "model_inference_failed")?;
        if values.is_empty() || values.iter().any(|x| !x.is_finite()) {
            return Err("model_invalid_output");
        }
        Ok(values)
    }
}

pub struct Detector {
    mel: Graph,
    embedding: Graph,
    keyword: Graph,
    mel_history: Vec<f32>,
    features: Vec<f32>,
    pending: Vec<i16>,
    overlap: Vec<i16>,
    frames: u64,
    last_hit: Option<u64>,
    below: u8,
    armed: bool,
}
impl Detector {
    pub fn open(dir: &Path) -> Result<Self, &'static str> {
        Self::from_bytes(&verified_bytes(&dir.join(MODEL_FILE))?)
    }

    fn from_bytes(data: &[u8]) -> Result<Self, &'static str> {
        let mut zip = zip::ZipArchive::new(Cursor::new(data)).map_err(|_| "model_invalid")?;
        let mut entry = |name| {
            let mut bytes = Vec::new();
            zip.by_name(name)
                .map_err(|_| "model_invalid")?
                .read_to_end(&mut bytes)
                .map_err(|_| "model_invalid")?;
            Ok::<_, &'static str>(bytes)
        };
        let mut graph = |name| Graph::load(&entry(name)?).map_err(|_| "model_runtime_unavailable");
        let mel = graph("melspectrogram.onnx")?;
        let embedding = graph("embedding_model.onnx")?;
        let keyword = graph("aegis.onnx")?;
        let seed = entry("initial_features.f32le")?;
        let features: Vec<_> = seed
            .chunks_exact(4)
            .map(|p| f32::from_le_bytes(p.try_into().unwrap()))
            .collect();
        if features.len() != 16 * 96 {
            return Err("model_invalid");
        }
        Ok(Self {
            mel,
            embedding,
            keyword,
            mel_history: vec![1.; 76 * 32],
            features,
            pending: Vec::with_capacity(BLOCK),
            overlap: Vec::with_capacity(480),
            frames: 0,
            last_hit: None,
            below: 0,
            armed: true,
        })
    }

    pub fn accept(&mut self, mut pcm: &[i16]) -> Result<Option<f32>, &'static str> {
        let mut hit = None;
        while !pcm.is_empty() {
            let count = (BLOCK - self.pending.len()).min(pcm.len());
            self.pending.extend_from_slice(&pcm[..count]);
            pcm = &pcm[count..];
            if self.pending.len() != BLOCK {
                break;
            }
            let raw: Vec<_> = self
                .overlap
                .iter()
                .chain(&self.pending)
                .map(|&s| f32::from(s))
                .collect();
            self.overlap.clear();
            self.overlap.extend_from_slice(&self.pending[BLOCK - 480..]);
            self.pending.clear();
            let mut mel = self.mel.run(&[1, raw.len() as i64], &raw, 8 * 32)?;
            if mel.len() % 32 != 0 {
                return Err("model_invalid_output");
            }
            for value in &mut mel {
                *value = *value / 10. + 2.;
            }
            append(&mut self.mel_history, &mel);
            let embedding = self.embedding.run(&[1, 76, 32, 1], &self.mel_history, 96)?;
            if embedding.len() != 96 {
                return Err("model_invalid_output");
            }
            append(&mut self.features, &embedding);
            let score = self.keyword.run(&[1, 16, 96], &self.features, 1)?[0];
            if !(0.0..=1.0).contains(&score) {
                return Err("model_invalid_output");
            }
            self.frames += 1;
            if self.frames <= 5 {
                continue;
            }
            self.below = if score < 0.30 {
                self.below.saturating_add(1)
            } else {
                0
            };
            if self.below >= 2 {
                self.armed = true;
            }
            if self.armed
                && score >= 0.60
                && self.last_hit.is_none_or(|last| self.frames - last >= 13)
            {
                self.armed = false;
                self.last_hit = Some(self.frames);
                hit = Some(score);
            }
        }
        Ok(hit)
    }
}
fn append(history: &mut [f32], values: &[f32]) {
    let start = history.len() - values.len();
    history.copy_within(values.len().., 0);
    history[start..].copy_from_slice(values);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn damaged_private_archive_is_rejected_before_inference() {
        let dir = tempfile::tempdir().unwrap();
        fs::write(dir.path().join(MODEL_FILE), b"invalid").unwrap();
        assert!(matches!(Detector::open(dir.path()), Err("model_invalid")));
    }

    #[test]
    #[ignore = "requires the pinned private wakeword bundle and Windows ML"]
    fn streaming_mel_binding_accepts_both_startup_and_steady_state_shapes() {
        let path =
            std::env::var_os("AEGIS_WINDOWS_WAKE_BUNDLE").expect("set AEGIS_WINDOWS_WAKE_BUNDLE");
        let _apartment = ImportApartment::enter().unwrap();
        let data = verified_bytes(Path::new(&path)).unwrap();
        let mut model = Detector::from_bytes(&data).unwrap();
        for _ in 0..16 {
            assert_eq!(model.accept(&[0; BLOCK / 4]).unwrap(), None);
        }
        assert_eq!(model.frames, 4);
    }
}
