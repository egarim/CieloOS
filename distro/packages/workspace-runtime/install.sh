#!/usr/bin/env bash
set -euo pipefail

install -d /opt/workspace-runtime
install -d /opt/workspace-runtime/config
install -d /opt/workspace-runtime/surfaces
install -d /opt/workspace-runtime/services
install -d /opt/workspace-runtime/bin
install -d /opt/workspace-runtime/distro/config
install -d /opt/workspace-runtime/distro/models/providers
install -d /opt/workspace-runtime/models

cp config/branding.json /opt/workspace-runtime/config/branding.json
cp surfaces/*.surface.json /opt/workspace-runtime/surfaces/
install -m 0644 distro/config/console-setup /etc/default/console-setup
cp distro/config/local-inference.json /opt/workspace-runtime/distro/config/local-inference.json
cp distro/services/*.service /etc/systemd/system/
cp distro/models/registry.json /opt/workspace-runtime/distro/models/registry.json
cp distro/models/providers/*.json /opt/workspace-runtime/distro/models/providers/
install -m 0755 distro/packages/local-inference/workspace-model /usr/local/sbin/workspace-model

if [[ -d runtime/bin ]]; then
  cp -a runtime/bin/. /opt/workspace-runtime/bin/

  runtime_executable=""
  for candidate in \
    runtime/bin/workspace-runtime-api \
    runtime/bin/WorkspaceRuntime.Api \
    runtime/bin/workspaceruntime.api; do
    if [[ -f "$candidate" ]]; then
      runtime_executable="$candidate"
      break
    fi
  done

  if [[ -z "$runtime_executable" ]]; then
    echo "Published runtime executable was not found." >&2
    exit 1
  fi

  install -m 0755 "$runtime_executable" /opt/workspace-runtime/bin/workspace-runtime-api

  if [[ -f runtime/bin/workspace-installer ]]; then
    install -m 0755 runtime/bin/workspace-installer /usr/local/bin/workspace-installer
  fi

  if [[ -f runtime/bin/workspace-agent ]]; then
    install -m 0755 runtime/bin/workspace-agent /usr/local/bin/workspace-agent
  fi
fi

systemctl daemon-reload
systemctl enable agent-policy.service
systemctl enable agent-executor.service
systemctl enable agent-runtime.service
systemctl enable local-inference.service

echo "Workspace runtime services installed. Reboot or start services manually."
