use minisign_verify::{PublicKey, Signature};

fn main() {
    let args: Vec<_> = std::env::args().collect();
    assert_eq!(args.len(), 5, "Expected installer, public key, signature and version");
    let data = std::fs::read(&args[1]).expect("Installer must be readable");
    let key = PublicKey::from_file(&args[2]).expect("Trusted updater public key must be valid");
    let signature = Signature::from_file(&args[3]).expect("Signature must be valid");
    key.verify(&data, &signature, true)
        .expect("Installer must verify against the existing updater key");
    assert!(signature.trusted_comment().split('\t').any(|field| field == format!("version:{}", args[4])),
        "Signature must bind the preview version");
    println!("Windows updater signature and signed preview version PASS.");
}
