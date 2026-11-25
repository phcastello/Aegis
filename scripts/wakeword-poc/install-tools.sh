#!/usr/bin/env bash
# Install pinned PoC tools only; no application dependencies.
set -euo pipefail

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
  echo 'This installer supports Linux x86_64. See the report for other platforms.' >&2
  exit 1
fi

tool_root="${1:-${XDG_CACHE_HOME:-$HOME/.cache}/aegis-wakeword-poc/tools}"
mkdir -p "$tool_root/bin"
tool_root="$(cd "$tool_root" && pwd)"
binary="$tool_root/bin/rustpotter-cli"
expected_sha='481adafafcb8cc7071fc7ec2450d9e1a476539cd4aa7cde0ab99f8ffd4059b7d'
url='https://github.com/GiviMAD/rustpotter-cli/releases/download/v3.0.2/rustpotter-cli_debian_x86_64'

if [[ ! -f "$binary" ]]; then
  download="$(mktemp "$tool_root/.rustpotter-cli.XXXXXX")"
  trap 'rm -f "$download"' EXIT
  curl --fail --location --retry 2 "$url" --output "$download"
  printf '%s  %s\n' "$expected_sha" "$download" | sha256sum --check --status
  chmod 755 "$download"
  mv "$download" "$binary"
fi
printf '%s  %s\n' "$expected_sha" "$binary" | sha256sum --check --status
"$binary" --version
printf 'RUSTPOTTER=%s\n' "$binary"

# Static FFmpeg/FFprobe distribution linked by ffmpeg.org. The release URL can
# change; the fixed digest below fails closed if upstream replaces the archive.
ffmpeg_archive="$tool_root/ffmpeg.tar.xz"
ffmpeg_sha='abda8d77ce8309141f83ab8edf0596834087c52467f6badf376a6a2a4c87cf67'
ffmpeg_url='https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz'
if [[ ! -f "$ffmpeg_archive" ]]; then
  download="$(mktemp "$tool_root/.ffmpeg.XXXXXX")"
  trap 'rm -f "$download"' EXIT
  curl --fail --location --retry 2 "$ffmpeg_url" --output "$download"
  printf '%s  %s\n' "$ffmpeg_sha" "$download" | sha256sum --check --status
  mv "$download" "$ffmpeg_archive"
fi
printf '%s  %s\n' "$ffmpeg_sha" "$ffmpeg_archive" | sha256sum --check --status

extract_dir="$(mktemp -d "$tool_root/.ffmpeg-extract.XXXXXX")"
trap 'rm -rf "$extract_dir"; rm -f "${download:-}"' EXIT
tar --extract --xz --file "$ffmpeg_archive" --directory "$extract_dir" \
  --strip-components=1 --no-same-owner \
  ffmpeg-7.0.2-amd64-static/ffmpeg ffmpeg-7.0.2-amd64-static/ffprobe \
  ffmpeg-7.0.2-amd64-static/GPLv3.txt
for tool in ffmpeg ffprobe; do
  target="$tool_root/bin/$tool"
  if [[ -e "$target" ]]; then
    cmp --silent "$extract_dir/$tool" "$target"
  else
    install -m 755 "$extract_dir/$tool" "$target"
  fi
  "$target" -version | sed -n '1p'
  sha256sum "$target"
  printf '%s=%s\n' "${tool^^}" "$target"
done
if [[ -e "$tool_root/ffmpeg-GPLv3.txt" ]]; then
  cmp --silent "$extract_dir/GPLv3.txt" "$tool_root/ffmpeg-GPLv3.txt"
else
  install -m 644 "$extract_dir/GPLv3.txt" "$tool_root/ffmpeg-GPLv3.txt"
fi
