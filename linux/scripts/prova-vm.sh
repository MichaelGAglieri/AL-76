#!/bin/zsh
# AL-76 in the Linux test VM (VMware Fusion on the Mac), through SSH with a key made only for that VM.
#   linux/scripts/prova-vm.sh install [FILE.deb]   copies the .deb, installs it with apt, restarts AL-76 in the desktop session
#   linux/scripts/prova-vm.sh run                  restarts AL-76 (systemd --user unit, so it lives in the GNOME session)
#   linux/scripts/prova-vm.sh log                  the last lines of AL-76's log and of its unit
#   linux/scripts/prova-vm.sh ssh 'command'        anything else
# Settings: VM_VMX (the .vmx), VM_USER (the VM's account), VM_KEY (the SSH key). The VM's address comes from VMware Tools.
set -euo pipefail
cd "${0:A:h}/.."
vmx=${VM_VMX:-/Volumes/VMs/Ubuntu 64-bit Arm 26.04.vmwarevm/Ubuntu 64-bit Arm 26.04.vmx}
user=${VM_USER:-neo}
key=${VM_KEY:-$HOME/.ssh/al76_vm}
ip=$("/Applications/VMware Fusion.app/Contents/Library/vmrun" getGuestIPAddress "$vmx") || { echo "VM spenta o senza VMware Tools"; exit 1; }
opts=(-i $key -o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new)
vm() { ssh $opts $user@$ip "$@"; }
restart='systemctl --user stop al76-prova 2>/dev/null; systemctl --user reset-failed al76-prova 2>/dev/null; pkill -x al76 2>/dev/null; sleep 1
         systemd-run --user --unit al76-prova --collect /usr/bin/al76 >/dev/null && echo "AL-76 avviato nella sessione grafica"'
case ${1:-install} in
  install)
    deb=${2:-$(ls -t out/al76_*_arm64.deb | head -1)}
    scp -q $opts $deb $user@$ip:/tmp/al76.deb
    vm "sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --reinstall /tmp/al76.deb 2>&1 | tail -1; dpkg -s al76 | grep ^Version; $restart" ;;
  run) vm "$restart" ;;
  log) vm "tail -n ${N:-15} ~/.local/share/AL-76/diagnostica.log; journalctl --user -u al76-prova --no-pager -o cat | tail -5" ;;
  ssh) shift; vm "$@" ;;
  *) echo "uso: $0 install [FILE.deb] | run | log | ssh 'comando'"; exit 1 ;;
esac
