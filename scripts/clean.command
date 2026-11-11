#!/usr/bin/env bash
# Removes build output from the working tree so the next build starts from scratch.
# Double-click it in Finder, or run it from Terminal:
#
#   scripts/clean.command              # list what will be removed, ask, then delete
#   scripts/clean.command --yes        # delete without asking (scripts, CI)
#   scripts/clean.command --dry-run    # only list what would be deleted
#   scripts/clean.command --ds-store   # also delete Finder's .DS_Store files
#
# Removes: bin/ and obj/ of every .NET project, artifacts/ (DMGs, installers, publish output),
# _site/ (website build), docs/images/raw/ (screenshot renders), TestResults/, __pycache__/.
# Never touches sources, .git, IDE settings (.idea, .vs) or local config (.claude).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DRY_RUN=false
DS_STORE=false
YES=false
for arg in "$@"; do
  case "$arg" in
    -n|--dry-run) DRY_RUN=true ;;
    --ds-store) DS_STORE=true ;;
    -y|--yes) YES=true ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $arg (see --help)" >&2; exit 2 ;;
  esac
done

targets=()
# bin/ and obj/ only next to a project file, so unrelated folders named "bin" are left alone
while IFS= read -r -d '' proj; do
  dir="$(dirname "$proj")"
  for sub in bin obj; do [[ -d "$dir/$sub" ]] && targets+=("$dir/$sub"); done
done < <(find "$ROOT" -path "$ROOT/.git" -prune -o -name "*.csproj" -print0)

for path in artifacts _site docs/images/raw; do
  [[ -e "$ROOT/$path" ]] && targets+=("$ROOT/$path")
done
while IFS= read -r -d '' dir; do targets+=("$dir"); done < <(
  find "$ROOT" -path "$ROOT/.git" -prune -o -type d \( -name "__pycache__" -o -name "TestResults" \) -print0)
if $DS_STORE; then
  while IFS= read -r -d '' f; do targets+=("$f"); done < <(
    find "$ROOT" -path "$ROOT/.git" -prune -o -name ".DS_Store" -type f -print0)
fi

if [[ ${#targets[@]} -eq 0 ]]; then
  echo "Already clean."
  exit 0
fi

echo "Sweepbox repo: $ROOT"
total_kb=0
for t in "${targets[@]}"; do
  kb=$(du -sk "$t" 2>/dev/null | cut -f1)
  total_kb=$((total_kb + ${kb:-0}))
  printf '  %8s  %s\n' "$((${kb:-0} / 1024)) MB" "${t#"$ROOT"/}"
done
echo "Total: about $((total_kb / 1024)) MB."

if $DRY_RUN; then
  echo "Dry run: nothing was removed."
  exit 0
fi
# Interactive (e.g. double-clicked in Finder): confirm first. Non-interactive runs need --yes.
if ! $YES; then
  if [[ -t 0 ]]; then
    read -r -p "Remove these? [y/N] " answer
    [[ "$answer" =~ ^[Yy]$ ]] || { echo "Nothing removed."; exit 0; }
  else
    echo "Not a terminal: pass --yes to remove without asking." >&2
    exit 1
  fi
fi

for t in "${targets[@]}"; do rm -rf -- "$t"; done
echo "Removed. Freed about $((total_kb / 1024)) MB."
