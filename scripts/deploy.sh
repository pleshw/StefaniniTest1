#!/usr/bin/env bash
# Deploys the stack for one environment over SSH, by image digest.
#
#   scripts/deploy.sh <environment> <repo> <tag>   deploy images ghcr.io/<repo>/<name>:<tag>
#   scripts/deploy.sh <environment> --rollback     re-apply the previous release
#
# Environment variables:
#   DEPLOY_HOST      user@host of the target machine (needs Docker with the compose plugin)
#   DEPLOY_SSH_KEY   private key for DEPLOY_HOST
#   ENV_FILE         content of the compose .env file (deploy only)
#
# Remote layout under ~/orders-platform/<environment>:
#   deploy/                 copy of the repository's deploy/ directory (compose files and mounted config)
#   deploy/.env             ENV_FILE
#   releases/current.env    image references of the running release, one <SERVICE>_IMAGE=<ref>@<digest> per image
#   releases/previous.env   image references of the release before it
#
# Database migrations are backward compatible, so a rollback only re-applies service images.
set -euo pipefail

usage() {
  echo "usage: $0 <staging|production> <repo> <tag> | $0 <staging|production> --rollback" >&2
  exit 2
}

[[ $# -ge 2 ]] || usage
environment="$1"
[[ "${environment}" == "staging" || "${environment}" == "production" ]] || usage
mode="deploy"
if [[ "$2" == "--rollback" ]]; then
  mode="rollback"
else
  [[ $# -eq 3 ]] || usage
  repo="$2"
  tag="$3"
fi

: "${DEPLOY_HOST:?DEPLOY_HOST is required}"
: "${DEPLOY_SSH_KEY:?DEPLOY_SSH_KEY is required}"
if [[ "${mode}" == "deploy" ]]; then
  : "${ENV_FILE:?ENV_FILE is required}"
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "${work_dir}"' EXIT

key_file="${work_dir}/id"
umask 077
printf '%s\n' "${DEPLOY_SSH_KEY}" > "${key_file}"
ssh_options=(-i "${key_file}" -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile="${work_dir}/known_hosts")
remote_dir="orders-platform/${environment}"
compose_file="deploy/compose.${environment}.yml"

# The remote command string is built on the client on purpose.
# shellcheck disable=SC2029
remote() {
  ssh "${ssh_options[@]}" "${DEPLOY_HOST}" "$@"
}

image_variable() {
  local name="$1"
  echo "$(echo "${name}" | tr '[:lower:]-' '[:upper:]_')_IMAGE"
}

write_release_file() {
  local file="${work_dir}/current.env"
  : > "${file}"
  while read -r name _; do
    local reference="ghcr.io/${repo}/${name}:${tag}"
    local digest
    digest="$(docker buildx imagetools inspect "${reference}" --format '{{.Manifest.Digest}}')"
    echo "$(image_variable "${name}")=ghcr.io/${repo}/${name}@${digest}" >> "${file}"
  done < <("${repo_root}/scripts/images.sh")
}

compose() {
  remote "cd ${remote_dir} && docker compose -f ${compose_file} --env-file deploy/.env --env-file releases/current.env $*"
}

if [[ "${mode}" == "deploy" ]]; then
  write_release_file

  remote "mkdir -p ${remote_dir}/deploy ${remote_dir}/releases"
  tar -C "${repo_root}/deploy" -cf - . | remote "tar -xf - -C ${remote_dir}/deploy"
  printf '%s\n' "${ENV_FILE}" | remote "cat > ${remote_dir}/deploy/.env"

  remote "cd ${remote_dir} && if [ -f releases/current.env ]; then cp releases/current.env releases/previous.env; fi"
  remote "cat > ${remote_dir}/releases/current.env" < "${work_dir}/current.env"

  if compose "config --services" | grep -qx "orders-migrator"; then
    compose "run --rm orders-migrator"
  fi
else
  remote "cd ${remote_dir} && test -f releases/previous.env" || {
    echo "No previous release recorded for ${environment}; nothing to roll back to." >&2
    exit 1
  }
  remote "cd ${remote_dir} && cp releases/previous.env releases/current.env"
fi

compose "up -d --wait --remove-orphans"
echo "${mode} of ${environment} finished"
