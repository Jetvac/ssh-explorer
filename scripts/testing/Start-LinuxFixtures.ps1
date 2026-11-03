param([ValidateSet('a','b','all')][string]$Selection='all')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location -LiteralPath $root
$qemu = Join-Path $root '.tools\vm\qemu\qemu-system-x86_64.exe'
$image = Join-Path $root '.tools\vm\qemu\qemu-img.exe'
if (-not (Test-Path -LiteralPath $qemu)) { throw 'QEMU development fixture tools are missing in .tools/vm/qemu.' }
foreach ($node in @(@{Name='Linux A';Key='a';Ssh=22222;Serial=22223},@{Name='Linux B';Key='b';Ssh=22232;Serial=22233})) {
    if($Selection -ne 'all' -and $Selection -ne $node.Key){continue}
    $directory=Join-Path $root ('.tools\iteration2\linux-'+$node.Key)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    if (Get-NetTCPConnection -State Listen -LocalPort @($node.Ssh,$node.Serial) -ErrorAction SilentlyContinue) { throw 'A test port is already occupied.' }
    $relative='.tools/v02-linux-'+$node.Key+'.qcow2'
    if (Test-Path -LiteralPath $relative) { throw 'Refusing to overwrite an existing fixture disk.' }
    & $image create -f qcow2 $relative 8G
    if($LASTEXITCODE){throw 'Fixture disk creation failed.'}
    $arguments=@('-L','.tools/vm/qemu/share','-machine','q35','-accel','tcg','-m','768','-smp','2','-display','none','-monitor','none','-serial',("tcp:127.0.0.1:$($node.Serial),server=on,wait=off"),'-kernel','.tools/vm/alpine-iso/boot/vmlinuz-virt','-initrd','.tools/vm/alpine-iso/boot/initramfs-virt','-append','"console=ttyS0 alpine_dev=cdrom:iso9660 modules=loop,squashfs,sd-mod,virtio quiet"','-cdrom','.tools/vm/alpine.iso','-drive',("file=$relative,if=virtio,format=qcow2"),'-netdev',("user,id=n1,hostfwd=tcp:127.0.0.1:$($node.Ssh)-:22"),'-device','virtio-net-pci,netdev=n1','-no-reboot')
    $process=Start-Process -FilePath $qemu -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $directory 'qemu-stderr.log')
    $process.Id | Set-Content -LiteralPath (Join-Path $directory 'qemu.pid')
    & python (Join-Path $PSScriptRoot 'provision-linux.py') --name ('sshx-lab-'+$node.Key) --serial-port $node.Serial --ssh-port $node.Ssh --directory $directory
    if($LASTEXITCODE){throw 'Linux fixture provisioning failed.'}
}
