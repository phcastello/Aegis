fn main() {
    #[cfg(feature = "native-runtime")]
    tauri_build::build();
}
