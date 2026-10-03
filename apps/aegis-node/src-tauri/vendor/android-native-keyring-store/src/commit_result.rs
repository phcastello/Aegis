// Aegis durability correction: a false Android commit result is a storage failure.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CommitFailure;
impl std::fmt::Display for CommitFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Android secure preference commit failed")
    }
}
impl std::error::Error for CommitFailure {}
pub fn require_commit(committed: bool) -> Result<bool, CommitFailure> {
    if committed {
        Ok(true)
    } else {
        Err(CommitFailure)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn successful_commit_is_confirmed() {
        assert_eq!(require_commit(true), Ok(true));
    }
    #[test]
    fn failed_commit_is_never_reported_as_success() {
        assert_eq!(require_commit(false), Err(CommitFailure));
    }
}
