#!/bin/bash
check() { return 0; }
depends() { echo kernel-modules; }
installkernel() { instmods drm; }
install() {
    inst_multiple modprobe
    inst_simple /lib/firmware/edid/edid-phn16s.bin
    inst_hook cmdline 01 "$moddir/intel-edid.sh"
}
