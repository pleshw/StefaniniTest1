#!/usr/bin/env bash
# Prints "<name> <Dockerfile>" for every image in deploy/images.txt whose Dockerfile exists.
# Usage: scripts/images.sh | cut -d' ' -f1                                  (names)
#        scripts/images.sh | jq -Rcn '[inputs | split(" ") | {name: .[0], file: .[1]}]'   (matrix)
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

while read -r name file; do
  [[ -z "${name}" || "${name}" == \#* ]] && continue
  if [[ -f "${file}" ]]; then
    echo "${name} ${file}"
  fi
done < deploy/images.txt
