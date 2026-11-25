//! Passive voice status on the interactive desktop, including when chat is in the tray.
use std::cell::Cell;
use windows::{
    core::{w, HSTRING},
    Win32::{
        Foundation::{HWND, LPARAM, RECT, WPARAM},
        Graphics::Gdi::{GetStockObject, DEFAULT_GUI_FONT},
        UI::WindowsAndMessaging::*,
    },
};
thread_local! { static PANEL: Cell<Option<HWND>> = const { Cell::new(None) }; }

pub fn update(app: &tauri::AppHandle, status: &str) {
    let label = match status {
        "capturing" => Some("Aegis  ·  Escutando"),
        "processing" => Some("Aegis  ·  Preparando resposta"),
        "speaking" => Some("Aegis  ·  Falando"),
        "continuation" => Some("Aegis  ·  Aguardando você"),
        _ => None,
    };
    let _ = app.run_on_main_thread(move || {
        PANEL.with(|slot| {
            // The window is owned and destroyed exclusively by the desktop UI thread.
            unsafe {
                let Some(label) = label else {
                    if let Some(hwnd) = slot.take() {
                        let _ = DestroyWindow(hwnd);
                    }
                    return;
                };
                if let Some(hwnd) = slot.get() {
                    let _ = SetWindowTextW(hwnd, &HSTRING::from(label));
                    return;
                }
                let mut work = RECT::default();
                if SystemParametersInfoW(
                    SPI_GETWORKAREA,
                    0,
                    Some((&raw mut work).cast()),
                    SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
                )
                .is_err()
                {
                    return;
                }
                // STATIC provides the system text color/background for high contrast.
                let Ok(hwnd) = CreateWindowExW(
                    WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                    w!("STATIC"),
                    &HSTRING::from(label),
                    WS_POPUP | WINDOW_STYLE(0x0201), // SS_CENTERIMAGE | SS_CENTER
                    work.right - 252,
                    work.bottom - 72,
                    232,
                    48,
                    None,
                    None,
                    None,
                    None,
                ) else {
                    return;
                };
                SendMessageW(
                    hwnd,
                    WM_SETFONT,
                    Some(WPARAM(GetStockObject(DEFAULT_GUI_FONT).0 as usize)),
                    Some(LPARAM(1)),
                );
                let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                slot.set(Some(hwnd));
            }
        })
    });
}
