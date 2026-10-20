//! Windows notification actions start quietly; only a resolved body-open shows the UI.

pub fn background_launch(args: &[String]) -> bool {
    args.iter().any(|argument| {
        matches!(argument.as_str(), "--autostart" | "--reminder-action")
            || argument.starts_with("aegis-reminder://")
    })
}

pub fn hide_after_activation(args: &[String], opened_window: bool) -> bool {
    background_launch(args) && !opened_window
}

#[cfg(test)]
mod tests {
    use super::*;
    fn args(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| (*value).to_owned()).collect()
    }

    #[test]
    fn a_resolved_cold_body_open_is_not_hidden_after_activation() {
        for uri in [
            "aegis-reminder://open",
            "aegis-reminder://action/observed-open-id",
        ] {
            let launch = args(&["aegis-node.exe", "--reminder-action", uri]);
            assert!(background_launch(&launch));
            assert!(!hide_after_activation(&launch, true));
        }
    }

    #[test]
    fn acknowledgement_snooze_autostart_and_invalid_actions_remain_in_background() {
        for launch in [
            args(&["--autostart"]),
            args(&[
                "--reminder-action",
                "aegis-reminder://action/observed-ack-id",
            ]),
            args(&[
                "--reminder-action",
                "aegis-reminder://action/observed-snooze-id",
            ]),
            args(&["aegis-reminder://invalid"]),
        ] {
            assert!(hide_after_activation(&launch, false));
        }
        assert!(!background_launch(&args(&["aegis-node.exe"])));
        assert!(!hide_after_activation(&args(&["aegis-node.exe"]), false));
    }
}
